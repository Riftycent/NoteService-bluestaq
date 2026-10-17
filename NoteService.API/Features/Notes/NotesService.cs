using Microsoft.EntityFrameworkCore;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Notes;

public interface INotesService
{
    Task<Page<NoteSummaryDto>> ListAsync(Guid teamId, NoteListQuery query, CancellationToken ct);

    Task<IReadOnlyList<NoteSummaryDto>> ListTrashAsync(Guid teamId, CancellationToken ct);

    Task<NoteDto> GetAsync(Guid teamId, Guid noteId, CancellationToken ct);

    Task<NoteDto> CreateAsync(Guid teamId, CreateNoteRequest request, CancellationToken ct);

    Task<NoteDto> UpdateAsync(Guid teamId, Guid noteId, UpdateNoteRequest request, Guid expectedStamp, CancellationToken ct);

    Task DeleteAsync(Guid teamId, Guid noteId, Guid expectedStamp, CancellationToken ct);

    Task<NoteDto> RestoreAsync(Guid teamId, Guid noteId, CancellationToken ct);
}

public sealed class NotesService(
    NoteDbContext db,
    INoteAccess noteAccess,
    ITeamAccess teamAccess,
    INoteHistory history,
    ICurrentUser currentUser,
    TimeProvider time) : INotesService
{
    private const int ExcerptLength = 200;
    private const int TrashLimit = 200;

    public async Task<Page<NoteSummaryDto>> ListAsync(Guid teamId, NoteListQuery query, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);

        var notes = db.Notes.AsNoTracking().Where(n => n.TeamId == teamId);
        if (query.FolderId is { } folderId)
        {
            notes = notes.Where(n => n.FolderId == folderId);
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tagName = Tag.NormalizeName(query.Tag);
            notes = notes.Where(n => n.NoteTags.Any(nt => nt.Tag.Name == tagName));
        }

        if (query.Pinned is { } pinned)
        {
            notes = notes.Where(n => n.IsPinned == pinned);
        }

        if (query.UpdatedSince is { } since)
        {
            notes = notes.Where(n => n.UpdatedAt >= since);
        }

        var descending = query.Order is { } order ? order == SortOrder.Desc : query.Sort == NoteSort.UpdatedAt;
        var cursor = query.Cursor is null ? null : Cursor.Decode(query.Cursor);

        var rows = await ProjectSummaries(ApplySort(notes, query.Sort, descending, cursor))
            .Take(query.Limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > query.Limit;
        var items = rows.Take(query.Limit).ToList();
        var next = hasMore ? Cursor.Encode(SortKey(items[^1], query.Sort), items[^1].Id) : null;
        return new Page<NoteSummaryDto>(items, next);
    }

    public async Task<IReadOnlyList<NoteSummaryDto>> ListTrashAsync(Guid teamId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        var trashed = db.Notes.AsNoTracking()
            .IgnoreQueryFilters([NoteDbContext.SoftDeleteFilter])
            .Where(n => n.TeamId == teamId && n.DeletedAt != null)
            .OrderByDescending(n => n.DeletedAt)
            .ThenByDescending(n => n.Id);

        return await ProjectSummaries(trashed).Take(TrashLimit).ToListAsync(ct);
    }

    public async Task<NoteDto> GetAsync(Guid teamId, Guid noteId, CancellationToken ct)
    {
        var result = await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Read, ct: ct);
        return await ToDtoAsync(result.Note, result.Access, ct);
    }

    public async Task<NoteDto> CreateAsync(Guid teamId, CreateNoteRequest request, CancellationToken ct)
    {
        currentUser.RequireId();
        var role = await teamAccess.RequireRoleAsync(teamId, ct);
        if (role < TeamRole.Editor)
        {
            throw new ForbiddenException();
        }

        if (request.FolderId is { } folderId)
        {
            await EnsureFolderAsync(teamId, folderId, ct);
        }

        var note = Note.Create(teamId, request.FolderId, request.Title, request.Content);
        note.SetPinned(request.IsPinned);
        db.Notes.Add(note);
        await history.SnapshotAsync(note, force: true, ct);
        await db.SaveChangesAsync(ct);

        return await ToDtoAsync(note, NoteAccess.Edit, ct);
    }

    public async Task<NoteDto> UpdateAsync(Guid teamId, Guid noteId, UpdateNoteRequest request, Guid expectedStamp, CancellationToken ct)
    {
        var result = await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, ct: ct);
        var note = result.Note;
        EnsureStamp(note, expectedStamp);

        var contentChanged = false;
        if (request.Title is not null || request.Content is not null)
        {
            var title = request.Title ?? note.Title;
            var content = request.Content ?? note.ContentMarkdown;
            if (title.Trim() != note.Title || content != note.ContentMarkdown)
            {
                note.SetContent(title, content);
                contentChanged = true;
            }
        }

        if (request.MoveToRoot)
        {
            note.MoveToFolder(null);
        }
        else if (request.FolderId is { } folderId && folderId != note.FolderId)
        {
            await EnsureFolderAsync(teamId, folderId, ct);
            note.MoveToFolder(folderId);
        }

        if (request.IsPinned is { } pinned)
        {
            note.SetPinned(pinned);
        }

        if (contentChanged)
        {
            await history.SnapshotAsync(note, force: false, ct);
        }

        await SaveAsync(ct);
        return await ToDtoAsync(note, result.Access, ct);
    }

    public async Task DeleteAsync(Guid teamId, Guid noteId, Guid expectedStamp, CancellationToken ct)
    {
        var result = await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, ct: ct);
        EnsureStamp(result.Note, expectedStamp);
        result.Note.SoftDelete(time.GetUtcNow());
        await SaveAsync(ct);
    }

    public async Task<NoteDto> RestoreAsync(Guid teamId, Guid noteId, CancellationToken ct)
    {
        var result = await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, includeDeleted: true, ct);
        result.Note.Restore();
        await SaveAsync(ct);
        return await ToDtoAsync(result.Note, result.Access, ct);
    }

    private static void EnsureStamp(Note note, Guid expected)
    {
        if (note.ConcurrencyStamp != expected)
        {
            throw new PreconditionFailedException(
                "The note was modified since you last read it. Reload it and try again.",
                ETagHelper.Format(note.ConcurrencyStamp));
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new PreconditionFailedException("The note was modified by someone else while you were saving. Reload it and try again.");
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("The change conflicted with a concurrent update. Please retry.");
        }
    }

    private async Task EnsureFolderAsync(Guid teamId, Guid folderId, CancellationToken ct)
    {
        if (!await db.Folders.AnyAsync(f => f.Id == folderId && f.TeamId == teamId, ct))
        {
            throw new ValidationFailedException("folderId", "The folder does not exist in this team.");
        }
    }

    private async Task<NoteDto> ToDtoAsync(Note note, NoteAccess access, CancellationToken ct)
    {
        var tags = await db.NoteTags
            .Where(nt => nt.NoteId == note.Id)
            .OrderBy(nt => nt.Tag.Name)
            .Select(nt => new TagRefDto(nt.TagId, nt.Tag.Name))
            .ToListAsync(ct);

        return new NoteDto(
            note.Id, note.TeamId, note.Title, note.ContentMarkdown, note.FolderId, note.IsPinned, tags,
            note.CreatedBy, note.UpdatedBy, note.CreatedAt, note.UpdatedAt, note.DeletedAt,
            access, ETagHelper.Format(note.ConcurrencyStamp));
    }

    private static IQueryable<NoteSummaryDto> ProjectSummaries(IQueryable<Note> notes) =>
        notes.Select(n => new NoteSummaryDto(
            n.Id,
            n.Title,
            n.ContentText.Substring(0, ExcerptLength),
            n.FolderId,
            n.IsPinned,
            n.NoteTags.Select(nt => nt.Tag.Name).OrderBy(name => name).ToList(),
            n.CreatedBy,
            n.UpdatedBy,
            n.CreatedAt,
            n.UpdatedAt,
            n.DeletedAt));

    private static string SortKey(NoteSummaryDto last, NoteSort sort) =>
        sort == NoteSort.Title ? last.Title : last.UpdatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Applies ordering plus the keyset predicate so each page starts right after the previous page's last row.</summary>
    internal static IQueryable<Note> ApplySort(IQueryable<Note> notes, NoteSort sort, bool descending, CursorToken? cursor)
    {
        var id = cursor?.Id ?? Guid.Empty;

        if (sort == NoteSort.Title)
        {
            var title = cursor?.Key ?? string.Empty;
            if (cursor is not null)
            {
                notes = descending
                    ? notes.Where(n => n.Title.CompareTo(title) < 0 || (n.Title == title && n.Id.CompareTo(id) < 0))
                    : notes.Where(n => n.Title.CompareTo(title) > 0 || (n.Title == title && n.Id.CompareTo(id) > 0));
            }

            return descending
                ? notes.OrderByDescending(n => n.Title).ThenByDescending(n => n.Id)
                : notes.OrderBy(n => n.Title).ThenBy(n => n.Id);
        }

        if (cursor is not null)
        {
            if (!long.TryParse(cursor.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks)
            {
                throw new ValidationFailedException("cursor", "The cursor is not valid.");
            }

            var at = new DateTimeOffset(ticks, TimeSpan.Zero);
            notes = descending
                ? notes.Where(n => n.UpdatedAt < at || (n.UpdatedAt == at && n.Id.CompareTo(id) < 0))
                : notes.Where(n => n.UpdatedAt > at || (n.UpdatedAt == at && n.Id.CompareTo(id) > 0));
        }

        return descending
            ? notes.OrderByDescending(n => n.UpdatedAt).ThenByDescending(n => n.Id)
            : notes.OrderBy(n => n.UpdatedAt).ThenBy(n => n.Id);
    }
}
