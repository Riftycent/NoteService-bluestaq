using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;

namespace NoteService.UnitTests.Domain;

public class TeamTests
{
    [Fact]
    public void Create_TrimsName() => Assert.Equal("Core", Team.Create("  Core ").Name);

    [Fact]
    public void Create_AssignsId() => Assert.NotEqual(Guid.Empty, Team.Create("Core").Id);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_BlankName_Throws(string name) => Assert.Throws<ValidationFailedException>(() => Team.Create(name));

    [Fact]
    public void Create_NameTooLong_Throws() =>
        Assert.Throws<ValidationFailedException>(() => Team.Create(new string('a', Team.NameMaxLength + 1)));

    [Fact]
    public void Rename_ValidName_Updates()
    {
        var team = Team.Create("Old");
        team.Rename("New");
        Assert.Equal("New", team.Name);
    }

    [Fact]
    public void Rename_BlankName_Throws() => Assert.Throws<ValidationFailedException>(() => Team.Create("Old").Rename(" "));
}

public class TeamMemberTests
{
    [Fact]
    public void Create_SetsAllFields()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var member = TeamMember.Create(teamId, userId, TeamRole.Editor, now);

        Assert.Equal(teamId, member.TeamId);
        Assert.Equal(userId, member.UserId);
        Assert.Equal(TeamRole.Editor, member.Role);
        Assert.Equal(now, member.JoinedAt);
    }
}

public class TeamInviteTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TeamInvite NewInvite() =>
        TeamInvite.Create(Guid.NewGuid(), "  Ann@Example.com ", TeamRole.Editor, "hash", Guid.NewGuid(), Now, TimeSpan.FromDays(7));

    [Fact]
    public void Create_NormalizesEmailAndSetsExpiry()
    {
        var invite = NewInvite();

        Assert.Equal("ann@example.com", invite.Email);
        Assert.Equal(Now.AddDays(7), invite.ExpiresAt);
        Assert.Equal("hash", invite.TokenHash);
    }

    [Fact]
    public void IsExpired_BeforeExpiry_ReturnsFalse() => Assert.False(NewInvite().IsExpired(Now.AddDays(6)));

    [Fact]
    public void IsExpired_AtExpiry_ReturnsTrue() => Assert.True(NewInvite().IsExpired(Now.AddDays(7)));

    [Theory]
    [InlineData("ANN@example.com", true)]
    [InlineData(" ann@example.com ", true)]
    [InlineData("bob@example.com", false)]
    public void IsFor_ComparesEmailIgnoringCase(string email, bool expected) => Assert.Equal(expected, NewInvite().IsFor(email));
}

public class FolderTests
{
    [Fact]
    public void Create_TrimsNameAndKeepsParent()
    {
        var parent = Guid.NewGuid();
        var folder = Folder.Create(Guid.NewGuid(), parent, " Specs ");

        Assert.Equal("Specs", folder.Name);
        Assert.Equal(parent, folder.ParentId);
    }

    [Fact]
    public void Create_BlankName_Throws() => Assert.Throws<ValidationFailedException>(() => Folder.Create(Guid.NewGuid(), null, " "));

    [Fact]
    public void Create_NameTooLong_Throws() =>
        Assert.Throws<ValidationFailedException>(() => Folder.Create(Guid.NewGuid(), null, new string('x', Folder.NameMaxLength + 1)));

    [Fact]
    public void Rename_ValidName_Updates()
    {
        var folder = Folder.Create(Guid.NewGuid(), null, "A");
        folder.Rename("B");
        Assert.Equal("B", folder.Name);
    }
}

public class TagTests
{
    [Fact]
    public void NormalizeName_TrimsAndLowercases() => Assert.Equal("urgent", Tag.NormalizeName("  URGENT "));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeName_Blank_Throws(string name) => Assert.Throws<ValidationFailedException>(() => Tag.NormalizeName(name));

    [Fact]
    public void NormalizeName_TooLong_Throws() =>
        Assert.Throws<ValidationFailedException>(() => Tag.NormalizeName(new string('a', Tag.NameMaxLength + 1)));

    [Fact]
    public void Create_UsesNormalizedName()
    {
        var teamId = Guid.NewGuid();
        var tag = Tag.Create(teamId, "Bug");

        Assert.Equal("bug", tag.Name);
        Assert.Equal(teamId, tag.TeamId);
    }
}

public class NoteVersionTests
{
    [Fact]
    public void Snapshot_CopiesNoteState()
    {
        var note = Note.Create(Guid.NewGuid(), null, "Title", "Body");
        var editor = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var version = NoteVersion.Snapshot(note, 3, editor, now);

        Assert.Equal(note.Id, version.NoteId);
        Assert.Equal(3, version.VersionNo);
        Assert.Equal("Title", version.Title);
        Assert.Equal("Body", version.ContentMarkdown);
        Assert.Equal(editor, version.EditedBy);
        Assert.Equal(now, version.CreatedAt);
    }
}

