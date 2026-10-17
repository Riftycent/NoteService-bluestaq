using Microsoft.EntityFrameworkCore;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Infrastructure;

public class NoteDbContextTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SaveChanges_AddedAuditableEntity_SetsCreatedAndUpdatedFields()
    {
        var user = _db.AddUser();
        _db.CurrentUser.UserId = user.Id;
        await using var context = _db.NewContext();

        var team = Team.Create("Core");
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        Assert.Equal(_db.Time.GetUtcNow(), team.CreatedAt);
        Assert.Equal(_db.Time.GetUtcNow(), team.UpdatedAt);
        Assert.Equal(user.Id, team.CreatedBy);
        Assert.Equal(user.Id, team.UpdatedBy);
    }

    [Fact]
    public async Task SaveChanges_ModifiedEntity_UpdatesOnlyUpdatedFields()
    {
        var author = _db.AddUser();
        var editor = _db.AddUser();
        var team = _db.AddTeam(author.Id);
        var note = _db.AddNote(team.Id, author.Id);
        var createdAt = _db.Time.GetUtcNow();

        _db.Time.Advance(TimeSpan.FromHours(2));
        _db.CurrentUser.UserId = editor.Id;
        await using var context = _db.NewContext();
        _db.AddMember(team.Id, editor.Id, TeamRole.Editor);
        var loaded = await context.Notes.SingleAsync(n => n.Id == note.Id);
        loaded.SetPinned(true);
        await context.SaveChangesAsync();

        Assert.Equal(createdAt, loaded.CreatedAt);
        Assert.Equal(author.Id, loaded.CreatedBy);
        Assert.Equal(createdAt.AddHours(2), loaded.UpdatedAt);
        Assert.Equal(editor.Id, loaded.UpdatedBy);
    }

    [Fact]
    public async Task SaveChanges_NoSignedInUser_UsesEmptyGuid()
    {
        await using var context = _db.NewContext();
        var team = Team.Create("Core");
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        Assert.Equal(Guid.Empty, team.CreatedBy);
    }

    [Fact]
    public void SaveChanges_Sync_AppliesAuditFields()
    {
        var user = _db.AddUser();
        _db.CurrentUser.UserId = user.Id;
        using var context = _db.NewContext();
        var team = Team.Create("Core");
        context.Teams.Add(team);

        context.SaveChanges();

        Assert.Equal(user.Id, team.CreatedBy);
    }

    [Fact]
    public async Task SaveChanges_NoteWrite_ChangesConcurrencyStampEachTime()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var note = _db.AddNote(team.Id, user.Id);
        _db.CurrentUser.UserId = user.Id;

        await using var context = _db.NewContext();
        var loaded = await context.Notes.SingleAsync(n => n.Id == note.Id);
        var before = loaded.ConcurrencyStamp;
        Assert.NotEqual(Guid.Empty, before);

        loaded.SetPinned(true);
        await context.SaveChangesAsync();

        Assert.NotEqual(before, loaded.ConcurrencyStamp);
    }

    [Fact]
    public async Task SaveChanges_StaleConcurrencyStamp_ThrowsConcurrencyException()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var note = _db.AddNote(team.Id, user.Id);
        _db.CurrentUser.UserId = user.Id;

        await using var first = _db.NewContext();
        await using var second = _db.NewContext();
        var a = await first.Notes.SingleAsync(n => n.Id == note.Id);
        var b = await second.Notes.SingleAsync(n => n.Id == note.Id);

        a.SetContent("from A", "x");
        await first.SaveChangesAsync();
        b.SetContent("from B", "y");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TenantFilter_ReturnsOnlyNotesOfTeamsTheUserBelongsTo()
    {
        var me = _db.AddUser();
        var other = _db.AddUser();
        var myTeam = _db.AddTeam(me.Id);
        var otherTeam = _db.AddTeam(other.Id);
        _db.AddNote(myTeam.Id, me.Id, "mine");
        _db.AddNote(otherTeam.Id, other.Id, "theirs");
        _db.CurrentUser.UserId = me.Id;

        await using var context = _db.NewContext();
        var titles = await context.Notes.Select(n => n.Title).ToListAsync();

        Assert.Equal(["mine"], titles);
    }

    [Fact]
    public async Task TenantFilter_AnonymousUser_SeesNothing()
    {
        var me = _db.AddUser();
        var team = _db.AddTeam(me.Id);
        _db.AddNote(team.Id, me.Id);
        _db.AddFolder(team.Id);
        _db.AddTag(team.Id);
        _db.CurrentUser.UserId = null;

        await using var context = _db.NewContext();

        Assert.Empty(await context.Notes.ToListAsync());
        Assert.Empty(await context.Teams.ToListAsync());
        Assert.Empty(await context.Folders.ToListAsync());
        Assert.Empty(await context.Tags.ToListAsync());
    }

    [Fact]
    public async Task TenantFilter_AppliesToTeamsFoldersAndTags()
    {
        var me = _db.AddUser();
        var other = _db.AddUser();
        var myTeam = _db.AddTeam(me.Id, "mine");
        var otherTeam = _db.AddTeam(other.Id, "theirs");
        _db.AddFolder(myTeam.Id, "f-mine");
        _db.AddFolder(otherTeam.Id, "f-theirs");
        _db.AddTag(myTeam.Id, "t-mine");
        _db.AddTag(otherTeam.Id, "t-theirs");
        _db.CurrentUser.UserId = me.Id;

        await using var context = _db.NewContext();

        Assert.Equal(["mine"], await context.Teams.Select(t => t.Name).ToListAsync());
        Assert.Equal(["f-mine"], await context.Folders.Select(f => f.Name).ToListAsync());
        Assert.Equal(["t-mine"], await context.Tags.Select(t => t.Name).ToListAsync());
    }

    [Fact]
    public async Task SoftDeleteFilter_HidesDeletedNotesUnlessIgnored()
    {
        var me = _db.AddUser();
        var team = _db.AddTeam(me.Id);
        _db.AddNote(team.Id, me.Id, "live");
        _db.AddNote(team.Id, me.Id, "trashed", deleted: true);
        _db.CurrentUser.UserId = me.Id;

        await using var context = _db.NewContext();
        var visible = await context.Notes.Select(n => n.Title).ToListAsync();
        var withTrash = await context.Notes.IgnoreQueryFilters([NoteDbContext.SoftDeleteFilter]).Select(n => n.Title).OrderBy(t => t).ToListAsync();

        Assert.Equal(["live"], visible);
        Assert.Equal(["live", "trashed"], withTrash);
    }

    [Fact]
    public async Task IgnoringSoftDeleteFilter_StillEnforcesTenantFilter()
    {
        var me = _db.AddUser();
        var other = _db.AddUser();
        var myTeam = _db.AddTeam(me.Id);
        var otherTeam = _db.AddTeam(other.Id);
        _db.AddNote(myTeam.Id, me.Id, "mine-trashed", deleted: true);
        _db.AddNote(otherTeam.Id, other.Id, "theirs-trashed", deleted: true);
        _db.CurrentUser.UserId = me.Id;

        await using var context = _db.NewContext();
        var titles = await context.Notes.IgnoreQueryFilters([NoteDbContext.SoftDeleteFilter]).Select(n => n.Title).ToListAsync();

        Assert.Equal(["mine-trashed"], titles);
    }

    [Fact]
    public async Task DateTimeOffsetColumns_CanBeOrderedAndCompared()
    {
        var me = _db.AddUser();
        var team = _db.AddTeam(me.Id);
        _db.AddNote(team.Id, me.Id, "first");
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        _db.AddNote(team.Id, me.Id, "second");
        _db.CurrentUser.UserId = me.Id;
        var cutoff = _db.Time.GetUtcNow().AddSeconds(-30);

        await using var context = _db.NewContext();
        var recent = await context.Notes.Where(n => n.UpdatedAt > cutoff).Select(n => n.Title).ToListAsync();
        var ordered = await context.Notes.OrderByDescending(n => n.UpdatedAt).Select(n => n.Title).ToListAsync();

        Assert.Equal(["second"], recent);
        Assert.Equal(["second", "first"], ordered);
    }
}
