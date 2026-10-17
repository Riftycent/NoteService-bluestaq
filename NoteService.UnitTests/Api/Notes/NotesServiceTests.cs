using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Notes;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Notes;

public class NotesServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NotesOptions _options = new() { VersionIntervalMinutes = 5 };

    public void Dispose() => _db.Dispose();

    private (NotesService Service, NoteDbContext Context) CreateService()
    {
        var context = _db.NewContext();
        var teamAccess = new TeamAccess(context, _db.CurrentUser);
        var service = new NotesService(
            context,
            new NoteAccessService(context, teamAccess, _db.CurrentUser),
            teamAccess,
            new NoteHistory(context, _db.CurrentUser, _db.Time, Options.Create(_options)),
            _db.CurrentUser,
            _db.Time);
        return (service, context);
    }

    private (Team Team, ApplicationUser Owner) SeedTeam()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        return (team, owner);
    }

    private ApplicationUser AddMember(Team team, TeamRole role)
    {
        var user = _db.AddUser();
        _db.AddMember(team.Id, user.Id, role);
        return user;
    }

    private Guid CurrentStamp(Guid noteId)
    {
        using var context = _db.NewContext();
        return context.Notes.IgnoreQueryFilters().Single(n => n.Id == noteId).ConcurrencyStamp;
    }

    // ---- Create

    [Fact]
    public async Task CreateAsync_EditorCreatesNote_PersistsNoteAndFirstVersion()
    {
        var (team, owner) = SeedTeam();
        var (service, context) = CreateService();
        await using var _ = context;

        var dto = await service.CreateAsync(team.Id, new CreateNoteRequest { Title = "Plan", Content = "# Goals\nShip it", IsPinned = true }, default);

        Assert.Equal("Plan", dto.Title);
        Assert.Equal("# Goals\nShip it", dto.Content);
        Assert.True(dto.IsPinned);
        Assert.Equal(NoteAccess.Edit, dto.Access);
        Assert.Equal(owner.Id, dto.CreatedBy);
        Assert.Equal(ETagHelper.Format(CurrentStamp(dto.Id)), dto.ETag);
        await using var verify = _db.NewContext();
        var version = await verify.NoteVersions.SingleAsync();
        Assert.Equal((1, "Plan", owner.Id), (version.VersionNo, version.Title, version.EditedBy));
    }

    [Fact]
    public async Task CreateAsync_Viewer_IsForbidden()
    {
        var (team, _) = SeedTeam();
        var viewer = AddMember(team, TeamRole.Viewer);
        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(team.Id, new CreateNoteRequest { Title = "x" }, default));
    }

    [Fact]
    public async Task CreateAsync_FolderFromAnotherTeam_ThrowsValidationError()
    {
        var (team, owner) = SeedTeam();
        var foreign = _db.AddFolder(_db.AddTeam(owner.Id, "Other").Id);
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.CreateAsync(team.Id, new CreateNoteRequest { Title = "x", FolderId = foreign.Id }, default));
    }

    [Fact]
    public async Task CreateAsync_Anonymous_ThrowsUnauthorized()
    {
        var (team, _) = SeedTeam();
        _db.CurrentUser.UserId = null;
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.CreateAsync(team.Id, new CreateNoteRequest { Title = "x" }, default));
    }

    [Fact]
    public async Task CreateAsync_NonMember_ThrowsNotFound()
    {
        var (team, _) = SeedTeam();
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync(team.Id, new CreateNoteRequest { Title = "x" }, default));
    }

    // ---- Get

    [Fact]
    public async Task GetAsync_ReturnsNoteWithTagsAndAccessLevel()
    {
        var (team, owner) = SeedTeam();
        var viewer = AddMember(team, TeamRole.Viewer);
        var note = _db.AddNote(team.Id, owner.Id, "Title", "Body");
        var tagB = _db.AddTag(team.Id, "b");
        var tagA = _db.AddTag(team.Id, "a");
        await using (var seed = _db.NewContext())
        {
            seed.NoteTags.AddRange(new NoteTag { NoteId = note.Id, TagId = tagB.Id }, new NoteTag { NoteId = note.Id, TagId = tagA.Id });
            await seed.SaveChangesAsync();
        }

        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = CreateService();
        await using var _ = context;

        var dto = await service.GetAsync(team.Id, note.Id, default);

        Assert.Equal(["a", "b"], dto.Tags.Select(t => t.Name));
        Assert.Equal(NoteAccess.Read, dto.Access);
        Assert.Equal("Body", dto.Content);
    }

    [Fact]
    public async Task GetAsync_TrashedNote_ThrowsNotFound()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id, deleted: true);
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(team.Id, note.Id, default));
    }

    // ---- List

    [Fact]
    public async Task ListAsync_DefaultsToNewestFirstAndExcludesTrash()
    {
        var (team, owner) = SeedTeam();
        _db.AddNote(team.Id, owner.Id, "oldest");
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        _db.AddNote(team.Id, owner.Id, "newest");
        _db.AddNote(team.Id, owner.Id, "trashed", deleted: true);
        var (service, context) = CreateService();
        await using var _ = context;

        var page = await service.ListAsync(team.Id, new NoteListQuery(), default);

        Assert.Equal(["newest", "oldest"], page.Items.Select(n => n.Title));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListAsync_ProjectsExcerptAndTags()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id, "n", new string('x', 500));
        var tag = _db.AddTag(team.Id, "urgent");
        await using (var seed = _db.NewContext())
        {
            seed.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = tag.Id });
            await seed.SaveChangesAsync();
        }

        var (service, context) = CreateService();
        await using var _ = context;

        var item = (await service.ListAsync(team.Id, new NoteListQuery(), default)).Items.Single();

        Assert.Equal(200, item.Excerpt.Length);
        Assert.Equal(["urgent"], item.Tags);
    }

    [Fact]
    public async Task ListAsync_FiltersByFolderTagPinnedAndUpdatedSince()
    {
        var (team, owner) = SeedTeam();
        var folder = _db.AddFolder(team.Id);
        var tag = _db.AddTag(team.Id, "bug");
        var inFolder = _db.AddNote(team.Id, owner.Id, "in folder", folderId: folder.Id);
        var tagged = _db.AddNote(team.Id, owner.Id, "tagged");
        await using (var seed = _db.NewContext())
        {
            seed.NoteTags.Add(new NoteTag { NoteId = tagged.Id, TagId = tag.Id });
            var pinned = await seed.Notes.IgnoreQueryFilters().SingleAsync(n => n.Id == inFolder.Id);
            pinned.SetPinned(true);
            await seed.SaveChangesAsync();
        }

        _db.Time.Advance(TimeSpan.FromHours(1));
        var recent = _db.AddNote(team.Id, owner.Id, "recent");
        var (service, context) = CreateService();
        await using var _ = context;

        Assert.Equal(["in folder"], (await service.ListAsync(team.Id, new NoteListQuery { FolderId = folder.Id }, default)).Items.Select(n => n.Title));
        Assert.Equal(["tagged"], (await service.ListAsync(team.Id, new NoteListQuery { Tag = " BUG " }, default)).Items.Select(n => n.Title));
        Assert.Equal(["in folder"], (await service.ListAsync(team.Id, new NoteListQuery { Pinned = true }, default)).Items.Select(n => n.Title));
        Assert.Equal([recent.Title], (await service.ListAsync(team.Id, new NoteListQuery { UpdatedSince = _db.Time.GetUtcNow().AddMinutes(-1) }, default)).Items.Select(n => n.Title));
    }

    [Fact]
    public async Task ListAsync_KeysetPagination_WalksAllNotesWithoutDuplicatesOrGaps()
    {
        var (team, owner) = SeedTeam();
        for (var i = 0; i < 7; i++)
        {
            // Half the notes share a timestamp so the id tie-breaker is exercised.
            if (i % 2 == 0)
            {
                _db.Time.Advance(TimeSpan.FromSeconds(1));
            }

            _db.AddNote(team.Id, owner.Id, $"note {i}");
        }

        var (service, context) = CreateService();
        await using var _ = context;

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await service.ListAsync(team.Id, new NoteListQuery { Limit = 3, Cursor = cursor }, default);
            seen.AddRange(page.Items.Select(n => n.Title));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task ListAsync_SortByTitleAscending_PagesInAlphabeticalOrder()
    {
        var (team, owner) = SeedTeam();
        foreach (var title in new[] { "delta", "alpha", "charlie", "bravo", "echo" })
        {
            _db.AddNote(team.Id, owner.Id, title);
        }

        var (service, context) = CreateService();
        await using var _ = context;

        var first = await service.ListAsync(team.Id, new NoteListQuery { Sort = NoteSort.Title, Limit = 3 }, default);
        var second = await service.ListAsync(team.Id, new NoteListQuery { Sort = NoteSort.Title, Limit = 3, Cursor = first.NextCursor }, default);

        Assert.Equal(["alpha", "bravo", "charlie"], first.Items.Select(n => n.Title));
        Assert.Equal(["delta", "echo"], second.Items.Select(n => n.Title));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task ListAsync_TitleDescendingAndUpdatedAscending_Work()
    {
        var (team, owner) = SeedTeam();
        _db.AddNote(team.Id, owner.Id, "a");
        _db.Time.Advance(TimeSpan.FromSeconds(1));
        _db.AddNote(team.Id, owner.Id, "b");
        var (service, context) = CreateService();
        await using var _ = context;

        var byTitleDesc = await service.ListAsync(team.Id, new NoteListQuery { Sort = NoteSort.Title, Order = SortOrder.Desc, Limit = 1 }, default);
        var nextTitle = await service.ListAsync(team.Id, new NoteListQuery { Sort = NoteSort.Title, Order = SortOrder.Desc, Limit = 1, Cursor = byTitleDesc.NextCursor }, default);
        var byUpdatedAsc = await service.ListAsync(team.Id, new NoteListQuery { Order = SortOrder.Asc, Limit = 1 }, default);
        var nextUpdated = await service.ListAsync(team.Id, new NoteListQuery { Order = SortOrder.Asc, Limit = 1, Cursor = byUpdatedAsc.NextCursor }, default);

        Assert.Equal(("b", "a"), (byTitleDesc.Items[0].Title, nextTitle.Items[0].Title));
        Assert.Equal(("a", "b"), (byUpdatedAsc.Items[0].Title, nextUpdated.Items[0].Title));
    }

    [Fact]
    public async Task ListAsync_InvalidCursor_ThrowsValidationError()
    {
        var (team, _) = SeedTeam();
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.ListAsync(team.Id, new NoteListQuery { Cursor = "garbage!!" }, default));
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.ListAsync(team.Id, new NoteListQuery { Cursor = Cursor.Encode("not-ticks", Guid.NewGuid()) }, default));
    }

    [Fact]
    public async Task ListAsync_OtherTeamsNotesAreNeverReturned()
    {
        var (team, owner) = SeedTeam();
        var other = _db.AddTeam(_db.AddUser().Id, "Other");
        _db.AddNote(other.Id, owner.Id, "foreign");
        var (service, context) = CreateService();
        await using var _ = context;

        Assert.Empty((await service.ListAsync(team.Id, new NoteListQuery(), default)).Items);
    }

    [Fact]
    public async Task ListAsync_NonMember_ThrowsNotFound()
    {
        var (team, _) = SeedTeam();
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.ListAsync(team.Id, new NoteListQuery(), default));
    }

    [Fact]
    public async Task ListTrashAsync_ReturnsOnlyDeletedNotesNewestDeletionFirst()
    {
        var (team, owner) = SeedTeam();
        _db.AddNote(team.Id, owner.Id, "live");
        _db.AddNote(team.Id, owner.Id, "first trashed", deleted: true);
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        _db.AddNote(team.Id, owner.Id, "second trashed", deleted: true);
        var (service, context) = CreateService();
        await using var _ = context;

        var trash = await service.ListTrashAsync(team.Id, default);

        Assert.Equal(["second trashed", "first trashed"], trash.Select(n => n.Title));
        Assert.All(trash, n => Assert.NotNull(n.DeletedAt));
    }

    // ---- Update

    [Fact]
    public async Task UpdateAsync_ChangeTitleAndContent_UpdatesNoteAndBumpsETag()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id, "Old", "old body");
        var stamp = CurrentStamp(note.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        var dto = await service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { Title = "New", Content = "new body" }, stamp, default);

        Assert.Equal(("New", "new body"), (dto.Title, dto.Content));
        Assert.NotEqual(ETagHelper.Format(stamp), dto.ETag);
    }

    [Fact]
    public async Task UpdateAsync_StaleETag_ThrowsPreconditionFailedWithCurrentETag()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        var ex = await Assert.ThrowsAsync<PreconditionFailedException>(() =>
            service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { Title = "x" }, Guid.NewGuid(), default));

        Assert.Equal(ETagHelper.Format(CurrentStamp(note.Id)), ex.CurrentETag);
    }

    [Fact]
    public async Task UpdateAsync_NothingChanged_KeepsETag()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id, "Same", "same");
        var stamp = CurrentStamp(note.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        var dto = await service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { Title = "Same", Content = "same" }, stamp, default);

        Assert.Equal(ETagHelper.Format(stamp), dto.ETag);
    }

    [Fact]
    public async Task UpdateAsync_BlankTitle_ThrowsValidationError()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { Title = "  " }, CurrentStamp(note.Id), default));
    }

    [Fact]
    public async Task UpdateAsync_MoveToFolderAndBackToRoot()
    {
        var (team, owner) = SeedTeam();
        var folder = _db.AddFolder(team.Id);
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        var moved = await service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { FolderId = folder.Id }, CurrentStamp(note.Id), default);
        var back = await service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { MoveToRoot = true }, CurrentStamp(note.Id), default);

        Assert.Equal(folder.Id, moved.FolderId);
        Assert.Null(back.FolderId);
    }

    [Fact]
    public async Task UpdateAsync_UnknownFolder_ThrowsValidationError()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { FolderId = Guid.NewGuid() }, CurrentStamp(note.Id), default));
    }

    [Fact]
    public async Task UpdateAsync_PinFlag_IsApplied()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        var dto = await service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { IsPinned = true }, CurrentStamp(note.Id), default);

        Assert.True(dto.IsPinned);
    }

    [Fact]
    public async Task UpdateAsync_Viewer_IsForbidden()
    {
        var (team, owner) = SeedTeam();
        var viewer = AddMember(team, TeamRole.Viewer);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { Title = "x" }, CurrentStamp(note.Id), default));
    }

    [Fact]
    public async Task UpdateAsync_ViewerWithEditShare_CanUpdate()
    {
        var (team, owner) = SeedTeam();
        var viewer = AddMember(team, TeamRole.Viewer);
        var note = _db.AddNote(team.Id, owner.Id);
        await using (var seed = _db.NewContext())
        {
            seed.NoteShares.Add(new NoteShare { NoteId = note.Id, UserId = viewer.Id, Permission = SharePermission.Edit });
            await seed.SaveChangesAsync();
        }

        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = CreateService();
        await using var _ = context;

        var dto = await service.UpdateAsync(team.Id, note.Id, new UpdateNoteRequest { Title = "Edited" }, CurrentStamp(note.Id), default);

        Assert.Equal("Edited", dto.Title);
    }

    [Fact]
    public async Task UpdateAsync_RapidEditsBySameUser_AreThrottledToOneSnapshot()
    {
        var (team, _) = SeedTeam();
        var created = await CreateViaServiceAsync(team.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        var second = await service.UpdateAsync(team.Id, created.Id, new UpdateNoteRequest { Content = "two" }, ETagHelper.Parse(created.ETag)!.Value, default);
        _db.Time.Advance(TimeSpan.FromSeconds(30));
        await service.UpdateAsync(team.Id, created.Id, new UpdateNoteRequest { Content = "three" }, ETagHelper.Parse(second.ETag)!.Value, default);

        await using var verify = _db.NewContext();
        Assert.Equal(1, await verify.NoteVersions.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_EditAfterInterval_AddsAnotherSnapshot()
    {
        var (team, _) = SeedTeam();
        var created = await CreateViaServiceAsync(team.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        _db.Time.Advance(TimeSpan.FromMinutes(6));
        await service.UpdateAsync(team.Id, created.Id, new UpdateNoteRequest { Content = "later" }, ETagHelper.Parse(created.ETag)!.Value, default);

        await using var verify = _db.NewContext();
        var versions = await verify.NoteVersions.OrderBy(v => v.VersionNo).ToListAsync();
        Assert.Equal([1, 2], versions.Select(v => v.VersionNo));
        Assert.Equal("later", versions[1].ContentMarkdown);
    }

    [Fact]
    public async Task UpdateAsync_PinOnly_DoesNotSnapshot()
    {
        var (team, _) = SeedTeam();
        var created = await CreateViaServiceAsync(team.Id);
        var (service, context) = CreateService();
        await using var _ = context;
        _db.Time.Advance(TimeSpan.FromHours(1));

        await service.UpdateAsync(team.Id, created.Id, new UpdateNoteRequest { IsPinned = true }, ETagHelper.Parse(created.ETag)!.Value, default);

        await using var verify = _db.NewContext();
        Assert.Equal(1, await verify.NoteVersions.CountAsync());
    }

    private async Task<NoteDto> CreateViaServiceAsync(Guid teamId)
    {
        var (service, context) = CreateService();
        await using var _ = context;
        return await service.CreateAsync(teamId, new CreateNoteRequest { Title = "T", Content = "one" }, default);
    }

    // ---- Delete / restore

    [Fact]
    public async Task DeleteAsync_MovesNoteToTrash()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        await service.DeleteAsync(team.Id, note.Id, CurrentStamp(note.Id), default);

        await using var verify = _db.NewContext();
        var stored = await verify.Notes.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_db.Time.GetUtcNow(), stored.DeletedAt);
    }

    [Fact]
    public async Task DeleteAsync_StaleETag_ThrowsPreconditionFailed()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<PreconditionFailedException>(() => service.DeleteAsync(team.Id, note.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task DeleteAsync_Viewer_IsForbidden()
    {
        var (team, owner) = SeedTeam();
        var viewer = AddMember(team, TeamRole.Viewer);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteAsync(team.Id, note.Id, CurrentStamp(note.Id), default));
    }

    [Fact]
    public async Task RestoreAsync_TrashedNote_ReturnsToTheLiveList()
    {
        var (team, owner) = SeedTeam();
        var note = _db.AddNote(team.Id, owner.Id, deleted: true);
        var (service, context) = CreateService();
        await using var _ = context;

        var dto = await service.RestoreAsync(team.Id, note.Id, default);

        Assert.Null(dto.DeletedAt);
        await using var verify = _db.NewContext();
        Assert.Single(await verify.Notes.ToListAsync());
    }

    [Fact]
    public async Task RestoreAsync_UnknownNote_ThrowsNotFound()
    {
        var (team, _) = SeedTeam();
        var (service, context) = CreateService();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RestoreAsync(team.Id, Guid.NewGuid(), default));
    }
}
