using NoteService.Domain.Abstractions;
using NoteService.Domain.Exceptions;
using NoteService.Domain.Rules;

namespace NoteService.Domain.Entities;

public class Note : IAuditable, IConcurrencyStamped
{
    public const int TitleMaxLength = 300;
    public const int ContentMaxLength = 1_000_000;

    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid? FolderId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ContentMarkdown { get; set; } = string.Empty;

    /// <summary>Markdown stripped to plain text; this is what search indexes.</summary>
    public string ContentText { get; set; } = string.Empty;

    public bool IsPinned { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public Guid ConcurrencyStamp { get; set; }

    public ICollection<NoteTag> NoteTags { get; set; } = [];

    public bool IsDeleted => DeletedAt.HasValue;

    public static Note Create(Guid teamId, Guid? folderId, string title, string? contentMarkdown)
    {
        var note = new Note { Id = Guid.CreateVersion7(), TeamId = teamId, FolderId = folderId };
        note.SetContent(title, contentMarkdown);
        return note;
    }

    /// <summary>Replaces the title and body, keeping the plain-text projection in sync.</summary>
    public void SetContent(string title, string? contentMarkdown)
    {
        var trimmedTitle = title?.Trim() ?? string.Empty;
        if (trimmedTitle.Length is 0 or > TitleMaxLength)
        {
            throw new ValidationFailedException("title", $"Title is required and must be at most {TitleMaxLength} characters.");
        }

        contentMarkdown ??= string.Empty;
        if (contentMarkdown.Length > ContentMaxLength)
        {
            throw new ValidationFailedException("content", $"Content must be at most {ContentMaxLength} characters.");
        }

        Title = trimmedTitle;
        ContentMarkdown = contentMarkdown;
        ContentText = MarkdownText.ToPlainText(contentMarkdown);
    }

    public void MoveToFolder(Guid? folderId) => FolderId = folderId;

    public void SetPinned(bool pinned) => IsPinned = pinned;

    public void SoftDelete(DateTimeOffset now) => DeletedAt ??= now;

    public void Restore() => DeletedAt = null;
}
