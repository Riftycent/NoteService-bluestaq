using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoteService.Domain.Entities;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;

namespace NoteService.Infrastructure.Maintenance;

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    public int IntervalSeconds { get; set; } = 60;
    public int TrashRetentionDays { get; set; } = 30;
    public int PurgeBatchSize { get; set; } = 100;
    public int OrphanBlobGraceHours { get; set; } = 24;
    public int EmailBatchSize { get; set; } = 20;
    public int RevokedTokenRetentionDays { get; set; } = 7;
}

public interface IMaintenanceJob
{
    string Name { get; }

    Task RunAsync(CancellationToken ct);
}

/// <summary>Permanently deletes notes that have been in the trash longer than the retention period.</summary>
public sealed class TrashPurgeJob(
    NoteDbContext db,
    IBlobStore blobs,
    TimeProvider time,
    IOptions<MaintenanceOptions> options,
    ILogger<TrashPurgeJob> logger) : IMaintenanceJob
{
    public string Name => nameof(TrashPurgeJob);

    public async Task RunAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(settings.TrashRetentionDays);

        var noteIds = await db.Notes.IgnoreQueryFilters()
            .Where(n => n.DeletedAt != null && n.DeletedAt < cutoff)
            .OrderBy(n => n.DeletedAt)
            .Select(n => n.Id)
            .Take(settings.PurgeBatchSize)
            .ToListAsync(ct);

        if (noteIds.Count == 0)
        {
            return;
        }

        var storageKeys = await db.Attachments.Where(a => noteIds.Contains(a.NoteId)).Select(a => a.StorageKey).ToListAsync(ct);

        await db.InTransactionAsync(async token =>
        {
            await db.NoteTags.Where(nt => noteIds.Contains(nt.NoteId)).ExecuteDeleteAsync(token);
            await db.NoteShares.Where(s => noteIds.Contains(s.NoteId)).ExecuteDeleteAsync(token);
            await db.NoteVersions.Where(v => noteIds.Contains(v.NoteId)).ExecuteDeleteAsync(token);
            await db.Attachments.Where(a => noteIds.Contains(a.NoteId)).ExecuteDeleteAsync(token);
            await db.Notes.IgnoreQueryFilters().Where(n => noteIds.Contains(n.Id)).ExecuteDeleteAsync(token);
        }, ct);

        foreach (var key in storageKeys)
        {
            try
            {
                await blobs.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                // The orphan-blob job will retry later.
                logger.LogWarning(ex, "Failed to delete blob {Key} for purged note", key);
            }
        }

        logger.LogInformation("Purged {Count} trashed notes", noteIds.Count);
    }
}

/// <summary>Removes expired invites and refresh tokens that are expired or long revoked.</summary>
public sealed class ExpiredTokenCleanupJob(NoteDbContext db, TimeProvider time, IOptions<MaintenanceOptions> options) : IMaintenanceJob
{
    public string Name => nameof(ExpiredTokenCleanupJob);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var revokedCutoff = now - TimeSpan.FromDays(options.Value.RevokedTokenRetentionDays);

        await db.RefreshTokens
            .Where(t => t.ExpiresAt < now || (t.RevokedAt != null && t.RevokedAt < revokedCutoff))
            .ExecuteDeleteAsync(ct);

        await db.TeamInvites.Where(i => i.ExpiresAt < now).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Delivers queued emails, retrying failures up to <see cref="OutboxEmail.MaxAttempts"/> times.</summary>
public sealed class EmailOutboxJob(
    NoteDbContext db,
    IEmailSender sender,
    TimeProvider time,
    IOptions<MaintenanceOptions> options,
    ILogger<EmailOutboxJob> logger) : IMaintenanceJob
{
    public string Name => nameof(EmailOutboxJob);

    public async Task RunAsync(CancellationToken ct)
    {
        var pending = await db.OutboxEmails
            .Where(e => e.SentAt == null && e.Attempts < OutboxEmail.MaxAttempts)
            .OrderBy(e => e.CreatedAt)
            .Take(options.Value.EmailBatchSize)
            .ToListAsync(ct);

        foreach (var email in pending)
        {
            try
            {
                await sender.SendAsync(email.To, email.Subject, email.Body, ct);
                email.MarkSent(time.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to send email {EmailId}", email.Id);
                email.MarkFailed(ex.Message);
            }
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>Deletes stored files that no attachment row references (for example after a failed upload or purge).</summary>
public sealed class OrphanBlobCleanupJob(
    NoteDbContext db,
    IBlobStore blobs,
    TimeProvider time,
    IOptions<MaintenanceOptions> options,
    ILogger<OrphanBlobCleanupJob> logger) : IMaintenanceJob
{
    public string Name => nameof(OrphanBlobCleanupJob);

    public async Task RunAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromHours(options.Value.OrphanBlobGraceHours);
        var known = (await db.Attachments.Select(a => a.StorageKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        await foreach (var blob in blobs.ListAsync(ct))
        {
            if (blob.LastModified < cutoff && !known.Contains(blob.Key))
            {
                await blobs.DeleteAsync(blob.Key, ct);
                logger.LogInformation("Deleted orphaned blob {Key}", blob.Key);
            }
        }
    }
}
