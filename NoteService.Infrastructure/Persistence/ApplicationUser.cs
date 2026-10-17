using Microsoft.AspNetCore.Identity;

namespace NoteService.Infrastructure.Persistence;

public class ApplicationUser : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 100;

    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
