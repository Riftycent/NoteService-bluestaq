using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoteService.API.Features.Auth;
using NoteService.API.Features.Notes;
using NoteService.API.Features.Teams;
using NoteService.Domain.Enums;

namespace NoteService.IntegrationTests.TestSupport;

/// <summary>A signed-in test user with convenience helpers for calling the API.</summary>
public sealed class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private ApiClient(HttpClient http, AuthResponse auth, string password)
    {
        Http = http;
        Auth = auth;
        Password = password;
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
    }

    public HttpClient Http { get; }

    public AuthResponse Auth { get; private set; }

    public string Password { get; }

    public Guid UserId => Auth.User.Id;

    public string Email => Auth.User.Email;

    public static async Task<ApiClient> RegisterAsync(ApiFactory factory, string? name = null)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var email = $"{name ?? "user"}-{id}@example.com".ToLowerInvariant();
        const string password = "Sup3r-secret-pw!";
        var anonymous = factory.CreateClient();

        var register = await anonymous.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest { Email = email, Password = password, DisplayName = name ?? "Test User" }, Json);
        register.EnsureSuccessStatusCode();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Email = email, Password = password }, Json);
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>(Json))!;

        return new ApiClient(factory.CreateClient(), auth, password);
    }

    public Task<HttpResponseMessage> GetAsync(string url) => Http.GetAsync(url);

    public Task<HttpResponseMessage> PostAsync<T>(string url, T body) => Http.PostAsJsonAsync(url, body, Json);

    public Task<HttpResponseMessage> PutAsync<T>(string url, T body) => Http.PutAsJsonAsync(url, body, Json);

    public Task<HttpResponseMessage> DeleteAsync(string url, string? ifMatch = null) =>
        Send(HttpMethod.Delete, url, null, ifMatch);

    public Task<HttpResponseMessage> PatchAsync<T>(string url, T body, string? ifMatch = null) =>
        Send(HttpMethod.Patch, url, JsonContent.Create(body, options: Json), ifMatch);

    public Task<HttpResponseMessage> Send(HttpMethod method, string url, HttpContent? content, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return Http.SendAsync(request);
    }

    public async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"Expected success but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        var body = response.IsSuccessStatusCode ? string.Empty : await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"Expected {(int)expected} but got {(int)response.StatusCode}. {body}");
    }

    // ---- Domain helpers

    public async Task<TeamDto> CreateTeamAsync(string name = "Team") =>
        await ReadAsync<TeamDto>(await PostAsync("/api/v1/teams", new CreateTeamRequest { Name = name }));

    public async Task<NoteDto> CreateNoteAsync(Guid teamId, string title = "Note", string content = "Content", Guid? folderId = null) =>
        await ReadAsync<NoteDto>(await PostAsync($"/api/v1/teams/{teamId}/notes", new CreateNoteRequest { Title = title, Content = content, FolderId = folderId }));

    /// <summary>Invites <paramref name="invitee"/> with the given role and has them accept, returning once they are a member.</summary>
    public async Task AddMemberAsync(Guid teamId, ApiClient invitee, TeamRole role, ApiFactory factory)
    {
        var invite = await PostAsync($"/api/v1/teams/{teamId}/invites", new CreateInviteRequest { Email = invitee.Email, Role = role });
        await AssertStatusAsync(HttpStatusCode.Created, invite);

        // The invite token only exists in the (outbox) email, so read it from the stored message.
        var token = await InviteTokens.ReadLatestAsync(factory, invitee.Email);
        var accept = await invitee.PostAsync($"/api/v1/invites/{token}/accept", new { });
        await AssertStatusAsync(HttpStatusCode.OK, accept);
    }
}
