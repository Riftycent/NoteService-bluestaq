using Microsoft.Extensions.Logging;
using NoteService.Domain.Entities;
using NoteService.Infrastructure.Persistence;

namespace NoteService.Infrastructure.Email;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Development sender: writes the message to the log instead of delivering it. Replace with SMTP/SendGrid/etc. for production.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation("Email to {To}: {Subject}", to, subject);
        logger.LogDebug("Email body for {To}:\n{Body}", to, body);
        return Task.CompletedTask;
    }
}

public interface IEmailOutbox
{
    Task EnqueueAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Persists outgoing mail so a failing mail server never fails a request; a background job delivers it.</summary>
public sealed class EmailOutbox(NoteDbContext db, TimeProvider time) : IEmailOutbox
{
    public async Task EnqueueAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        db.OutboxEmails.Add(OutboxEmail.Create(to, subject, body, time.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }
}
