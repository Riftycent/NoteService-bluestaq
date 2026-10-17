using System.ComponentModel.DataAnnotations;

namespace NoteService.API.Common;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "NoteService";

    [Required]
    public string Audience { get; set; } = "NoteService";

    /// <summary>HMAC-SHA256 signing key; supply via environment variable or secret store, never source control.</summary>
    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Front-end base URL used to build links in emails.</summary>
    [Required]
    public string PublicBaseUrl { get; set; } = "http://localhost:3000";

    public bool RequireConfirmedEmail { get; set; } = true;

    [Range(1, 60)]
    public int InviteLifetimeDays { get; set; } = 7;
}

public sealed class NotesOptions
{
    public const string SectionName = "Notes";

    /// <summary>Minimum minutes between history snapshots by the same editor.</summary>
    [Range(0, 1440)]
    public int VersionIntervalMinutes { get; set; } = 5;
}

public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";

    [Range(1, 100 * 1024 * 1024)]
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;

    public string[] AllowedContentTypes { get; set; } =
    [
        "image/png", "image/jpeg", "image/gif", "image/webp",
        "application/pdf", "text/plain", "text/markdown", "text/csv"
    ];
}

public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>
    /// Use SQL Server full-text search (requires the Full-Text feature and the catalog created by the migration).
    /// When false, a case-insensitive substring match is used.
    /// </summary>
    public bool UseFullText { get; set; }
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public int PermitPerMinute { get; set; } = 300;
    public int AuthPermitPerMinute { get; set; } = 10;
}
