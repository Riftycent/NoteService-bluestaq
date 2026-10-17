using NoteService.Domain.Exceptions;

namespace NoteService.Domain.Abstractions;

public static class CurrentUserExtensions
{
    /// <summary>Returns the signed-in user's id or throws when the operation is anonymous.</summary>
    public static Guid RequireId(this ICurrentUser currentUser) =>
        currentUser.UserId ?? throw new UnauthorizedAppException("Authentication is required.");
}
