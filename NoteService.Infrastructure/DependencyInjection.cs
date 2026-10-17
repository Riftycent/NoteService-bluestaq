using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Maintenance;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;

namespace NoteService.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "NoteDb";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<NoteDbContext>(options => options.UseSqlServer(
            configuration.GetConnectionString(ConnectionStringName),
            sql => sql.EnableRetryOnFailure()));

        services.AddOptions<BlobStorageOptions>().Bind(configuration.GetSection(BlobStorageOptions.SectionName));
        services.AddOptions<MaintenanceOptions>().Bind(configuration.GetSection(MaintenanceOptions.SectionName));

        services.TryAddSingleton<IBlobStore, LocalDiskBlobStore>();
        services.TryAddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddScoped<IEmailOutbox, EmailOutbox>();

        services.AddScoped<IMaintenanceJob, TrashPurgeJob>();
        services.AddScoped<IMaintenanceJob, ExpiredTokenCleanupJob>();
        services.AddScoped<IMaintenanceJob, EmailOutboxJob>();
        services.AddScoped<IMaintenanceJob, OrphanBlobCleanupJob>();

        return services;
    }

    /// <summary>Registers the hosted worker; kept separate so tests can opt out of background processing.</summary>
    public static IServiceCollection AddMaintenanceWorker(this IServiceCollection services) =>
        services.AddHostedService<MaintenanceWorker>();
}
