using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Folders;

public sealed record FolderDto(Guid Id, Guid? ParentId, string Name);

public sealed record CreateFolderRequest
{
    [Required, StringLength(Folder.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    public Guid? ParentId { get; init; }
}

public sealed record UpdateFolderRequest
{
    [StringLength(Folder.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>Move under this folder.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Move to the top level (a null <see cref="ParentId"/> alone means "unchanged").</summary>
    public bool MoveToRoot { get; init; }
}

public interface IFolderService
{
    Task<IReadOnlyList<FolderDto>> ListAsync(Guid teamId, CancellationToken ct);

    Task<FolderDto> CreateAsync(Guid teamId, CreateFolderRequest request, CancellationToken ct);

    Task<FolderDto> UpdateAsync(Guid teamId, Guid folderId, UpdateFolderRequest request, CancellationToken ct);

    Task DeleteAsync(Guid teamId, Guid folderId, CancellationToken ct);
}

public sealed class FolderService(NoteDbContext db, ITeamAccess teamAccess) : IFolderService
{
    public async Task<IReadOnlyList<FolderDto>> ListAsync(Guid teamId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        return await db.Folders
            .Where(f => f.TeamId == teamId)
            .OrderBy(f => f.Name)
            .Select(f => new FolderDto(f.Id, f.ParentId, f.Name))
            .ToListAsync(ct);
    }

    public async Task<FolderDto> CreateAsync(Guid teamId, CreateFolderRequest request, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        if (request.ParentId is { } parentId)
        {
            await EnsureFolderExistsAsync(teamId, parentId, ct);
        }

        var folder = Folder.Create(teamId, request.ParentId, request.Name);
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        return ToDto(folder);
    }

    public async Task<FolderDto> UpdateAsync(Guid teamId, Guid folderId, UpdateFolderRequest request, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        var folder = await FindAsync(teamId, folderId, ct);

        if (request.Name is not null)
        {
            folder.Rename(request.Name);
        }

        if (request.MoveToRoot)
        {
            folder.ParentId = null;
        }
        else if (request.ParentId is { } newParent)
        {
            await EnsureNoCycleAsync(teamId, folder.Id, newParent, ct);
            folder.ParentId = newParent;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(folder);
    }

    /// <summary>Deleting a folder keeps its contents: subfolders and notes (including trashed ones) move up to the parent.</summary>
    public async Task DeleteAsync(Guid teamId, Guid folderId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        var folder = await FindAsync(teamId, folderId, ct);
        var parentId = folder.ParentId;

        await db.InTransactionAsync(async token =>
        {
            await db.Notes.IgnoreQueryFilters().Where(n => n.FolderId == folderId)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.FolderId, parentId), token);
            await db.Folders.Where(f => f.ParentId == folderId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, parentId), token);
            await db.Folders.Where(f => f.Id == folderId).ExecuteDeleteAsync(token);
        }, ct);
    }

    private async Task<Folder> FindAsync(Guid teamId, Guid folderId, CancellationToken ct) =>
        await db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.TeamId == teamId, ct)
        ?? throw new NotFoundException("Folder not found.");

    private async Task EnsureFolderExistsAsync(Guid teamId, Guid folderId, CancellationToken ct)
    {
        if (!await db.Folders.AnyAsync(f => f.Id == folderId && f.TeamId == teamId, ct))
        {
            throw new ValidationFailedException("parentId", "The parent folder does not exist in this team.");
        }
    }

    private async Task EnsureNoCycleAsync(Guid teamId, Guid folderId, Guid newParentId, CancellationToken ct)
    {
        var parents = await db.Folders.Where(f => f.TeamId == teamId).ToDictionaryAsync(f => f.Id, f => f.ParentId, ct);
        if (!parents.ContainsKey(newParentId))
        {
            throw new ValidationFailedException("parentId", "The parent folder does not exist in this team.");
        }

        for (Guid? current = newParentId; current is { } id; current = parents.GetValueOrDefault(id))
        {
            if (id == folderId)
            {
                throw new ValidationFailedException("parentId", "A folder cannot be moved into itself or one of its subfolders.");
            }
        }
    }

    private static FolderDto ToDto(Folder f) => new(f.Id, f.ParentId, f.Name);
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/folders")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class FoldersController(IFolderService folders) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FolderDto>>> List(Guid teamId, CancellationToken ct) =>
        Ok(await folders.ListAsync(teamId, ct));

    [HttpPost]
    [TeamAuthorize(TeamRole.Admin)]
    [ProducesResponseType<FolderDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<FolderDto>> Create(Guid teamId, CreateFolderRequest request, CancellationToken ct)
    {
        var folder = await folders.CreateAsync(teamId, request, ct);
        return CreatedAtAction(nameof(List), new { teamId }, folder);
    }

    [HttpPatch("{folderId:guid}")]
    [TeamAuthorize(TeamRole.Admin)]
    public async Task<ActionResult<FolderDto>> Update(Guid teamId, Guid folderId, UpdateFolderRequest request, CancellationToken ct) =>
        await folders.UpdateAsync(teamId, folderId, request, ct);

    [HttpDelete("{folderId:guid}")]
    [TeamAuthorize(TeamRole.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid teamId, Guid folderId, CancellationToken ct)
    {
        await folders.DeleteAsync(teamId, folderId, ct);
        return NoContent();
    }
}
