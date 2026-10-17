using NoteService.Domain.Enums;

namespace NoteService.Domain.Rules;

public static class NoteAccessPolicy
{
    /// <summary>
    /// Resolves a team member's access to one note. Admins and owners always have full access.
    /// For other roles, a per-note share overrides the baseline: a Read share restricts an editor,
    /// an Edit share elevates a viewer.
    /// </summary>
    public static NoteAccess Resolve(TeamRole role, SharePermission? share)
    {
        if (role >= TeamRole.Admin)
        {
            return NoteAccess.Edit;
        }

        if (share is { } permission)
        {
            return permission == SharePermission.Edit ? NoteAccess.Edit : NoteAccess.Read;
        }

        return role >= TeamRole.Editor ? NoteAccess.Edit : NoteAccess.Read;
    }
}

public static class MembershipRules
{
    /// <summary>Owners may invite any role; admins may invite editors and viewers.</summary>
    public static bool CanInvite(TeamRole actor, TeamRole invitedRole) =>
        actor switch
        {
            TeamRole.Owner => true,
            TeamRole.Admin => invitedRole <= TeamRole.Editor,
            _ => false
        };

    /// <summary>Owners may change any role; admins may only manage editors and viewers.</summary>
    public static bool CanChangeRole(TeamRole actor, TeamRole targetCurrent, TeamRole newRole) =>
        actor switch
        {
            TeamRole.Owner => true,
            TeamRole.Admin => targetCurrent <= TeamRole.Editor && newRole <= TeamRole.Editor,
            _ => false
        };

    /// <summary>Anyone may leave a team; removing someone else requires authority over their role.</summary>
    public static bool CanRemove(TeamRole actor, TeamRole target, bool isSelf) =>
        isSelf || actor switch
        {
            TeamRole.Owner => true,
            TeamRole.Admin => target <= TeamRole.Editor,
            _ => false
        };
}
