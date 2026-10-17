using System.Diagnostics;
using Serilog;
using Serilog.Context;

namespace NoteService.API.Common;

/// <summary>Accepts or creates an X-Correlation-ID, echoes it, and attaches it to logs and the current trace.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].ToString();
        if (!IsValid(correlationId))
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        Activity.Current?.SetTag("correlation_id", correlationId);

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    internal static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        });

        return next(context);
    }
}

public static class RequestLogEnricher
{
    /// <summary>Adds the user and team (never note content) to the request-completion log event.</summary>
    public static void Enrich(IDiagnosticContext diagnosticContext, HttpContext httpContext)
    {
        var user = httpContext.User?.FindFirst("sub")?.Value;
        if (user is not null)
        {
            diagnosticContext.Set("UserId", user);
        }

        if (httpContext.Request.RouteValues.TryGetValue("teamId", out var team) && team is not null)
        {
            diagnosticContext.Set("TeamId", team.ToString());
        }
    }
}
