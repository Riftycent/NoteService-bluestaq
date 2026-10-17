using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoteService.Infrastructure.Maintenance;

/// <summary>Runs every <see cref="IMaintenanceJob"/> on a fixed interval, each in its own scope.</summary>
public sealed class MaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MaintenanceOptions> options,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds)));
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    /// <summary>Runs all jobs once. A failing job is logged and never prevents the others from running.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        foreach (var job in scope.ServiceProvider.GetServices<IMaintenanceJob>())
        {
            try
            {
                await job.RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Maintenance job {Job} failed", job.Name);
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
