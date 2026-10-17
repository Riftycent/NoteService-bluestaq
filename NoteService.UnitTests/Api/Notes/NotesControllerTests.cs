using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using NoteService.API.Common;
using NoteService.API.Features.Notes;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Notes;

public class NotesControllerTests
{
    private readonly INotesService _notes = Substitute.For<INotesService>();
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _noteId = Guid.NewGuid();

    private NoteDto Dto(string etag = "\"abc\"") => new(
        _noteId, _teamId, "T", "C", null, false, [], Guid.Empty, Guid.Empty,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, NoteAccess.Edit, etag);

    private NotesController CreateController(Guid? ifMatch = null)
    {
        var controller = new NotesController(_notes) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        if (ifMatch is { } stamp)
        {
            controller.Request.Headers.IfMatch = ETagHelper.Format(stamp);
        }

        return controller;
    }

    [Fact]
    public async Task List_ReturnsServicePage()
    {
        var page = new Page<NoteSummaryDto>([], null);
        var query = new NoteListQuery();
        _notes.ListAsync(_teamId, query, Arg.Any<CancellationToken>()).Returns(page);

        var result = await CreateController().List(_teamId, query, default);

        Assert.Equal(page, result.Value);
    }

    [Fact]
    public async Task Create_ReturnsCreatedWithETagHeader()
    {
        _notes.CreateAsync(_teamId, Arg.Any<CreateNoteRequest>(), Arg.Any<CancellationToken>()).Returns(Dto("\"v1\""));
        var controller = CreateController();

        var result = await controller.Create(_teamId, new CreateNoteRequest { Title = "T" }, default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(NotesController.Get), created.ActionName);
        Assert.Equal(_noteId, created.RouteValues!["noteId"]);
        Assert.Equal("\"v1\"", controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Get_ReturnsNoteWithETagHeader()
    {
        _notes.GetAsync(_teamId, _noteId, Arg.Any<CancellationToken>()).Returns(Dto("\"v2\""));
        var controller = CreateController();

        var result = await controller.Get(_teamId, _noteId, default);

        Assert.Equal(_noteId, result.Value!.Id);
        Assert.Equal("\"v2\"", controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Update_PassesIfMatchStampToService()
    {
        var stamp = Guid.NewGuid();
        var request = new UpdateNoteRequest { Title = "New" };
        _notes.UpdateAsync(_teamId, _noteId, request, stamp, Arg.Any<CancellationToken>()).Returns(Dto("\"v3\""));
        var controller = CreateController(stamp);

        var result = await controller.Update(_teamId, _noteId, request, default);

        Assert.Equal("\"v3\"", result.Value!.ETag);
        Assert.Equal("\"v3\"", controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Update_WithoutIfMatch_ThrowsPreconditionRequired()
    {
        await Assert.ThrowsAsync<PreconditionRequiredException>(() =>
            CreateController().Update(_teamId, _noteId, new UpdateNoteRequest(), default));
        await _notes.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task Delete_PassesIfMatchStampAndReturnsNoContent()
    {
        var stamp = Guid.NewGuid();

        var result = await CreateController(stamp).Delete(_teamId, _noteId, default);

        Assert.IsType<NoContentResult>(result);
        await _notes.Received(1).DeleteAsync(_teamId, _noteId, stamp, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_WithoutIfMatch_ThrowsPreconditionRequired() =>
        await Assert.ThrowsAsync<PreconditionRequiredException>(() => CreateController().Delete(_teamId, _noteId, default));

    [Fact]
    public async Task Restore_ReturnsRestoredNote()
    {
        _notes.RestoreAsync(_teamId, _noteId, Arg.Any<CancellationToken>()).Returns(Dto("\"v4\""));
        var controller = CreateController();

        var result = await controller.Restore(_teamId, _noteId, default);

        Assert.Equal("\"v4\"", controller.Response.Headers.ETag.ToString());
        Assert.Equal(_noteId, result.Value!.Id);
    }

    [Fact]
    public async Task TrashController_List_ReturnsTrashedNotes()
    {
        IReadOnlyList<NoteSummaryDto> trash = [];
        _notes.ListTrashAsync(_teamId, Arg.Any<CancellationToken>()).Returns(trash);

        var result = await new TrashController(_notes).List(_teamId, default);

        Assert.Equal(trash, Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}

public class NoteHistoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private NoteHistory Create(NoteService.Infrastructure.Persistence.NoteDbContext context, int intervalMinutes = 5) =>
        new(context, _db.CurrentUser, _db.Time, Options.Create(new NotesOptions { VersionIntervalMinutes = intervalMinutes }));

    [Fact]
    public async Task SnapshotAsync_FirstSnapshot_IsVersionOne()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var note = _db.AddNote(team.Id, user.Id, "T", "body");
        _db.CurrentUser.UserId = user.Id;
        await using var context = _db.NewContext();

        var taken = await Create(context).SnapshotAsync(note, force: false, default);
        await context.SaveChangesAsync();

        Assert.True(taken);
        var version = await context.NoteVersions.SingleAsync();
        Assert.Equal((1, "T", "body", user.Id), (version.VersionNo, version.Title, version.ContentMarkdown, version.EditedBy));
    }

    [Fact]
    public async Task SnapshotAsync_WithinIntervalBySameEditor_IsSkippedUnlessForced()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var note = _db.AddNote(team.Id, user.Id);
        _db.CurrentUser.UserId = user.Id;
        await using (var seed = _db.NewContext())
        {
            seed.NoteVersions.Add(NoteService.Domain.Entities.NoteVersion.Snapshot(note, 1, user.Id, _db.Time.GetUtcNow()));
            await seed.SaveChangesAsync();
        }

        _db.Time.Advance(TimeSpan.FromMinutes(1));
        await using var context = _db.NewContext();
        var history = Create(context);

        Assert.False(await history.SnapshotAsync(note, force: false, default));
        Assert.True(await history.SnapshotAsync(note, force: true, default));
        await context.SaveChangesAsync();
        Assert.Equal([1, 2], await context.NoteVersions.OrderBy(v => v.VersionNo).Select(v => v.VersionNo).ToListAsync());
    }

    [Fact]
    public async Task SnapshotAsync_DifferentEditor_AlwaysSnapshots()
    {
        var author = _db.AddUser();
        var editor = _db.AddUser();
        var team = _db.AddTeam(author.Id);
        var note = _db.AddNote(team.Id, author.Id);
        await using (var seed = _db.NewContext())
        {
            seed.NoteVersions.Add(NoteService.Domain.Entities.NoteVersion.Snapshot(note, 1, author.Id, _db.Time.GetUtcNow()));
            await seed.SaveChangesAsync();
        }

        _db.CurrentUser.UserId = editor.Id;
        await using var context = _db.NewContext();

        Assert.True(await Create(context).SnapshotAsync(note, force: false, default));
    }

    [Fact]
    public async Task SnapshotAsync_Anonymous_ThrowsUnauthorized()
    {
        var user = _db.AddUser();
        var note = _db.AddNote(_db.AddTeam(user.Id).Id, user.Id);
        _db.CurrentUser.UserId = null;
        await using var context = _db.NewContext();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => Create(context).SnapshotAsync(note, force: true, default));
    }
}
