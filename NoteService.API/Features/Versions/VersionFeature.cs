using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Notes;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Versions;

public sealed record NoteVersionSummaryDto(int VersionNo, string Title, Guid EditedBy, DateTimeOffset CreatedAt);

public sealed record NoteVersionDto(int VersionNo, string Title, string Content, Guid EditedBy, DateTimeOffset CreatedAt);

public interface IVersionService
{
    /// <param name="before">Return only versions older than this number (for paging backwards).</param>
    Task<IReadOnlyList<NoteVersionSummaryDto>> ListAsync(Guid teamId, Guid noteId, int? before, int limit, CancellationToken ct);

    Task<NoteVersionDto> GetAsync(Guid teamId, Guid noteId, int versionNo, CancellationToken ct);

    Task<NoteDto> RestoreAsync(Guid teamId, Guid noteId, int versionNo, Guid expectedStamp, CancellationToken ct);
}

public sealed class VersionService(NoteDbContext db, INoteAccess noteAccess, INoteHistory history, INotesService notes) : IVersionService
{
    public async Task<IReadOnlyList<NoteVersionSummaryDto>> ListAsync(Guid teamId, Guid noteId, int? before, int limit, CancellationToken ct)
    {
        await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Read, ct: ct);

        var versions = db.NoteVersions.AsNoTracking().Where(v => v.NoteId == noteId);
        if (before is { } cutoff)
        {
            versions = versions.Where(v => v.VersionNo < cutoff);
        }

        return await versions
            .OrderByDescending(v => v.VersionNo)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(v => new NoteVersionSummaryDto(v.VersionNo, v.Title, v.EditedBy, v.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<NoteVersionDto> GetAsync(Guid teamId, Guid noteId, int versionNo, CancellationToken ct)
    {
        await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Read, ct: ct);
        return await db.NoteVersions.AsNoTracking()
                   .Where(v => v.NoteId == noteId && v.VersionNo == versionNo)
                   .Select(v => new NoteVersionDto(v.VersionNo, v.Title, v.ContentMarkdown, v.EditedBy, v.CreatedAt))
                   .FirstOrDefaultAsync(ct)
               ?? throw new NotFoundException("Version not found.");
    }

    /// <summary>Copies an old version's title and body back onto the note and records the result as a new history entry.</summary>
    public async Task<NoteDto> RestoreAsync(Guid teamId, Guid noteId, int versionNo, Guid expectedStamp, CancellationToken ct)
    {
        var result = await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, ct: ct);
        var note = result.Note;
        if (note.ConcurrencyStamp != expectedStamp)
        {
            throw new PreconditionFailedException(
                "The note was modified since you last read it. Reload it and try again.",
                ETagHelper.Format(note.ConcurrencyStamp));
        }

        var version = await db.NoteVersions.AsNoTracking().FirstOrDefaultAsync(v => v.NoteId == noteId && v.VersionNo == versionNo, ct)
            ?? throw new NotFoundException("Version not found.");

        note.SetContent(version.Title, version.ContentMarkdown);
        await history.SnapshotAsync(note, force: true, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new PreconditionFailedException("The note was modified by someone else while you were saving. Reload it and try again.");
        }

        return await notes.GetAsync(teamId, noteId, ct);
    }
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/notes/{{noteId:guid}}/versions")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class VersionsController(IVersionService versions) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NoteVersionSummaryDto>>> List(
        Guid teamId, Guid noteId, [FromQuery] int? before, [FromQuery] int limit = 25, CancellationToken ct = default) =>
        Ok(await versions.ListAsync(teamId, noteId, before, limit, ct));

    [HttpGet("{versionNo:int}")]
    public async Task<ActionResult<NoteVersionDto>> Get(Guid teamId, Guid noteId, int versionNo, CancellationToken ct) =>
        await versions.GetAsync(teamId, noteId, versionNo, ct);

    /// <summary>Requires an <c>If-Match</c> header carrying the note's current ETag.</summary>
    [HttpPost("{versionNo:int}/restore")]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<NoteDto>> Restore(Guid teamId, Guid noteId, int versionNo, CancellationToken ct)
    {
        var stamp = ETagHelper.RequireIfMatch(Request);
        var note = await versions.RestoreAsync(teamId, noteId, versionNo, stamp, ct);
        SetETag(note.ETag);
        return note;
    }
}
