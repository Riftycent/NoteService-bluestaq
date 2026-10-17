using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using OpenTelemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using NoteService.API.Authorization;
using NoteService.API.Features.Attachments;
using NoteService.API.Features.Auth;
using NoteService.API.Features.Folders;
using NoteService.API.Features.Notes;
using NoteService.API.Features.Search;
using NoteService.API.Features.Shares;
using NoteService.API.Features.Tags;
using NoteService.API.Features.Teams;
using NoteService.API.Features.Versions;
using NoteService.Domain.Abstractions;
using NoteService.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NoteService.API.Common;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFeatureServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddScoped<ITeamAccess, TeamAccess>();
        services.AddScoped<INoteAccess, NoteAccessService>();
        services.AddScoped<IAuthorizationHandler, TeamRoleHandler>();

        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<IMemberService, MemberService>();
        services.AddScoped<IInviteService, InviteService>();
        services.AddScoped<IFolderService, FolderService>();
        services.AddScoped<INoteHistory, NoteHistory>();
        services.AddScoped<INotesService, NotesService>();
        services.AddScoped<IVersionService, VersionService>();
        services.AddScoped<IShareService, ShareService>();
        services.AddScoped<ITagService, TagService>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<ISearchService, SearchService>();
        return services;
    }

    public static IServiceCollection AddValidatedOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AppOptions>().Bind(configuration.GetSection(AppOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<NotesOptions>().Bind(configuration.GetSection(NotesOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AttachmentOptions>().Bind(configuration.GetSection(AttachmentOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SearchOptions>().Bind(configuration.GetSection(SearchOptions.SectionName));
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.SectionName));
        return services;
    }

    public static IServiceCollection AddAppIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<NoteDbContext>()
            .AddDefaultTokenProviders();
        return services;
    }

    public static IServiceCollection AddAppAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured through IOptions so the signing key is read after configuration overrides (including tests) are applied.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                var settings = jwt.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name"
                };
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }

    public static IServiceCollection AddAppControllers(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.AssumeDefaultVersionWhenUnspecified = false;
                o.ApiVersionReader = new UrlSegmentApiVersionReader();
                o.ReportApiVersions = true;
            })
            .AddMvc()
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'VVV";
                o.SubstituteApiVersionInUrl = true;
            });

        services.Configure<ForwardedHeadersOptions>(o =>
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        return services;
    }

    public static IServiceCollection AddAppOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", o => o.AddDocumentTransformer<BearerSecurityTransformer>());
        return services;
    }

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Broad per-user limit (falls back to the client address for anonymous callers).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.PermitPerMinute, Window = TimeSpan.FromMinutes(1) }));

            // Strict per-address limit on credential endpoints.
            options.AddPolicy(RateLimitPolicies.Auth, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    "ip:" + context.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AuthPermitPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }

    internal static string PartitionKey(HttpContext context) =>
        context.User.FindFirst("sub")?.Value is { } user ? "user:" + user : "ip:" + context.Connection.RemoteIpAddress;

    public static IServiceCollection AddAppTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("NoteService"))
            .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSqlClientInstrumentation())
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        return services;
    }

    public static IServiceCollection AddAppCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(o => o.AddDefaultPolicy(policy =>
        {
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()
                    .WithExposedHeaders("ETag", CorrelationIdMiddleware.HeaderName);
            }
        }));
        return services;
    }
}

/// <summary>Documents the bearer-token scheme so API explorers offer an Authorize button.</summary>
internal sealed class BearerSecurityTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT"
        };

        document.Security =
        [
            new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }
        ];
        return Task.CompletedTask;
    }
}
