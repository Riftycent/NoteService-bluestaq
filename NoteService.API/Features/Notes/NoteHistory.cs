using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoteService.API.Common;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Rules;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Notes;

public interface INoteHistory
{
    /// <summary>
    /// Stages a history snapshot of the note's current state (the caller saves). Unless <paramref name="force"/> is set,
    /// snapshots are throttled per editor so rapid saves do not flood the history.
    /// </summary>
    Task<bool> SnapshotAsync(Note note, bool force, CancellationToken ct);
}

public sealed class NoteHistory(NoteDbContext db, ICurrentUser currentUser, TimeProvider time, IOptions<NotesOptions> options) : INoteHistory
{
    public async Task<bool> SnapshotAsync(Note note, bool force, CancellationToken ct)
    {
        var editor = currentUser.RequireId();
        var now = time.GetUtcNow();

        var last = await db.NoteVersions
            .Where(v => v.NoteId == note.Id)
            .OrderByDescending(v => v.VersionNo)
            .FirstOrDefaultAsync(ct);

        var interval = TimeSpan.FromMinutes(options.Value.VersionIntervalMinutes);
        if (!force && !VersionThrottle.ShouldSnapshot(last, editor, now, interval))
        {
            return false;
        }

        db.NoteVersions.Add(NoteVersion.Snapshot(note, (last?.VersionNo ?? 0) + 1, editor, now));
        return true;
    }
}
