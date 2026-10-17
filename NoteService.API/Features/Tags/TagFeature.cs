using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Notes;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Tags;

public sealed record TagDto(Guid Id, string Name, int NoteCount);

public sealed record CreateTagRequest
{
    [Required, StringLength(Tag.NameMaxLength)]
    public string Name { get; init; } = string.Empty;
}

public interface ITagService
{
    Task<IReadOnlyList<TagDto>> ListAsync(Guid teamId, CancellationToken ct);

    Task<TagDto> CreateAsync(Guid teamId, string name, CancellationToken ct);

    Task DeleteAsync(Guid teamId, Guid tagId, CancellationToken ct);

    Task<IReadOnlyList<TagRefDto>> AddToNoteAsync(Guid teamId, Guid noteId, Guid tagId, CancellationToken ct);

    Task<IReadOnlyList<TagRefDto>> RemoveFromNoteAsync(Guid teamId, Guid noteId, Guid tagId, CancellationToken ct);
}

public sealed class TagService(NoteDbContext db, ITeamAccess teamAccess, INoteAccess noteAccess) : ITagService
{
    public async Task<IReadOnlyList<TagDto>> ListAsync(Guid teamId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        return await db.Tags
            .Where(t => t.TeamId == teamId)
            .OrderBy(t => t.Name)
            .Select(t => new TagDto(t.Id, t.Name, db.NoteTags.Count(nt => nt.TagId == t.Id)))
            .ToListAsync(ct);
    }

    public async Task<TagDto> CreateAsync(Guid teamId, string name, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        var tag = Tag.Create(teamId, name);

        if (await db.Tags.AnyAsync(t => t.TeamId == teamId && t.Name == tag.Name, ct))
        {
            throw new ConflictException("A tag with that name already exists.");
        }

        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        return new TagDto(tag.Id, tag.Name, 0);
    }

    public async Task DeleteAsync(Guid teamId, Guid tagId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        if (!await db.Tags.AnyAsync(t => t.Id == tagId && t.TeamId == teamId, ct))
        {
            throw new NotFoundException("Tag not found.");
        }

        await db.InTransactionAsync(async token =>
        {
            await db.NoteTags.Where(nt => nt.TagId == tagId).ExecuteDeleteAsync(token);
            await db.Tags.Where(t => t.Id == tagId).ExecuteDeleteAsync(token);
        }, ct);
    }

    public async Task<IReadOnlyList<TagRefDto>> AddToNoteAsync(Guid teamId, Guid noteId, Guid tagId, CancellationToken ct)
    {
        await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, ct: ct);
        if (!await db.Tags.AnyAsync(t => t.Id == tagId && t.TeamId == teamId, ct))
        {
            throw new NotFoundException("Tag not found.");
        }

        if (!await db.NoteTags.AnyAsync(nt => nt.NoteId == noteId && nt.TagId == tagId, ct))
        {
            db.NoteTags.Add(new NoteTag { NoteId = noteId, TagId = tagId });
            await db.SaveChangesAsync(ct);
        }

        return await TagsOfNoteAsync(noteId, ct);
    }

    public async Task<IReadOnlyList<TagRefDto>> RemoveFromNoteAsync(Guid teamId, Guid noteId, Guid tagId, CancellationToken ct)
    {
        await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, ct: ct);
        await db.NoteTags.Where(nt => nt.NoteId == noteId && nt.TagId == tagId).ExecuteDeleteAsync(ct);
        return await TagsOfNoteAsync(noteId, ct);
    }

    private async Task<IReadOnlyList<TagRefDto>> TagsOfNoteAsync(Guid noteId, CancellationToken ct) =>
        await db.NoteTags
            .Where(nt => nt.NoteId == noteId)
            .OrderBy(nt => nt.Tag.Name)
            .Select(nt => new TagRefDto(nt.TagId, nt.Tag.Name))
            .ToListAsync(ct);
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/tags")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class TagsController(ITagService tags) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TagDto>>> List(Guid teamId, CancellationToken ct) =>
        Ok(await tags.ListAsync(teamId, ct));

    [HttpPost]
    [TeamAuthorize(TeamRole.Editor)]
    [ProducesResponseType<TagDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TagDto>> Create(Guid teamId, CreateTagRequest request, CancellationToken ct)
    {
        var tag = await tags.CreateAsync(teamId, request.Name, ct);
        return CreatedAtAction(nameof(List), new { teamId }, tag);
    }

    [HttpDelete("{tagId:guid}")]
    [TeamAuthorize(TeamRole.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid teamId, Guid tagId, CancellationToken ct)
    {
        await tags.DeleteAsync(teamId, tagId, ct);
        return NoContent();
    }
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/notes/{{noteId:guid}}/tags")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class NoteTagsController(ITagService tags) : ApiControllerBase
{
    [HttpPut("{tagId:guid}")]
    public async Task<ActionResult<IReadOnlyList<TagRefDto>>> Add(Guid teamId, Guid noteId, Guid tagId, CancellationToken ct) =>
        Ok(await tags.AddToNoteAsync(teamId, noteId, tagId, ct));

    [HttpDelete("{tagId:guid}")]
    public async Task<ActionResult<IReadOnlyList<TagRefDto>>> Remove(Guid teamId, Guid noteId, Guid tagId, CancellationToken ct) =>
        Ok(await tags.RemoveFromNoteAsync(teamId, noteId, tagId, ct));
}
