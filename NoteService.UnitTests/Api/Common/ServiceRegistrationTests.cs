using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Notes;
using NoteService.API.Features.Teams;
using NoteService.Domain.Abstractions;
using NoteService.Infrastructure;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Maintenance;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;

namespace NoteService.UnitTests.Api.Common;

public class ServiceCollectionExtensionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AddFeatureServices_RegistersFeatureServicesAndCurrentUser()
    {
        var services = new ServiceCollection().AddFeatureServices();

        Assert.Contains(services, d => d.ServiceType == typeof(ICurrentUser) && d.ImplementationType == typeof(HttpCurrentUser));
        Assert.Contains(services, d => d.ServiceType == typeof(INotesService) && d.ImplementationType == typeof(NotesService));
        Assert.Contains(services, d => d.ServiceType == typeof(ITeamService));
        Assert.Contains(services, d => d.ServiceType == typeof(ITeamAccess));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthorizationHandler) && d.ImplementationType == typeof(TeamRoleHandler));
    }

    [Fact]
    public void AddValidatedOptions_ShortSigningKey_FailsValidationOnAccess()
    {
        var config = Config(new() { ["Jwt:SigningKey"] = "too-short" });
        using var provider = new ServiceCollection().AddValidatedOptions(config).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    [Fact]
    public void AddValidatedOptions_ValidConfiguration_BindsValues()
    {
        var config = Config(new()
        {
            ["Jwt:SigningKey"] = new string('k', 40),
            ["Jwt:AccessTokenMinutes"] = "30",
            ["Notes:VersionIntervalMinutes"] = "9",
            ["Search:UseFullText"] = "true",
            ["RateLimiting:AuthPermitPerMinute"] = "3"
        });
        using var provider = new ServiceCollection().AddValidatedOptions(config).BuildServiceProvider();

        Assert.Equal(30, provider.GetRequiredService<IOptions<JwtOptions>>().Value.AccessTokenMinutes);
        Assert.Equal(9, provider.GetRequiredService<IOptions<NotesOptions>>().Value.VersionIntervalMinutes);
        Assert.True(provider.GetRequiredService<IOptions<SearchOptions>>().Value.UseFullText);
        Assert.Equal(3, provider.GetRequiredService<IOptions<RateLimitOptions>>().Value.AuthPermitPerMinute);
    }

    [Fact]
    public void AddAppIdentity_RegistersUserManager()
    {
        var services = new ServiceCollection().AddAppIdentity();

        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>));
    }

    [Fact]
    public void PartitionKey_AuthenticatedUser_UsesUserId()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "abc")], "test"))
        };

        Assert.Equal("user:abc", ServiceCollectionExtensions.PartitionKey(context));
    }

    [Fact]
    public void PartitionKey_Anonymous_UsesRemoteAddress()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");

        Assert.Equal("ip:10.1.2.3", ServiceCollectionExtensions.PartitionKey(context));
    }

    [Fact]
    public void AddAppRateLimiting_UsesConfiguredLimitsWithoutThrowing()
    {
        var services = new ServiceCollection().AddLogging().AddAppRateLimiting(Config(new() { ["RateLimiting:PermitPerMinute"] = "5" }));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>>().Value.GlobalLimiter);
    }

    [Fact]
    public void AddAppCors_WithoutOrigins_BuildsPolicyAllowingNone()
    {
        var services = new ServiceCollection().AddLogging().AddAppCors(Config([]));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>().Value.GetPolicy("__DefaultCorsPolicy");
        Assert.Empty(policy!.Origins);
    }

    [Fact]
    public void AddAppCors_WithOrigins_AllowsThemAndExposesETag()
    {
        var services = new ServiceCollection().AddLogging().AddAppCors(Config(new() { ["Cors:AllowedOrigins:0"] = "https://app.test" }));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>().Value.GetPolicy("__DefaultCorsPolicy")!;
        Assert.Equal(["https://app.test"], policy.Origins);
        Assert.Contains("ETag", policy.ExposedHeaders);
    }

    [Fact]
    public async Task BearerSecurityTransformer_AddsBearerSchemeAndRequirement()
    {
        var document = new OpenApiDocument();

        await new BearerSecurityTransformer().TransformAsync(document, null!, default);

        var scheme = Assert.IsType<OpenApiSecurityScheme>(document.Components!.SecuritySchemes!["Bearer"]);
        Assert.Equal("bearer", scheme.Scheme);
        Assert.Equal(SecuritySchemeType.Http, scheme.Type);
        Assert.Single(document.Security!);
    }
}

public class InfrastructureRegistrationTests
{
    private static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NoteDb"] = "Server=.;Database=x;TrustServerCertificate=True",
            ["Maintenance:TrashRetentionDays"] = "12",
            ["BlobStorage:RootPath"] = "custom-blobs"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<ICurrentUser, FixedUser>();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class FixedUser : ICurrentUser
    {
        public Guid? UserId => null;
    }

    [Fact]
    public void AddInfrastructure_RegistersStorageEmailAndDbContext()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        Assert.IsType<LocalDiskBlobStore>(scope.ServiceProvider.GetRequiredService<IBlobStore>());
        Assert.IsType<LoggingEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.IsType<EmailOutbox>(scope.ServiceProvider.GetRequiredService<IEmailOutbox>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NoteDbContext>());
        Assert.Same(TimeProvider.System, scope.ServiceProvider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void AddInfrastructure_BindsOptions()
    {
        using var provider = Build();

        Assert.Equal(12, provider.GetRequiredService<IOptions<MaintenanceOptions>>().Value.TrashRetentionDays);
        Assert.Equal("custom-blobs", provider.GetRequiredService<IOptions<BlobStorageOptions>>().Value.RootPath);
    }

    [Fact]
    public void AddInfrastructure_RegistersAllMaintenanceJobs()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var jobs = scope.ServiceProvider.GetServices<IMaintenanceJob>().Select(j => j.Name).Order().ToList();

        Assert.Equal(
            [nameof(EmailOutboxJob), nameof(ExpiredTokenCleanupJob), nameof(OrphanBlobCleanupJob), nameof(TrashPurgeJob)],
            jobs);
    }

    [Fact]
    public void AddMaintenanceWorker_RegistersHostedService()
    {
        var services = new ServiceCollection().AddMaintenanceWorker();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void DesignTimeFactory_CreatesContextForSqlServer()
    {
        using var context = new DesignTimeNoteDbContextFactory().CreateDbContext([]);

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.NotEmpty(context.Model.GetEntityTypes());
    }
}
