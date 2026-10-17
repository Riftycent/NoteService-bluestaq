using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Attachments;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Attachments;

public class AttachmentServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IBlobStore _blobs = Substitute.For<IBlobStore>();
    private readonly AttachmentOptions _options = new() { MaxBytes = 1024, AllowedContentTypes = ["text/plain", "image/png"] };

    public void Dispose() => _db.Dispose();

    private (AttachmentService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        var noteAccess = new NoteAccessService(context, new TeamAccess(context, _db.CurrentUser), _db.CurrentUser);
        return (new AttachmentService(context, noteAccess, _blobs, _db.CurrentUser, _db.Time, Options.Create(_options)), context);
    }

    private (Team Team, ApplicationUser Owner, Note Note) Seed()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        return (team, owner, note);
    }

    private static MemoryStream Content(int length = 10) => new(new byte[length]);

    [Fact]
    public async Task UploadAsync_ValidFile_StoresBlobAndMetadata()
    {
        var (team, owner, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.UploadAsync(team.Id, note.Id, "..\\dir/report.txt", "Text/Plain; charset=utf-8", 10, Content(), default);

        Assert.Equal(("report.txt", "text/plain", 10L, owner.Id), (dto.FileName, dto.ContentType, dto.SizeBytes, dto.UploadedBy));
        await _blobs.Received(1).SaveAsync(Arg.Is<string>(k => k == Attachment.BuildStorageKey(team.Id, note.Id, dto.Id)), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await using var verify = _db.NewContext();
        Assert.Equal(dto.Id, (await verify.Attachments.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task UploadAsync_EmptyFile_ThrowsValidationError(long length)
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", length, Content(0), default));
    }

    [Fact]
    public async Task UploadAsync_TooLarge_ThrowsValidationErrorWithoutStoring()
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 2048, Content(), default));
        await _blobs.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Fact]
    public async Task UploadAsync_DisallowedContentType_ThrowsValidationError()
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.UploadAsync(team.Id, note.Id, "a.exe", "application/x-msdownload", 10, Content(), default));
    }

    [Fact]
    public async Task UploadAsync_ViewerWithoutEditAccess_IsForbidden()
    {
        var (team, _, note) = Seed();
        var viewer = _db.AddUser();
        _db.AddMember(team.Id, viewer.Id, TeamRole.Viewer);
        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), default));
    }

    [Fact]
    public async Task UploadAsync_DatabaseSaveFails_DeletesTheStoredBlob()
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        using var cts = new CancellationTokenSource();
        // Cancel right after the blob is stored so the database save that follows fails.
        _blobs.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), cts.Token));

        await _blobs.Received(1).DeleteAsync(Arg.Any<string>(), CancellationToken.None);
    }

    [Fact]
    public async Task ListAsync_ReturnsAttachmentsOfTheNote()
    {
        var (team, owner, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        await service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), default);
        var other = _db.AddNote(team.Id, owner.Id, "other");

        Assert.Single(await service.ListAsync(team.Id, note.Id, default));
        Assert.Empty(await service.ListAsync(team.Id, other.Id, default));
    }

    [Fact]
    public async Task DownloadAsync_ReturnsStreamAndMetadata()
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        var uploaded = await service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), default);
        var stream = new MemoryStream([1, 2, 3]);
        _blobs.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(stream);

        var download = await service.DownloadAsync(team.Id, uploaded.Id, default);

        Assert.Same(stream, download.Content);
        Assert.Equal(("a.txt", "text/plain"), (download.FileName, download.ContentType));
    }

    [Fact]
    public async Task DownloadAsync_MissingBlob_ThrowsNotFound()
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        var uploaded = await service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), default);
        _blobs.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Stream?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => service.DownloadAsync(team.Id, uploaded.Id, default));
    }

    [Fact]
    public async Task DownloadAsync_FromAnotherTeam_ThrowsNotFound()
    {
        var (team, owner, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        var uploaded = await service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), default);
        var otherTeam = _db.AddTeam(owner.Id, "Other");

        await Assert.ThrowsAsync<NotFoundException>(() => service.DownloadAsync(otherTeam.Id, uploaded.Id, default));
    }

    [Fact]
    public async Task DownloadAsync_UnknownAttachment_ThrowsNotFound()
    {
        var (team, _, _) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.DownloadAsync(team.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task DeleteAsync_RemovesRowAndBlob()
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        var uploaded = await service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), default);

        await service.DeleteAsync(team.Id, uploaded.Id, default);

        await using var verify = _db.NewContext();
        Assert.Empty(await verify.Attachments.ToListAsync());
        await _blobs.Received(1).DeleteAsync(Attachment.BuildStorageKey(team.Id, note.Id, uploaded.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_ViewerWithoutEditAccess_IsForbidden()
    {
        var (team, _, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        var uploaded = await service.UploadAsync(team.Id, note.Id, "a.txt", "text/plain", 10, Content(), default);
        var viewer = _db.AddUser();
        _db.AddMember(team.Id, viewer.Id, TeamRole.Viewer);
        _db.CurrentUser.UserId = viewer.Id;
        var (viewerService, viewerContext) = Create();
        await using var _viewer = viewerContext;

        await Assert.ThrowsAsync<ForbiddenException>(() => viewerService.DeleteAsync(team.Id, uploaded.Id, default));
    }

    [Theory]
    [InlineData(null, "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData("Image/PNG", "image/png")]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    public void NormalizeContentType_StripsParametersAndLowercases(string? input, string expected) =>
        Assert.Equal(expected, AttachmentService.NormalizeContentType(input));

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("..\\..\\secret.txt", "secret.txt")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("na\"me\r\n.txt", "name.txt")]
    [InlineData("", "attachment")]
    [InlineData(null, "attachment")]
    [InlineData("   ", "attachment")]
    public void SanitizeFileName_KeepsOnlyASafeLeafName(string? input, string expected) =>
        Assert.Equal(expected, AttachmentService.SanitizeFileName(input));

    [Fact]
    public void SanitizeFileName_VeryLongName_IsTruncated() =>
        Assert.Equal(255, AttachmentService.SanitizeFileName(new string('a', 400)).Length);
}

public class AttachmentsControllerTests
{
    private readonly IAttachmentService _attachments = Substitute.For<IAttachmentService>();
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _noteId = Guid.NewGuid();

    private AttachmentsController Create() =>
        new(_attachments) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    [Fact]
    public async Task List_ReturnsAttachments()
    {
        IReadOnlyList<AttachmentDto> list = [];
        _attachments.ListAsync(_teamId, _noteId, Arg.Any<CancellationToken>()).Returns(list);

        var result = await Create().List(_teamId, _noteId, default);

        Assert.Equal(list, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Upload_PassesFileDetailsToServiceAndReturnsCreated()
    {
        var dto = new AttachmentDto(Guid.NewGuid(), _noteId, "a.txt", "text/plain", 3, Guid.NewGuid(), DateTimeOffset.UtcNow);
        _attachments.UploadAsync(_teamId, _noteId, "a.txt", "text/plain", 3, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(dto);
        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "a.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" };

        var result = await Create().Upload(_teamId, _noteId, file, default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(AttachmentsController.Download), created.ActionName);
        Assert.Equal(dto, created.Value);
    }

    [Fact]
    public async Task Download_ReturnsFileResultWithNoSniffHeader()
    {
        var attachmentId = Guid.NewGuid();
        _attachments.DownloadAsync(_teamId, attachmentId, Arg.Any<CancellationToken>())
            .Returns(new AttachmentDownload(new MemoryStream([1]), "a.txt", "text/plain"));
        var controller = Create();

        var result = await controller.Download(_teamId, attachmentId, default);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal(("text/plain", "a.txt"), (file.ContentType, file.FileDownloadName));
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
    }

    [Fact]
    public async Task Delete_ReturnsNoContent()
    {
        var attachmentId = Guid.NewGuid();

        var result = await Create().Delete(_teamId, attachmentId, default);

        Assert.IsType<NoContentResult>(result);
        await _attachments.Received(1).DeleteAsync(_teamId, attachmentId, Arg.Any<CancellationToken>());
    }
}
