using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Domain.Rules;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Authorization;

/// <summary>Resolves the current user's role in a team, caching the answer for the lifetime of the request.</summary>
public interface ITeamAccess
{
    /// <returns>The role, or null when the user is not a member (or not signed in).</returns>
    Task<TeamRole?> GetRoleAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Returns the role or throws <see cref="NotFoundException"/> so team existence is not leaked to non-members.</summary>
    Task<TeamRole> RequireRoleAsync(Guid teamId, CancellationToken ct = default);
}

public sealed class TeamAccess(NoteDbContext db, ICurrentUser currentUser) : ITeamAccess
{
    private readonly Dictionary<Guid, TeamRole?> _cache = [];

    public async Task<TeamRole?> GetRoleAsync(Guid teamId, CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        if (!_cache.TryGetValue(teamId, out var role))
        {
            role = await db.TeamMembers
                .Where(m => m.TeamId == teamId && m.UserId == userId)
                .Select(m => (TeamRole?)m.Role)
                .FirstOrDefaultAsync(ct);
            _cache[teamId] = role;
        }

        return role;
    }

    public async Task<TeamRole> RequireRoleAsync(Guid teamId, CancellationToken ct = default) =>
        await GetRoleAsync(teamId, ct) ?? throw new NotFoundException("Team not found.");
}

public sealed record TeamRoleRequirement(TeamRole Minimum) : IAuthorizationRequirement;

/// <summary>Succeeds when the user's role in the team (the resource, identified by its id) is at least the required role.</summary>
public sealed class TeamRoleHandler(ITeamAccess teamAccess) : AuthorizationHandler<TeamRoleRequirement, Guid>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TeamRoleRequirement requirement, Guid teamId)
    {
        var role = await teamAccess.GetRoleAsync(teamId);
        if (role is { } actual && actual >= requirement.Minimum)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Requires a minimum team role for the <c>teamId</c> route value. Non-members get 404 (team existence is not leaked),
/// members with an insufficient role get 403.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TeamAuthorizeAttribute(TeamRole minimum = TeamRole.Viewer) : Attribute, IAsyncAuthorizationFilter
{
    public TeamRole Minimum { get; } = minimum;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new Microsoft.AspNetCore.Mvc.UnauthorizedResult();
            return;
        }

        if (!context.RouteData.Values.TryGetValue("teamId", out var raw) || !Guid.TryParse(raw?.ToString(), out var teamId))
        {
            throw new NotFoundException();
        }

        var teamAccess = http.RequestServices.GetRequiredService<ITeamAccess>();
        if (await teamAccess.GetRoleAsync(teamId, http.RequestAborted) is null)
        {
            throw new NotFoundException("Team not found.");
        }

        var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(http.User, teamId, new TeamRoleRequirement(Minimum));
        if (!result.Succeeded)
        {
            throw new ForbiddenException();
        }
    }
}

public sealed record NoteAccessResult(Note Note, TeamRole Role, NoteAccess Access);

/// <summary>Loads a note and verifies the caller's effective access (team role combined with any per-note share).</summary>
public interface INoteAccess
{
    /// <exception cref="NotFoundException">Not a team member, or the note does not exist in the team.</exception>
    /// <exception cref="ForbiddenException">The caller's effective access is lower than required.</exception>
    Task<NoteAccessResult> RequireAsync(Guid teamId, Guid noteId, NoteAccess required, bool includeDeleted = false, CancellationToken ct = default);
}

public sealed class NoteAccessService(NoteDbContext db, ITeamAccess teamAccess, ICurrentUser currentUser) : INoteAccess
{
    public async Task<NoteAccessResult> RequireAsync(Guid teamId, Guid noteId, NoteAccess required, bool includeDeleted = false, CancellationToken ct = default)
    {
        var userId = currentUser.RequireId();
        var role = await teamAccess.RequireRoleAsync(teamId, ct);

        var query = db.Notes.Where(n => n.Id == noteId && n.TeamId == teamId);
        if (includeDeleted)
        {
            query = query.IgnoreQueryFilters([NoteDbContext.SoftDeleteFilter]);
        }

        var note = await query.FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Note not found.");

        var share = await db.NoteShares
            .Where(s => s.NoteId == noteId && s.UserId == userId)
            .Select(s => (SharePermission?)s.Permission)
            .FirstOrDefaultAsync(ct);

        var access = NoteAccessPolicy.Resolve(role, share);
        if (access < required)
        {
            throw new ForbiddenException();
        }

        return new NoteAccessResult(note, role, access);
    }
}
