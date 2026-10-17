using Microsoft.EntityFrameworkCore;
using NoteService.API.Authorization;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Domain.Rules;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Teams;

public interface IMemberService
{
    Task<IReadOnlyList<MemberDto>> ListAsync(Guid teamId, CancellationToken ct);

    Task<MemberDto> ChangeRoleAsync(Guid teamId, Guid userId, TeamRole newRole, CancellationToken ct);

    Task RemoveAsync(Guid teamId, Guid userId, CancellationToken ct);
}

public sealed class MemberService(NoteDbContext db, ITeamAccess teamAccess, ICurrentUser currentUser) : IMemberService
{
    public async Task<IReadOnlyList<MemberDto>> ListAsync(Guid teamId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        var rows = await db.TeamMembers
            .Where(m => m.TeamId == teamId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.UserId, u.Email, u.DisplayName, m.Role, m.JoinedAt })
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);

        return rows.Select(x => new MemberDto(x.UserId, x.Email ?? string.Empty, x.DisplayName, x.Role, x.JoinedAt)).ToList();
    }

    public async Task<MemberDto> ChangeRoleAsync(Guid teamId, Guid userId, TeamRole newRole, CancellationToken ct)
    {
        var actorRole = await teamAccess.RequireRoleAsync(teamId, ct);
        var member = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Member not found.");

        if (!MembershipRules.CanChangeRole(actorRole, member.Role, newRole))
        {
            throw new ForbiddenException();
        }

        if (member.Role == TeamRole.Owner && newRole != TeamRole.Owner)
        {
            await EnsureAnotherOwnerAsync(teamId, userId, ct);
        }

        member.Role = newRole;
        await db.SaveChangesAsync(ct);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        return new MemberDto(userId, user.Email ?? string.Empty, user.DisplayName, member.Role, member.JoinedAt);
    }

    public async Task RemoveAsync(Guid teamId, Guid userId, CancellationToken ct)
    {
        var actorRole = await teamAccess.RequireRoleAsync(teamId, ct);
        var member = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Member not found.");

        var isSelf = currentUser.RequireId() == userId;
        if (!MembershipRules.CanRemove(actorRole, member.Role, isSelf))
        {
            throw new ForbiddenException();
        }

        if (member.Role == TeamRole.Owner)
        {
            await EnsureAnotherOwnerAsync(teamId, userId, ct);
        }

        await db.InTransactionAsync(async token =>
        {
            // Per-note shares only make sense while the person is on the team.
            await db.NoteShares
                .Where(s => s.UserId == userId && db.Notes.IgnoreQueryFilters().Any(n => n.Id == s.NoteId && n.TeamId == teamId))
                .ExecuteDeleteAsync(token);
            await db.TeamMembers.Where(m => m.TeamId == teamId && m.UserId == userId).ExecuteDeleteAsync(token);
        }, ct);
    }

    private async Task EnsureAnotherOwnerAsync(Guid teamId, Guid excludedUserId, CancellationToken ct)
    {
        var otherOwners = await db.TeamMembers.CountAsync(m => m.TeamId == teamId && m.Role == TeamRole.Owner && m.UserId != excludedUserId, ct);
        if (otherOwners == 0)
        {
            throw new ConflictException("A team must keep at least one owner. Promote another member to owner first.");
        }
    }
}
