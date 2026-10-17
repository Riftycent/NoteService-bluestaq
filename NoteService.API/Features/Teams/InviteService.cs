using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Domain.Rules;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Teams;

public interface IInviteService
{
    Task<IReadOnlyList<InviteDto>> ListAsync(Guid teamId, CancellationToken ct);

    Task<InviteDto> CreateAsync(Guid teamId, string email, TeamRole role, CancellationToken ct);

    Task RevokeAsync(Guid teamId, Guid inviteId, CancellationToken ct);

    Task<TeamDto> AcceptAsync(string token, CancellationToken ct);
}

public sealed class InviteService(
    NoteDbContext db,
    ITeamAccess teamAccess,
    ICurrentUser currentUser,
    IEmailOutbox outbox,
    TimeProvider time,
    IOptions<AppOptions> appOptions) : IInviteService
{
    public async Task<IReadOnlyList<InviteDto>> ListAsync(Guid teamId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        var invites = await db.TeamInvites.Where(i => i.TeamId == teamId).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        return invites.Select(ToDto).ToList();
    }

    public async Task<InviteDto> CreateAsync(Guid teamId, string email, TeamRole role, CancellationToken ct)
    {
        var actorRole = await teamAccess.RequireRoleAsync(teamId, ct);
        if (!MembershipRules.CanInvite(actorRole, role))
        {
            throw new ForbiddenException("You cannot invite someone with that role.");
        }

        var normalized = email.Trim().ToLowerInvariant();
        var alreadyMember = await db.TeamMembers
            .Where(m => m.TeamId == teamId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.NormalizedEmail)
            .AnyAsync(e => e == normalized.ToUpperInvariant(), ct);
        if (alreadyMember)
        {
            throw new ConflictException("That person is already a member of this team.");
        }

        var token = TokenHasher.Generate();
        var invite = TeamInvite.Create(
            teamId, normalized, role, TokenHasher.Hash(token), currentUser.RequireId(), time.GetUtcNow(),
            TimeSpan.FromDays(appOptions.Value.InviteLifetimeDays));

        // One open invite per address: a new one replaces the old one.
        await db.TeamInvites.Where(i => i.TeamId == teamId && i.Email == normalized).ExecuteDeleteAsync(ct);
        db.TeamInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        var teamName = await db.Teams.Where(t => t.Id == teamId).Select(t => t.Name).FirstAsync(ct);
        var link = $"{appOptions.Value.PublicBaseUrl.TrimEnd('/')}/invites/{token}";
        await outbox.EnqueueAsync(normalized, $"You're invited to join {teamName} on NoteService", $"Accept your invitation: {link}", ct);

        return ToDto(invite);
    }

    public async Task RevokeAsync(Guid teamId, Guid inviteId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);
        var deleted = await db.TeamInvites.Where(i => i.TeamId == teamId && i.Id == inviteId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            throw new NotFoundException("Invite not found.");
        }
    }

    public async Task<TeamDto> AcceptAsync(string token, CancellationToken ct)
    {
        var userId = currentUser.RequireId();
        var hash = TokenHasher.Hash(token);
        var now = time.GetUtcNow();

        // Every failure looks the same so tokens cannot be probed.
        var invite = await db.TeamInvites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (invite is null || invite.IsExpired(now) || !invite.IsFor(user.Email ?? string.Empty))
        {
            throw new NotFoundException("The invitation is not valid or has expired.");
        }

        var existing = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == invite.TeamId && m.UserId == userId, ct);
        var role = existing?.Role ?? invite.Role;
        if (existing is null)
        {
            db.TeamMembers.Add(TeamMember.Create(invite.TeamId, userId, invite.Role, now));
        }

        db.TeamInvites.Remove(invite);
        await db.SaveChangesAsync(ct);

        var team = await db.Teams.FirstAsync(t => t.Id == invite.TeamId, ct);
        return new TeamDto(team.Id, team.Name, role, team.CreatedAt);
    }

    private static InviteDto ToDto(TeamInvite i) => new(i.Id, i.Email, i.Role, i.CreatedAt, i.ExpiresAt);
}
