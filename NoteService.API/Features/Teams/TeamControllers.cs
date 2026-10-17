using Microsoft.AspNetCore.Mvc;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Enums;

namespace NoteService.API.Features.Teams;

[Route($"{ApiRoutes.Base}/teams")]
public sealed class TeamsController(ITeamService teams) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TeamDto>>> List(CancellationToken ct) =>
        Ok(await teams.ListAsync(ct));

    [HttpPost]
    [ProducesResponseType<TeamDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TeamDto>> Create(CreateTeamRequest request, CancellationToken ct)
    {
        var team = await teams.CreateAsync(request.Name, ct);
        return CreatedAtAction(nameof(Get), new { teamId = team.Id }, team);
    }

    [HttpGet("{teamId:guid}")]
    [TeamAuthorize(TeamRole.Viewer)]
    public async Task<ActionResult<TeamDto>> Get(Guid teamId, CancellationToken ct) =>
        await teams.GetAsync(teamId, ct);

    [HttpPatch("{teamId:guid}")]
    [TeamAuthorize(TeamRole.Admin)]
    public async Task<ActionResult<TeamDto>> Update(Guid teamId, UpdateTeamRequest request, CancellationToken ct) =>
        await teams.RenameAsync(teamId, request.Name, ct);

    [HttpDelete("{teamId:guid}")]
    [TeamAuthorize(TeamRole.Owner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid teamId, CancellationToken ct)
    {
        await teams.DeleteAsync(teamId, ct);
        return NoContent();
    }
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/members")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class MembersController(IMemberService members) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MemberDto>>> List(Guid teamId, CancellationToken ct) =>
        Ok(await members.ListAsync(teamId, ct));

    [HttpPatch("{userId:guid}")]
    [TeamAuthorize(TeamRole.Admin)]
    public async Task<ActionResult<MemberDto>> ChangeRole(Guid teamId, Guid userId, ChangeRoleRequest request, CancellationToken ct) =>
        await members.ChangeRoleAsync(teamId, userId, request.Role, ct);

    /// <summary>Removes a member. Any member may remove themselves (leave); removing others requires authority over their role.</summary>
    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid teamId, Guid userId, CancellationToken ct)
    {
        await members.RemoveAsync(teamId, userId, ct);
        return NoContent();
    }
}

[Route(ApiRoutes.Base)]
public sealed class InvitesController(IInviteService invites) : ApiControllerBase
{
    [HttpGet("teams/{teamId:guid}/invites")]
    [TeamAuthorize(TeamRole.Admin)]
    public async Task<ActionResult<IReadOnlyList<InviteDto>>> List(Guid teamId, CancellationToken ct) =>
        Ok(await invites.ListAsync(teamId, ct));

    [HttpPost("teams/{teamId:guid}/invites")]
    [TeamAuthorize(TeamRole.Admin)]
    [ProducesResponseType<InviteDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<InviteDto>> Create(Guid teamId, CreateInviteRequest request, CancellationToken ct)
    {
        var invite = await invites.CreateAsync(teamId, request.Email, request.Role, ct);
        return CreatedAtAction(nameof(List), new { teamId }, invite);
    }

    [HttpDelete("teams/{teamId:guid}/invites/{inviteId:guid}")]
    [TeamAuthorize(TeamRole.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid teamId, Guid inviteId, CancellationToken ct)
    {
        await invites.RevokeAsync(teamId, inviteId, ct);
        return NoContent();
    }

    [HttpPost("invites/{token}/accept")]
    public async Task<ActionResult<TeamDto>> Accept(string token, CancellationToken ct) =>
        await invites.AcceptAsync(token, ct);
}