public class AttachmentTests
{
    [Fact]
    public void BuildStorageKey_UsesCompactGuidsInThreeSegments()
    {
        var team = Guid.NewGuid();
        var note = Guid.NewGuid();
        var attachment = Guid.NewGuid();

        var key = Attachment.BuildStorageKey(team, note, attachment);

        Assert.Equal($"{team:N}/{note:N}/{attachment:N}", key);
    }
}

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RefreshToken NewToken() => RefreshToken.Create(Guid.NewGuid(), "hash", Now, TimeSpan.FromDays(14));

    [Fact]
    public void Create_SetsExpiry() => Assert.Equal(Now.AddDays(14), NewToken().ExpiresAt);

    [Fact]
    public void IsActive_FreshToken_ReturnsTrue() => Assert.True(NewToken().IsActive(Now.AddDays(1)));

    [Fact]
    public void IsActive_ExpiredToken_ReturnsFalse() => Assert.False(NewToken().IsActive(Now.AddDays(14)));

    [Fact]
    public void IsExpired_BeforeExpiry_ReturnsFalse() => Assert.False(NewToken().IsExpired(Now.AddDays(13)));

    [Fact]
    public void Revoke_MarksRevokedAndInactive()
    {
        var token = NewToken();

        token.Revoke(Now.AddHours(1));

        Assert.True(token.IsRevoked);
        Assert.False(token.IsActive(Now.AddHours(2)));
    }

    [Fact]
    public void Revoke_CalledTwice_KeepsFirstTimestamp()
    {
        var token = NewToken();

        token.Revoke(Now.AddHours(1));
        token.Revoke(Now.AddHours(5));

        Assert.Equal(Now.AddHours(1), token.RevokedAt);
    }
}

public class OutboxEmailTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_StartsUnsentWithNoAttempts()
    {
        var email = OutboxEmail.Create("a@b.test", "Hi", "Body", Now);

        Assert.Null(email.SentAt);
        Assert.Equal(0, email.Attempts);
        Assert.Equal("a@b.test", email.To);
    }

    [Fact]
    public void MarkSent_RecordsTimeAndAttempt()
    {
        var email = OutboxEmail.Create("a@b.test", "Hi", "Body", Now);
        email.MarkFailed("boom");

        email.MarkSent(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(1), email.SentAt);
        Assert.Equal(2, email.Attempts);
        Assert.Null(email.LastError);
    }

    [Fact]
    public void MarkFailed_RecordsErrorAndAttempt()
    {
        var email = OutboxEmail.Create("a@b.test", "Hi", "Body", Now);

        email.MarkFailed("smtp down");

        Assert.Equal(1, email.Attempts);
        Assert.Equal("smtp down", email.LastError);
        Assert.Null(email.SentAt);
    }

    [Fact]
    public void MarkFailed_LongError_IsTruncated()
    {
        var email = OutboxEmail.Create("a@b.test", "Hi", "Body", Now);

        email.MarkFailed(new string('x', 900));

        Assert.Equal(500, email.LastError!.Length);
    }
}

public class CurrentUserExtensionsTests
{
    private sealed class StubUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId { get; } = id;
    }

    [Fact]
    public void RequireId_SignedIn_ReturnsId()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, new StubUser(id).RequireId());
    }

    [Fact]
    public void RequireId_Anonymous_Throws() =>
        Assert.Throws<UnauthorizedAppException>(() => new StubUser(null).RequireId());
}

public class AppExceptionTests
{
    [Fact]
    public void ValidationFailedException_FieldConstructor_BuildsErrorMap()
    {
        var ex = new ValidationFailedException("name", "required");

        Assert.Equal(["required"], ex.Errors["name"]);
    }

    [Fact]
    public void PreconditionFailedException_ExposesCurrentETag() =>
        Assert.Equal("\"abc\"", new PreconditionFailedException("changed", "\"abc\"").CurrentETag);

    [Fact]
    public void NotFoundException_HasDefaultMessage() =>
        Assert.False(string.IsNullOrWhiteSpace(new NotFoundException().Message));

    [Fact]
    public void OtherExceptions_CarryTheirMessages()
    {
        Assert.Equal("c", new ConflictException("c").Message);
        Assert.False(string.IsNullOrWhiteSpace(new ForbiddenException().Message));
        Assert.False(string.IsNullOrWhiteSpace(new UnauthorizedAppException().Message));
        Assert.False(string.IsNullOrWhiteSpace(new PreconditionRequiredException().Message));
    }
}
