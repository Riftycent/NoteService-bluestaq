using Microsoft.AspNetCore.Mvc;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Enums;

namespace NoteService.API.Features.Notes;

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/notes")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class NotesController(INotesService notes) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Page<NoteSummaryDto>>> List(Guid teamId, [FromQuery] NoteListQuery query, CancellationToken ct) =>
        await notes.ListAsync(teamId, query, ct);

    [HttpPost]
    [TeamAuthorize(TeamRole.Editor)]
    [ProducesResponseType<NoteDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<NoteDto>> Create(Guid teamId, CreateNoteRequest request, CancellationToken ct)
    {
        var note = await notes.CreateAsync(teamId, request, ct);
        SetETag(note.ETag);
        return CreatedAtAction(nameof(Get), new { teamId, noteId = note.Id }, note);
    }

    [HttpGet("{noteId:guid}")]
    public async Task<ActionResult<NoteDto>> Get(Guid teamId, Guid noteId, CancellationToken ct)
    {
        var note = await notes.GetAsync(teamId, noteId, ct);
        SetETag(note.ETag);
        return note;
    }

    /// <summary>Partial update. Requires an <c>If-Match</c> header carrying the note's current ETag.</summary>
    [HttpPatch("{noteId:guid}")]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<NoteDto>> Update(Guid teamId, Guid noteId, UpdateNoteRequest request, CancellationToken ct)
    {
        var stamp = ETagHelper.RequireIfMatch(Request);
        var note = await notes.UpdateAsync(teamId, noteId, request, stamp, ct);
        SetETag(note.ETag);
        return note;
    }

    /// <summary>Moves the note to the trash. Requires an <c>If-Match</c> header.</summary>
    [HttpDelete("{noteId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid teamId, Guid noteId, CancellationToken ct)
    {
        var stamp = ETagHelper.RequireIfMatch(Request);
        await notes.DeleteAsync(teamId, noteId, stamp, ct);
        return NoContent();
    }

    [HttpPost("{noteId:guid}/restore")]
    public async Task<ActionResult<NoteDto>> Restore(Guid teamId, Guid noteId, CancellationToken ct)
    {
        var note = await notes.RestoreAsync(teamId, noteId, ct);
        SetETag(note.ETag);
        return note;
    }
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/trash")]
[TeamAuthorize(TeamRole.Editor)]
public sealed class TrashController(INotesService notes) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NoteSummaryDto>>> List(Guid teamId, CancellationToken ct) =>
        Ok(await notes.ListTrashAsync(teamId, ct));
}
