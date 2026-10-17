using NoteService.Domain.Entities;

namespace NoteService.Domain.Rules;

public static class VersionThrottle
{
    /// <summary>
    /// A history snapshot is taken when there is none yet, when a different person edits,
    /// or when the previous snapshot is at least <paramref name="interval"/> old.
    /// </summary>
    public static bool ShouldSnapshot(NoteVersion? last, Guid editor, DateTimeOffset now, TimeSpan interval) =>
        last is null || last.EditedBy != editor || now - last.CreatedAt >= interval;
}
