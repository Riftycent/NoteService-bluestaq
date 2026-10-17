using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Shares;

public sealed record ShareDto(Guid UserId, string Email, string DisplayName, SharePermission Permission);

public sealed record SetShareRequest
{
    [Required, EnumDataType(typeof(SharePermission))]
    public SharePermission Permission { get; init; }
}

public interface IShareService
{
    Task<IReadOnlyList<ShareDto>> ListAsync(Guid teamId, Guid noteId, CancellationToken ct);

    Task<ShareDto> SetAsync(Guid teamId, Guid noteId, Guid userId, SharePermission permission, CancellationToken ct);

    Task RemoveAsync(Guid teamId, Guid noteId, Guid userId, CancellationToken ct);
}

/// <summary>Per-note overrides on top of a member's team role. Only team admins/owners and the note's creator can manage them.</summary>
public sealed class ShareService(NoteDbContext db, INoteAccess noteAccess, ICurrentUser currentUser) : IShareService
{
    public async Task<IReadOnlyList<ShareDto>> ListAsync(Guid teamId, Guid noteId, CancellationToken ct)
    {
        await RequireManageAsync(teamId, noteId, ct);
        var rows = await db.NoteShares
            .Where(s => s.NoteId == noteId)
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { s.UserId, u.Email, u.DisplayName, s.Permission })
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);

        return rows.Select(x => new ShareDto(x.UserId, x.Email ?? string.Empty, x.DisplayName, x.Permission)).ToList();
    }

    public async Task<ShareDto> SetAsync(Guid teamId, Guid noteId, Guid userId, SharePermission permission, CancellationToken ct)
    {
        await RequireManageAsync(teamId, noteId, ct);

        var isMember = await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);
        if (!isMember)
        {
            throw new ValidationFailedException("userId", "Notes can only be shared with members of the team.");
        }

        var share = await db.NoteShares.FirstOrDefaultAsync(s => s.NoteId == noteId && s.UserId == userId, ct);
        if (share is null)
        {
            db.NoteShares.Add(new NoteShare { NoteId = noteId, UserId = userId, Permission = permission });
        }
        else
        {
            share.Permission = permission;
        }

        await db.SaveChangesAsync(ct);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        return new ShareDto(userId, user.Email ?? string.Empty, user.DisplayName, permission);
    }

    public async Task RemoveAsync(Guid teamId, Guid noteId, Guid userId, CancellationToken ct)
    {
        await RequireManageAsync(teamId, noteId, ct);
        var deleted = await db.NoteShares.Where(s => s.NoteId == noteId && s.UserId == userId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            throw new NotFoundException("Share not found.");
        }
    }

    private async Task RequireManageAsync(Guid teamId, Guid noteId, CancellationToken ct)
    {
        var result = await noteAccess.RequireAsync(teamId, noteId, NoteAccess.Edit, ct: ct);
        if (result.Role < TeamRole.Admin && result.Note.CreatedBy != currentUser.RequireId())
        {
            throw new ForbiddenException("Only the note's author or a team admin can manage sharing.");
        }
    }
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/notes/{{noteId:guid}}/shares")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class SharesController(IShareService shares) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ShareDto>>> List(Guid teamId, Guid noteId, CancellationToken ct) =>
        Ok(await shares.ListAsync(teamId, noteId, ct));

    [HttpPut("{userId:guid}")]
    public async Task<ActionResult<ShareDto>> Set(Guid teamId, Guid noteId, Guid userId, SetShareRequest request, CancellationToken ct) =>
        await shares.SetAsync(teamId, noteId, userId, request.Permission, ct);

    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid teamId, Guid noteId, Guid userId, CancellationToken ct)
    {
        await shares.RemoveAsync(teamId, noteId, userId, ct);
        return NoContent();
    }
}
