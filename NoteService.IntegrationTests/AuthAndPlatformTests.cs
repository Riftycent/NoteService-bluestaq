using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NoteService.API.Features.Auth;
using NoteService.IntegrationTests.TestSupport;

namespace NoteService.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthAndPlatformTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_Login_And_Me_RoundTrip()
    {
        var user = await ApiClient.RegisterAsync(factory, "ann");

        var me = await user.ReadAsync<UserDto>(await user.GetAsync("/api/v1/auth/me"));

        Assert.Equal(user.Email, me.Email);
        Assert.Equal(user.UserId, me.Id);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsValidationProblem()
    {
        var user = await ApiClient.RegisterAsync(factory, "dup");
        var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest { Email = user.Email, Password = "Another-pw-123!", DisplayName = "Dup" }, ApiClient.Json);

        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Register_WeakPassword_IsRejected()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest { Email = $"weak-{Guid.NewGuid():N}@example.com", Password = "short", DisplayName = "Weak" }, ApiClient.Json);

        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var user = await ApiClient.RegisterAsync(factory, "pw");

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest { Email = user.Email, Password = "Wrong-password-1!" }, ApiClient.Json);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Unauthorized, response);
    }

    [Fact]
    public async Task Login_RepeatedFailures_LockTheAccount()
    {
        var user = await ApiClient.RegisterAsync(factory, "lock");
        var anonymous = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Email = user.Email, Password = "Wrong-password-1!" }, ApiClient.Json);
        }

        var correct = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Email = user.Email, Password = user.Password }, ApiClient.Json);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Unauthorized, correct);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/teams");

        await ApiClient.AssertStatusAsync(HttpStatusCode.Unauthorized, response);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithGarbageToken_Returns401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not.a.jwt");

        await ApiClient.AssertStatusAsync(HttpStatusCode.Unauthorized, await client.GetAsync("/api/v1/teams"));
    }

    [Fact]
    public async Task Refresh_RotatesTokenAndDetectsReuse()
    {
        var user = await ApiClient.RegisterAsync(factory, "refresh");
        var anonymous = factory.CreateClient();

        var first = await user.ReadAsync<AuthResponse>(
            await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest { RefreshToken = user.Auth.RefreshToken }, ApiClient.Json));
        Assert.NotEqual(user.Auth.RefreshToken, first.RefreshToken);

        // Presenting the already-rotated token again is treated as theft and kills every session of the user.
        var reuse = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest { RefreshToken = user.Auth.RefreshToken }, ApiClient.Json);
        await ApiClient.AssertStatusAsync(HttpStatusCode.Unauthorized, reuse);

        var stolenSessionToo = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest { RefreshToken = first.RefreshToken }, ApiClient.Json);
        await ApiClient.AssertStatusAsync(HttpStatusCode.Unauthorized, stolenSessionToo);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var user = await ApiClient.RegisterAsync(factory, "logout");
        var anonymous = factory.CreateClient();

        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent,
            await anonymous.PostAsJsonAsync("/api/v1/auth/logout", new RefreshRequest { RefreshToken = user.Auth.RefreshToken }, ApiClient.Json));

        await ApiClient.AssertStatusAsync(HttpStatusCode.Unauthorized,
            await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest { RefreshToken = user.Auth.RefreshToken }, ApiClient.Json));
    }

    [Fact]
    public async Task ForgotPassword_AlwaysAccepted_EvenForUnknownEmail()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest { Email = "nobody-" + Guid.NewGuid().ToString("N") + "@example.com" }, ApiClient.Json);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Accepted, response);
    }

    [Fact]
    public async Task Health_LiveAndReady_AreAnonymousAndHealthy()
    {
        var client = factory.CreateClient();

        await ApiClient.AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync("/health/live"));
        await ApiClient.AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync("/health/ready"));
    }

    [Fact]
    public async Task Responses_CarryCorrelationIdAndSecurityHeaders()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "it-test-123");

        var response = await client.GetAsync("/health/live");

        Assert.Equal("it-test-123", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task OpenApiDocument_IsServedAndDescribesTheApi()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        await ApiClient.AssertStatusAsync(HttpStatusCode.OK, response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/teams", out _), "teams path missing");
        Assert.True(paths.TryGetProperty("/api/v1/teams/{teamId}/notes/{noteId}", out _), "note path missing");
        Assert.True(document.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var user = await ApiClient.RegisterAsync(factory, "route");

        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await user.GetAsync("/api/v1/nothing-here"));
    }
}
