using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoteService.Infrastructure.Persistence;

namespace NoteService.IntegrationTests.TestSupport;

/// <summary>Invite tokens are only ever emailed, so tests read them back from the email outbox.</summary>
public static class InviteTokens
{
    public static async Task<string> ReadLatestAsync(ApiFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NoteDbContext>();
        var mail = await db.OutboxEmails
            .Where(e => e.To == email && e.Body.Contains("/invites/"))
            .OrderByDescending(e => e.CreatedAt)
            .FirstAsync();

        return mail.Body[(mail.Body.LastIndexOf("/invites/", StringComparison.Ordinal) + "/invites/".Length)..].Trim();
    }
}
