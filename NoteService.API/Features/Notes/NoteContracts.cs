using System.ComponentModel.DataAnnotations;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;

namespace NoteService.API.Features.Notes;

public enum NoteSort
{
    UpdatedAt = 0,
    Title = 1
}

public enum SortOrder
{
    Asc = 0,
    Desc = 1
}

public sealed record TagRefDto(Guid Id, string Name);

public sealed record NoteSummaryDto(
    Guid Id,
    string Title,
    string Excerpt,
    Guid? FolderId,
    bool IsPinned,
    IReadOnlyList<string> Tags,
    Guid CreatedBy,
    Guid UpdatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt);

public sealed record NoteDto(
    Guid Id,
    Guid TeamId,
    string Title,
    string Content,
    Guid? FolderId,
    bool IsPinned,
    IReadOnlyList<TagRefDto> Tags,
    Guid CreatedBy,
    Guid UpdatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    NoteAccess Access,
    string ETag);

public sealed record CreateNoteRequest
{
    [Required, StringLength(Note.TitleMaxLength)]
    public string Title { get; init; } = string.Empty;

    [StringLength(Note.ContentMaxLength)]
    public string? Content { get; init; }

    public Guid? FolderId { get; init; }

    public bool IsPinned { get; init; }
}

/// <summary>Partial update: omitted (null) properties are left unchanged.</summary>
public sealed record UpdateNoteRequest
{
    [StringLength(Note.TitleMaxLength)]
    public string? Title { get; init; }

    [StringLength(Note.ContentMaxLength)]
    public string? Content { get; init; }

    public Guid? FolderId { get; init; }

    /// <summary>Moves the note to the top level (a null <see cref="FolderId"/> alone means "unchanged").</summary>
    public bool MoveToRoot { get; init; }

    public bool? IsPinned { get; init; }
}

public sealed record NoteListQuery
{
    public Guid? FolderId { get; init; }

    [StringLength(Domain.Entities.Tag.NameMaxLength)]
    public string? Tag { get; init; }

    public bool? Pinned { get; init; }

    public DateTimeOffset? UpdatedSince { get; init; }

    public NoteSort Sort { get; init; } = NoteSort.UpdatedAt;

    /// <summary>Defaults to descending for updatedAt and ascending for title.</summary>
    public SortOrder? Order { get; init; }

    [StringLength(500)]
    public string? Cursor { get; init; }

    [Range(1, 100)]
    public int Limit { get; init; } = 25;
}
