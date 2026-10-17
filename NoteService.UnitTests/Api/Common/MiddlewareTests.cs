using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NoteService.API.Common;
using NoteService.Domain.Exceptions;
using NoteService.UnitTests.TestSupport;
using Serilog;

namespace NoteService.UnitTests.Api.Common;

public class CorrelationIdMiddlewareTests
{
    private static async Task<DefaultHttpContext> RunAsync(string? incomingHeader)
    {
        var context = CapturingResponseFeature.CreateContext(out var feature);
        if (incomingHeader is not null)
        {
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = incomingHeader;
        }

        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);
        await feature.FireOnStartingAsync();
        return context;
    }

    [Fact]
    public async Task InvokeAsync_ValidIncomingId_IsReusedAndEchoed()
    {
        var context = await RunAsync("req-123_ABC");

        Assert.Equal("req-123_ABC", context.TraceIdentifier);
        Assert.Equal("req-123_ABC", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task InvokeAsync_NoIncomingId_GeneratesOne()
    {
        var context = await RunAsync(null);

        Assert.False(string.IsNullOrWhiteSpace(context.TraceIdentifier));
        Assert.Equal(context.TraceIdentifier, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task InvokeAsync_UnsafeIncomingId_IsReplaced()
    {
        var context = await RunAsync("bad value\r\ninjected");

        Assert.NotEqual("bad value\r\ninjected", context.TraceIdentifier);
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        var called = false;
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(new DefaultHttpContext());

        Assert.True(called);
    }

    [Theory]
    [InlineData("abc-123_X", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    public void IsValid_ChecksCharactersAndPresence(string? value, bool expected) =>
        Assert.Equal(expected, CorrelationIdMiddleware.IsValid(value));

    [Fact]
    public void IsValid_TooLong_ReturnsFalse() => Assert.False(CorrelationIdMiddleware.IsValid(new string('a', 65)));
}

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AddsSecurityHeaders()
    {
        var context = CapturingResponseFeature.CreateContext(out var feature);
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
        await feature.FireOnStartingAsync();

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
    }
}

public class RequestLogEnricherTests
{
    [Fact]
    public void Enrich_AuthenticatedRequestWithTeam_SetsUserAndTeam()
    {
        var diagnostics = Substitute.For<IDiagnosticContext>();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"))
        };
        context.Request.RouteValues["teamId"] = "team-1";

        RequestLogEnricher.Enrich(diagnostics, context);

        diagnostics.Received(1).Set("UserId", "user-1", false);
        diagnostics.Received(1).Set("TeamId", "team-1", false);
    }

    [Fact]
    public void Enrich_AnonymousRequestWithoutTeam_SetsNothing()
    {
        var diagnostics = Substitute.For<IDiagnosticContext>();

        RequestLogEnricher.Enrich(diagnostics, new DefaultHttpContext());

        diagnostics.DidNotReceiveWithAnyArgs().Set(default!, default, default);
    }
}

public class GlobalExceptionHandlerTests
{
    private static async Task<(bool Handled, ProblemDetailsContext? Written, DefaultHttpContext Http)> HandleAsync(Exception exception)
    {
        ProblemDetailsContext? written = null;
        var service = Substitute.For<IProblemDetailsService>();
        service.TryWriteAsync(Arg.Do<ProblemDetailsContext>(c => written = c)).Returns(true);
        var http = new DefaultHttpContext();

        var handled = await new GlobalExceptionHandler(service).TryHandleAsync(http, exception, CancellationToken.None);
        return (handled, written, http);
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task TryHandleAsync_KnownException_MapsToStatus(Exception exception, int status)
    {
        var (handled, written, http) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal(status, written!.ProblemDetails.Status);
    }

    public static TheoryData<Exception, int> Mappings() => new()
    {
        { new NotFoundException(), 404 },
        { new ForbiddenException(), 403 },
        { new ConflictException("c"), 409 },
        { new UnauthorizedAppException(), 401 },
        { new PreconditionRequiredException(), 428 },
        { new PreconditionFailedException(), 412 },
        { new ValidationFailedException("f", "m"), 400 }
    };

    [Fact]
    public async Task TryHandleAsync_ValidationFailure_ReturnsFieldErrors()
    {
        var (_, written, _) = await HandleAsync(new ValidationFailedException("title", "required"));

        var details = Assert.IsType<ValidationProblemDetails>(written!.ProblemDetails);
        Assert.Equal(["required"], details.Errors["title"]);
    }

    [Fact]
    public async Task TryHandleAsync_PreconditionFailedWithETag_ExposesItAsProblemExtension()
    {
        var (_, written, _) = await HandleAsync(new PreconditionFailedException("changed", "\"abc\""));

        Assert.Equal("\"abc\"", written!.ProblemDetails.Extensions["currentETag"]);
    }

    [Fact]
    public async Task TryHandleAsync_BadHttpRequest_UsesItsStatusCode()
    {
        var (handled, _, http) = await HandleAsync(new BadHttpRequestException("too big", StatusCodes.Status413PayloadTooLarge));

        Assert.True(handled);
        Assert.Equal(413, http.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_UnknownException_IsNotHandled()
    {
        var (handled, written, _) = await HandleAsync(new InvalidOperationException("boom"));

        Assert.False(handled);
        Assert.Null(written);
    }
}
