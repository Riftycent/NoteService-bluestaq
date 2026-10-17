using Microsoft.EntityFrameworkCore;

namespace NoteService.Infrastructure.Persistence;

public static class DbContextExtensions
{
    /// <summary>Runs <paramref name="work"/> in a transaction that is safe with SQL Server's retrying execution strategy.</summary>
    public static Task InTransactionAsync(this DbContext db, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await work(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
