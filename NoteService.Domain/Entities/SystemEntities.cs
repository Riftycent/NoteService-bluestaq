namespace NoteService.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public static RefreshToken Create(Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + lifetime
    };

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public class OutboxEmail
{
    public const int MaxAttempts = 5;

    public Guid Id { get; set; }
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public static OutboxEmail Create(string to, string subject, string body, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), To = to, Subject = subject, Body = body, CreatedAt = now };

    public void MarkSent(DateTimeOffset now)
    {
        SentAt = now;
        Attempts++;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length > 500 ? error[..500] : error;
    }
}
