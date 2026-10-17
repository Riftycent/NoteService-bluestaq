using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoteService.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace NoteService.IntegrationTests.TestSupport;

/// <summary>
/// Hosts the real API against a real SQL Server. By default a throw-away SQL Server container is started with
/// Testcontainers (Docker required). Set <c>NOTESERVICE_TEST_SQL</c> to a SQL Server connection string (for example
/// LocalDB) to use that server instead; a uniquely named database is created and dropped for the run.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ExternalSqlVariable = "NOTESERVICE_TEST_SQL";

    private MsSqlContainer? _container;
    private string? _externalServer;
    private string _connectionString = string.Empty;
    private readonly string _blobRoot = Path.Combine(Path.GetTempPath(), "noteservice-it-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _externalServer = Environment.GetEnvironmentVariable(ExternalSqlVariable);
        if (!string.IsNullOrWhiteSpace(_externalServer))
        {
            var builder = new SqlConnectionStringBuilder(_externalServer) { InitialCatalog = "NoteServiceTests_" + Guid.NewGuid().ToString("N") };
            _connectionString = builder.ConnectionString;
        }
        else
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }

        // Applying the real migrations here also proves they work against SQL Server.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NoteDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        else if (!string.IsNullOrWhiteSpace(_externalServer))
        {
            SqlConnection.ClearAllPools();
            var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
            var database = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];";
            await command.ExecuteNonQueryAsync();
        }

        await base.DisposeAsync().AsTask();

        if (Directory.Exists(_blobRoot))
        {
            Directory.Delete(_blobRoot, recursive: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting feeds host configuration, which Program.cs reads while it is still building services.
        builder.UseSetting("ConnectionStrings:NoteDb", _connectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789-0123456789");
        builder.UseSetting("App:RequireConfirmedEmail", "false");
        builder.UseSetting("Maintenance:Enabled", "false");
        builder.UseSetting("RateLimiting:PermitPerMinute", "100000");
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "100000");
        builder.UseSetting("BlobStorage:RootPath", _blobRoot);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
