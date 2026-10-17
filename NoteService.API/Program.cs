using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using NoteService.API.Common;
using NoteService.Infrastructure;
using NoteService.Infrastructure.Persistence;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

const long MaxRequestBytes = 16 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxRequestBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxRequestBytes);

builder.Services
    .AddValidatedOptions(builder.Configuration)
    .AddInfrastructure(builder.Configuration)
    .AddFeatureServices()
    .AddAppIdentity()
    .AddAppAuthentication()
    .AddAppControllers()
    .AddAppOpenApi()
    .AddAppRateLimiting(builder.Configuration)
    .AddAppCors(builder.Configuration)
    .AddAppTelemetry(builder.Configuration);

builder.Services.AddHealthChecks().AddDbContextCheck<NoteDbContext>("database", tags: ["ready"]);

if (builder.Configuration.GetValue("Maintenance:Enabled", true))
{
    builder.Services.AddMaintenanceWorker();
}

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = RequestLogEnricher.Enrich);
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference("/docs").AllowAnonymous();
}

app.Run();

/// <summary>Entry point marker so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
