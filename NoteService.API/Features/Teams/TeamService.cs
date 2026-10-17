using Microsoft.EntityFrameworkCore;
using NoteService.API.Authorization;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;

namespace NoteService.API.Features.Teams;

public interface ITeamService
{
    Task<IReadOnlyList<TeamDto>> ListAsync(CancellationToken ct);

    Task<TeamDto> CreateAsync(string name, CancellationToken ct);

    Task<TeamDto> GetAsync(Guid teamId, CancellationToken ct);

    Task<TeamDto> RenameAsync(Guid teamId, string name, CancellationToken ct);

    Task DeleteAsync(Guid teamId, CancellationToken ct);
}

public sealed class TeamService(
    NoteDbContext db,
    ICurrentUser currentUser,
    ITeamAccess teamAccess,
    IBlobStore blobs,
    TimeProvider time) : ITeamService
{
    public async Task<IReadOnlyList<TeamDto>> ListAsync(CancellationToken ct)
    {
        var userId = currentUser.RequireId();
        var rows = await db.TeamMembers
            .Where(m => m.UserId == userId)
            .Join(db.Teams, m => m.TeamId, t => t.Id, (m, t) => new { t.Id, t.Name, m.Role, t.CreatedAt })
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return rows.Select(x => new TeamDto(x.Id, x.Name, x.Role, x.CreatedAt)).ToList();
    }

    public async Task<TeamDto> CreateAsync(string name, CancellationToken ct)
    {
        var userId = currentUser.RequireId();
        var team = Team.Create(name);
        db.Teams.Add(team);
        db.TeamMembers.Add(TeamMember.Create(team.Id, userId, TeamRole.Owner, time.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return new TeamDto(team.Id, team.Name, TeamRole.Owner, team.CreatedAt);
    }

    public async Task<TeamDto> GetAsync(Guid teamId, CancellationToken ct)
    {
        var role = await teamAccess.RequireRoleAsync(teamId, ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct)
            ?? throw new Domain.Exceptions.NotFoundException("Team not found.");
        return new TeamDto(team.Id, team.Name, role, team.CreatedAt);
    }

    public async Task<TeamDto> RenameAsync(Guid teamId, string name, CancellationToken ct)
    {
        var role = await teamAccess.RequireRoleAsync(teamId, ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct)
            ?? throw new Domain.Exceptions.NotFoundException("Team not found.");
        team.Rename(name);
        await db.SaveChangesAsync(ct);
        return new TeamDto(team.Id, team.Name, role, team.CreatedAt);
    }

    public async Task DeleteAsync(Guid teamId, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);

        var teamNotes = db.Notes.IgnoreQueryFilters().Where(n => n.TeamId == teamId);
        var storageKeys = await db.Attachments
            .Where(a => teamNotes.Any(n => n.Id == a.NoteId))
            .Select(a => a.StorageKey)
            .ToListAsync(ct);

        await db.InTransactionAsync(async token =>
        {
            await db.NoteTags.Where(x => teamNotes.Any(n => n.Id == x.NoteId)).ExecuteDeleteAsync(token);
            await db.NoteShares.Where(x => teamNotes.Any(n => n.Id == x.NoteId)).ExecuteDeleteAsync(token);
            await db.NoteVersions.Where(x => teamNotes.Any(n => n.Id == x.NoteId)).ExecuteDeleteAsync(token);
            await db.Attachments.Where(x => teamNotes.Any(n => n.Id == x.NoteId)).ExecuteDeleteAsync(token);
            await teamNotes.ExecuteDeleteAsync(token);
            await db.Tags.IgnoreQueryFilters().Where(t => t.TeamId == teamId).ExecuteDeleteAsync(token);
            await db.Folders.IgnoreQueryFilters().Where(f => f.TeamId == teamId).ExecuteDeleteAsync(token);
            await db.TeamInvites.Where(i => i.TeamId == teamId).ExecuteDeleteAsync(token);
            await db.TeamMembers.Where(m => m.TeamId == teamId).ExecuteDeleteAsync(token);
            await db.Teams.IgnoreQueryFilters().Where(t => t.Id == teamId).ExecuteDeleteAsync(token);
        }, ct);

        foreach (var key in storageKeys)
        {
            // Best effort: anything left behind is removed later by the orphan-blob job.
            try
            {
                await blobs.DeleteAsync(key, ct);
            }
            catch (IOException)
            {
            }
        }
    }
}
