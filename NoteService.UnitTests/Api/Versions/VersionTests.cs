using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Notes;
using NoteService.API.Features.Versions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Versions;

public class VersionServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (VersionService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        var teamAccess = new TeamAccess(context, _db.CurrentUser);
        var noteAccess = new NoteAccessService(context, teamAccess, _db.CurrentUser);
        var history = new NoteHistory(context, _db.CurrentUser, _db.Time, Options.Create(new NotesOptions()));
        var notes = new NotesService(context, noteAccess, teamAccess, history, _db.CurrentUser, _db.Time);
        return (new VersionService(context, noteAccess, history, notes), context);
    }

    private (Team Team, ApplicationUser Owner, Note Note) SeedNoteWithVersions(int count = 3)
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        var note = _db.AddNote(team.Id, owner.Id, "Title v1", "content v1");
        using (var seed = _db.NewContext())
        {
            for (var i = 1; i <= count; i++)
            {
                var snapshot = NoteVersion.Snapshot(note, i, owner.Id, _db.Time.GetUtcNow().AddMinutes(i));
                snapshot.Title = $"Title v{i}";
                snapshot.ContentMarkdown = $"content v{i}";
                seed.NoteVersions.Add(snapshot);
            }

            seed.SaveChanges();
        }

        _db.CurrentUser.UserId = owner.Id;
        return (team, owner, note);
    }

    private Guid StampOf(Guid noteId)
    {
        using var context = _db.NewContext();
        return context.Notes.IgnoreQueryFilters().Single(n => n.Id == noteId).ConcurrencyStamp;
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var (service, context) = Create();
        await using var _ = context;

        var versions = await service.ListAsync(team.Id, note.Id, null, 25, default);

        Assert.Equal([3, 2, 1], versions.Select(v => v.VersionNo));
    }

    [Fact]
    public async Task ListAsync_BeforeAndLimit_PageBackwards()
    {
        var (team, _, note) = SeedNoteWithVersions(5);
        var (service, context) = Create();
        await using var _ = context;

        var page = await service.ListAsync(team.Id, note.Id, before: 4, limit: 2, default);

        Assert.Equal([3, 2], page.Select(v => v.VersionNo));
    }

    [Fact]
    public async Task ListAsync_LimitIsClamped()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var (service, context) = Create();
        await using var _ = context;

        Assert.Single(await service.ListAsync(team.Id, note.Id, null, 0, default));
    }

    [Fact]
    public async Task ListAsync_NonMember_ThrowsNotFound()
    {
        var (team, _, note) = SeedNoteWithVersions();
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.ListAsync(team.Id, note.Id, null, 25, default));
    }

    [Fact]
    public async Task GetAsync_ReturnsVersionContent()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var (service, context) = Create();
        await using var _ = context;

        var version = await service.GetAsync(team.Id, note.Id, 2, default);

        Assert.Equal(("Title v2", "content v2"), (version.Title, version.Content));
    }

    [Fact]
    public async Task GetAsync_UnknownVersion_ThrowsNotFound()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(team.Id, note.Id, 99, default));
    }

    [Fact]
    public async Task RestoreAsync_CopiesOldVersionOntoNoteAndRecordsNewVersion()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var (service, context) = Create();
        await using var _ = context;

        var restored = await service.RestoreAsync(team.Id, note.Id, 1, StampOf(note.Id), default);

        Assert.Equal(("Title v1", "content v1"), (restored.Title, restored.Content));
        await using var verify = _db.NewContext();
        var versions = await verify.NoteVersions.OrderBy(v => v.VersionNo).ToListAsync();
        Assert.Equal(4, versions.Count);
        Assert.Equal("Title v1", versions[^1].Title);
    }

    [Fact]
    public async Task RestoreAsync_StaleETag_ThrowsPreconditionFailed()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<PreconditionFailedException>(() => service.RestoreAsync(team.Id, note.Id, 1, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task RestoreAsync_UnknownVersion_ThrowsNotFound()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RestoreAsync(team.Id, note.Id, 99, StampOf(note.Id), default));
    }

    [Fact]
    public async Task RestoreAsync_Viewer_IsForbidden()
    {
        var (team, _, note) = SeedNoteWithVersions();
        var viewer = _db.AddUser();
        _db.AddMember(team.Id, viewer.Id, TeamRole.Viewer);
        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.RestoreAsync(team.Id, note.Id, 1, StampOf(note.Id), default));
    }
}

public class VersionsControllerTests
{
    private readonly IVersionService _versions = Substitute.For<IVersionService>();
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _noteId = Guid.NewGuid();

    [Fact]
    public async Task List_PassesPagingParameters()
    {
        IReadOnlyList<NoteVersionSummaryDto> list = [];
        _versions.ListAsync(_teamId, _noteId, 5, 10, Arg.Any<CancellationToken>()).Returns(list);

        var result = await new VersionsController(_versions).List(_teamId, _noteId, 5, 10, default);

        Assert.Equal(list, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Get_ReturnsVersion()
    {
        var dto = new NoteVersionDto(2, "T", "C", Guid.NewGuid(), DateTimeOffset.UtcNow);
        _versions.GetAsync(_teamId, _noteId, 2, Arg.Any<CancellationToken>()).Returns(dto);

        var result = await new VersionsController(_versions).Get(_teamId, _noteId, 2, default);

        Assert.Equal(dto, result.Value);
    }

    [Fact]
    public async Task Restore_UsesIfMatchAndSetsETag()
    {
        var stamp = Guid.NewGuid();
        var note = new NoteDto(_noteId, _teamId, "T", "C", null, false, [], Guid.Empty, Guid.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, NoteAccess.Edit, "\"new\"");
        _versions.RestoreAsync(_teamId, _noteId, 2, stamp, Arg.Any<CancellationToken>()).Returns(note);
        var controller = new VersionsController(_versions) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers.IfMatch = ETagHelper.Format(stamp);

        var result = await controller.Restore(_teamId, _noteId, 2, default);

        Assert.Equal(note, result.Value);
        Assert.Equal("\"new\"", controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Restore_WithoutIfMatch_ThrowsPreconditionRequired()
    {
        var controller = new VersionsController(_versions) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        await Assert.ThrowsAsync<PreconditionRequiredException>(() => controller.Restore(_teamId, _noteId, 1, default));
    }
}
