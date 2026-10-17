namespace NoteService.Domain.Enums;

/// <summary>Team roles, ordered by increasing privilege.</summary>
public enum TeamRole
{
    Viewer = 0,
    Editor = 1,
    Admin = 2,
    Owner = 3
}

public enum SharePermission
{
    Read = 0,
    Edit = 1
}

/// <summary>Effective access a user has on a single note.</summary>
public enum NoteAccess
{
    Read = 0,
    Edit = 1
}
