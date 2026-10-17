using NoteService.Domain.Abstractions;
using NoteService.Domain.Exceptions;

namespace NoteService.Domain.Entities;

public class Team : IAuditable
{
    public const int NameMaxLength = 100;

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }

    public static Team Create(string name) => new() { Id = Guid.CreateVersion7(), Name = Normalize(name) };

    public void Rename(string name) => Name = Normalize(name);

    private static string Normalize(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new ValidationFailedException("name", $"Name is required and must be at most {NameMaxLength} characters.");
        }

        return trimmed;
    }
}

public class TeamMember
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public Enums.TeamRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public static TeamMember Create(Guid teamId, Guid userId, Enums.TeamRole role, DateTimeOffset now) =>
        new() { TeamId = teamId, UserId = userId, Role = role, JoinedAt = now };
}

public class TeamInvite
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string Email { get; set; } = string.Empty;
    public Enums.TeamRole Role { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public Guid InvitedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public static TeamInvite Create(Guid teamId, string email, Enums.TeamRole role, string tokenHash, Guid invitedBy, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(),
        TeamId = teamId,
        Email = email.Trim().ToLowerInvariant(),
        Role = role,
        TokenHash = tokenHash,
        InvitedBy = invitedBy,
        CreatedAt = now,
        ExpiresAt = now + lifetime
    };

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsFor(string email) => string.Equals(Email, email.Trim(), StringComparison.OrdinalIgnoreCase);
}
