using NoteService.Domain.Exceptions;

namespace NoteService.Domain.Entities;

public class NoteVersion
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public int VersionNo { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ContentMarkdown { get; set; } = string.Empty;
    public Guid EditedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static NoteVersion Snapshot(Note note, int versionNo, Guid editedBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        NoteId = note.Id,
        VersionNo = versionNo,
        Title = note.Title,
        ContentMarkdown = note.ContentMarkdown,
        EditedBy = editedBy,
        CreatedAt = now
    };
}

public class NoteShare
{
    public Guid NoteId { get; set; }
    public Guid UserId { get; set; }
    public Enums.SharePermission Permission { get; set; }
}

public class Tag
{
    public const int NameMaxLength = 50;

    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Tag names are stored trimmed and lower-cased so they are unique per team regardless of case.</summary>
    public static string NormalizeName(string name)
    {
        var normalized = name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is 0 or > NameMaxLength)
        {
            throw new ValidationFailedException("name", $"Name is required and must be at most {NameMaxLength} characters.");
        }

        return normalized;
    }

    public static Tag Create(Guid teamId, string name) =>
        new() { Id = Guid.CreateVersion7(), TeamId = teamId, Name = NormalizeName(name) };
}

public class NoteTag
{
    public Guid NoteId { get; set; }
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

public class Attachment
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; }

    public static string BuildStorageKey(Guid teamId, Guid noteId, Guid attachmentId) => $"{teamId:N}/{noteId:N}/{attachmentId:N}";
}
