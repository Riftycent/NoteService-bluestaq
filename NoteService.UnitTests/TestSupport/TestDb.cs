using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Infrastructure.Persistence;

namespace NoteService.UnitTests.TestSupport;

public sealed class FakeCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
}

/// <summary>
/// An in-process SQLite database with the real EF model (query filters, concurrency tokens, audit fields).
/// It is not an external database, so tests using it stay fast and isolated.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public FakeCurrentUser CurrentUser { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

    public NoteDbContext NewContext() =>
        new(new DbContextOptionsBuilder<NoteDbContext>().UseSqlite(_connection).Options, CurrentUser, Time);

    public ApplicationUser AddUser(string? email = null, string displayName = "Test User")
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = displayName,
            CreatedAt = Time.GetUtcNow(),
            SecurityStamp = Guid.NewGuid().ToString()
        };

        using var context = NewContext();
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    /// <summary>Creates a team and makes <paramref name="ownerId"/> its owner.</summary>
    public Team AddTeam(Guid ownerId, string name = "Team")
    {
        var team = Team.Create(name);
        using var context = NewContext();
        context.Teams.Add(team);
        context.TeamMembers.Add(TeamMember.Create(team.Id, ownerId, TeamRole.Owner, Time.GetUtcNow()));
        context.SaveChanges();
        return team;
    }

    public void AddMember(Guid teamId, Guid userId, TeamRole role)
    {
        using var context = NewContext();
        context.TeamMembers.Add(TeamMember.Create(teamId, userId, role, Time.GetUtcNow()));
        context.SaveChanges();
    }

    public Note AddNote(Guid teamId, Guid author, string title = "Note", string content = "Body", Guid? folderId = null, bool deleted = false)
    {
        var previous = CurrentUser.UserId;
        CurrentUser.UserId = author;
        try
        {
            var note = Note.Create(teamId, folderId, title, content);
            if (deleted)
            {
                note.SoftDelete(Time.GetUtcNow());
            }

            using var context = NewContext();
            context.Notes.Add(note);
            context.SaveChanges();
            return note;
        }
        finally
        {
            CurrentUser.UserId = previous;
        }
    }

    public Folder AddFolder(Guid teamId, string name = "Folder", Guid? parentId = null)
    {
        var folder = Folder.Create(teamId, parentId, name);
        using var context = NewContext();
        context.Folders.Add(folder);
        context.SaveChanges();
        return folder;
    }

    public Tag AddTag(Guid teamId, string name = "tag")
    {
        var tag = Tag.Create(teamId, name);
        using var context = NewContext();
        context.Tags.Add(tag);
        context.SaveChanges();
        return tag;
    }

    public void Dispose() => _connection.Dispose();
}
