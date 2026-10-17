using System.ComponentModel.DataAnnotations;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;

namespace NoteService.API.Features.Teams;

public sealed record TeamDto(Guid Id, string Name, TeamRole Role, DateTimeOffset CreatedAt);

public sealed record CreateTeamRequest
{
    [Required, StringLength(Team.NameMaxLength)]
    public string Name { get; init; } = string.Empty;
}

public sealed record UpdateTeamRequest
{
    [Required, StringLength(Team.NameMaxLength)]
    public string Name { get; init; } = string.Empty;
}

public sealed record MemberDto(Guid UserId, string Email, string DisplayName, TeamRole Role, DateTimeOffset JoinedAt);

public sealed record ChangeRoleRequest
{
    [Required, EnumDataType(typeof(TeamRole))]
    public TeamRole Role { get; init; }
}

public sealed record InviteDto(Guid Id, string Email, TeamRole Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record CreateInviteRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [EnumDataType(typeof(TeamRole))]
    public TeamRole Role { get; init; } = TeamRole.Editor;
}
