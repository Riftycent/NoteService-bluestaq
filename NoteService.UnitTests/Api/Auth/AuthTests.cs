using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NoteService.API.Common;
using NoteService.API.Features.Auth;
using NoteService.Domain.Entities;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Auth;

public class JwtTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        Issuer = "issuer",
        Audience = "audience",
        SigningKey = "unit-test-signing-key-0123456789-0123456789",
        AccessTokenMinutes = 15
    };

    private readonly TestDb _db = new();

    private JwtTokenService Create() => new(Microsoft.Extensions.Options.Options.Create(Options), _db.Time);

    [Fact]
    public async Task CreateAccessToken_ProducesValidTokenWithUserClaims()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "ann@example.com", DisplayName = "Ann" };

        var token = Create().CreateAccessToken(user);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Options.SigningKey)),
            LifetimeValidator = (_, _, _, _) => true
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(user.Id.ToString(), result.ClaimsIdentity.FindFirst("sub")!.Value);
        Assert.Equal("ann@example.com", result.ClaimsIdentity.FindFirst("email")!.Value);
        Assert.Equal("Ann", result.ClaimsIdentity.FindFirst("name")!.Value);
    }

    [Fact]
    public void CreateAccessToken_ExpiresAfterConfiguredMinutes()
    {
        var token = Create().CreateAccessToken(new ApplicationUser { Id = Guid.NewGuid(), Email = "a@b.test", DisplayName = "A" });

        Assert.Equal(_db.Time.GetUtcNow().AddMinutes(15), token.ExpiresAt);
    }

    [Fact]
    public void CreateRefreshToken_ReturnsRawTokenAndMatchingHash()
    {
        var service = Create();

        var issued = service.CreateRefreshToken();

        Assert.NotEqual(issued.Raw, issued.Hash);
        Assert.Equal(issued.Hash, service.HashToken(issued.Raw));
        Assert.NotEqual(issued.Raw, service.CreateRefreshToken().Raw);
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        var service = Create();
        Assert.Equal(service.HashToken("abc"), service.HashToken("abc"));
    }
}

public class AuthServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly UserManager<ApplicationUser> _users;
    private readonly IEmailOutbox _outbox = Substitute.For<IEmailOutbox>();
    private readonly AppOptions _app = new() { RequireConfirmedEmail = true, PublicBaseUrl = "https://app.test" };
    private readonly JwtOptions _jwt = new() { SigningKey = "unit-test-signing-key-0123456789-0123456789", RefreshTokenDays = 14 };

    public AuthServiceTests() =>
        _users = Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);

    public void Dispose() => _db.Dispose();

    private (AuthService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        var service = new AuthService(
            _users, context, new JwtTokenService(Options.Create(_jwt), _db.Time), _outbox, _db.Time, _db.CurrentUser,
            Options.Create(_jwt), Options.Create(_app));
        return (service, context);
    }

    private ApplicationUser SeededUser(string email = "ann@example.com", bool confirmed = true)
    {
        var user = _db.AddUser(email, "Ann");
        user.EmailConfirmed = confirmed;
        return user;
    }

    private static string Encode(string raw) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));

    // ---- Register

    [Fact]
    public async Task RegisterAsync_ConfirmationRequired_CreatesUnconfirmedUserAndQueuesEmail()
    {
        ApplicationUser? created = null;
        _users.CreateAsync(Arg.Do<ApplicationUser>(u => created = u), "Password123!").Returns(IdentityResult.Success);
        _users.GenerateEmailConfirmationTokenAsync(Arg.Any<ApplicationUser>()).Returns("raw-token");
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.RegisterAsync(new RegisterRequest { Email = " Ann@Example.com ", Password = "Password123!", DisplayName = " Ann " }, default);

        Assert.Equal("Ann@Example.com", dto.Email);
        Assert.Equal("Ann", dto.DisplayName);
        Assert.False(created!.EmailConfirmed);
        await _outbox.Received(1).EnqueueAsync(
            "Ann@Example.com", Arg.Any<string>(),
            Arg.Is<string>(b => b.Contains("https://app.test/confirm-email?userId=" + created.Id) && b.Contains(Uri.EscapeDataString(Encode("raw-token")))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_ConfirmationNotRequired_MarksConfirmedAndSendsNothing()
    {
        _app.RequireConfirmedEmail = false;
        ApplicationUser? created = null;
        _users.CreateAsync(Arg.Do<ApplicationUser>(u => created = u), Arg.Any<string>()).Returns(IdentityResult.Success);
        var (service, context) = Create();
        await using var _ = context;

        await service.RegisterAsync(new RegisterRequest { Email = "a@b.test", Password = "Password123!", DisplayName = "A" }, default);

        Assert.True(created!.EmailConfirmed);
        await _outbox.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("DuplicateEmail", "email")]
    [InlineData("DuplicateUserName", "email")]
    [InlineData("PasswordTooShort", "password")]
    [InlineData("Other", "request")]
    public async Task RegisterAsync_IdentityFailure_ThrowsValidationErrorOnMatchingField(string code, string field)
    {
        _users.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = code, Description = "bad" }));
        var (service, context) = Create();
        await using var _ = context;

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.RegisterAsync(new RegisterRequest { Email = "a@b.test", Password = "x", DisplayName = "A" }, default));

        Assert.Contains(field, ex.Errors.Keys);
    }

    // ---- Login

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsTokensAndStoresRefreshTokenHash()
    {
        var user = SeededUser();
        _users.FindByEmailAsync("ann@example.com").Returns(user);
        _users.IsLockedOutAsync(user).Returns(false);
        _users.CheckPasswordAsync(user, "pw").Returns(true);
        var (service, context) = Create();
        await using var _ = context;

        var response = await service.LoginAsync(new LoginRequest { Email = "ann@example.com", Password = "pw" }, default);

        Assert.False(string.IsNullOrEmpty(response.AccessToken));
        Assert.Equal(user.Id, response.User.Id);
        await using var verify = _db.NewContext();
        var stored = await verify.RefreshTokens.SingleAsync();
        Assert.Equal(TokenHasher.Hash(response.RefreshToken), stored.TokenHash);
        Assert.Equal(_db.Time.GetUtcNow().AddDays(14), stored.ExpiresAt);
        await _users.Received(1).ResetAccessFailedCountAsync(user);
    }

    [Fact]
    public async Task LoginAsync_UnknownEmail_ThrowsUnauthorized()
    {
        _users.FindByEmailAsync(Arg.Any<string>()).Returns((ApplicationUser?)null);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.LoginAsync(new LoginRequest { Email = "x@y.test", Password = "pw" }, default));
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_RecordsFailureAndThrows()
    {
        var user = SeededUser();
        _users.FindByEmailAsync(Arg.Any<string>()).Returns(user);
        _users.CheckPasswordAsync(user, Arg.Any<string>()).Returns(false);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.LoginAsync(new LoginRequest { Email = "ann@example.com", Password = "bad" }, default));

        await _users.Received(1).AccessFailedAsync(user);
    }

    [Fact]
    public async Task LoginAsync_LockedOutUser_ThrowsWithoutCheckingPassword()
    {
        var user = SeededUser();
        _users.FindByEmailAsync(Arg.Any<string>()).Returns(user);
        _users.IsLockedOutAsync(user).Returns(true);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.LoginAsync(new LoginRequest { Email = "ann@example.com", Password = "pw" }, default));

        await _users.DidNotReceiveWithAnyArgs().CheckPasswordAsync(default!, default!);
    }

    [Fact]
    public async Task LoginAsync_UnconfirmedEmail_ThrowsWhenConfirmationRequired()
    {
        var user = SeededUser(confirmed: false);
        _users.FindByEmailAsync(Arg.Any<string>()).Returns(user);
        _users.CheckPasswordAsync(user, Arg.Any<string>()).Returns(true);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.LoginAsync(new LoginRequest { Email = "ann@example.com", Password = "pw" }, default));
    }

    // ---- Refresh / logout

    private async Task<(string Raw, ApplicationUser User)> SeedRefreshTokenAsync(TimeSpan? lifetime = null, bool revoked = false)
    {
        var user = _db.AddUser();
        var raw = TokenHasher.Generate();
        var token = RefreshToken.Create(user.Id, TokenHasher.Hash(raw), _db.Time.GetUtcNow(), lifetime ?? TimeSpan.FromDays(1));
        if (revoked)
        {
            token.Revoke(_db.Time.GetUtcNow());
        }

        await using var seed = _db.NewContext();
        seed.RefreshTokens.Add(token);
        await seed.SaveChangesAsync();
        _users.FindByIdAsync(user.Id.ToString()).Returns(user);
        return (raw, user);
    }

    [Fact]
    public async Task RefreshAsync_ActiveToken_RotatesIt()
    {
        var (raw, user) = await SeedRefreshTokenAsync();
        var (service, context) = Create();
        await using var _ = context;

        var response = await service.RefreshAsync(raw, default);

        Assert.NotEqual(raw, response.RefreshToken);
        Assert.Equal(user.Id, response.User.Id);
        await using var verify = _db.NewContext();
        var tokens = await verify.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens, t => t.IsRevoked);
    }

    [Fact]
    public async Task RefreshAsync_UnknownToken_ThrowsUnauthorized()
    {
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.RefreshAsync("nope", default));
    }

    [Fact]
    public async Task RefreshAsync_ReusedRevokedToken_RevokesAllUserSessions()
    {
        var (raw, user) = await SeedRefreshTokenAsync(revoked: true);
        await using (var seed = _db.NewContext())
        {
            seed.RefreshTokens.Add(RefreshToken.Create(user.Id, "other-active", _db.Time.GetUtcNow(), TimeSpan.FromDays(1)));
            await seed.SaveChangesAsync();
        }

        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.RefreshAsync(raw, default));

        await using var verify = _db.NewContext();
        Assert.All(await verify.RefreshTokens.ToListAsync(), t => Assert.True(t.IsRevoked));
    }

    [Fact]
    public async Task RefreshAsync_ExpiredToken_ThrowsUnauthorized()
    {
        var (raw, _) = await SeedRefreshTokenAsync(TimeSpan.FromMinutes(1));
        _db.Time.Advance(TimeSpan.FromHours(1));
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.RefreshAsync(raw, default));
    }

    [Fact]
    public async Task RefreshAsync_UserNoLongerExists_ThrowsUnauthorized()
    {
        var (raw, user) = await SeedRefreshTokenAsync();
        _users.FindByIdAsync(user.Id.ToString()).Returns((ApplicationUser?)null);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.RefreshAsync(raw, default));
    }

    [Fact]
    public async Task LogoutAsync_KnownToken_RevokesIt()
    {
        var (raw, _) = await SeedRefreshTokenAsync();
        var (service, context) = Create();
        await using var _ = context;

        await service.LogoutAsync(raw, default);

        await using var verify = _db.NewContext();
        Assert.True((await verify.RefreshTokens.SingleAsync()).IsRevoked);
    }

    [Fact]
    public async Task LogoutAsync_UnknownToken_DoesNothing()
    {
        var (service, context) = Create();
        await using var _ = context;

        await service.LogoutAsync("unknown", default);
    }

    // ---- Email confirmation and password reset

    [Fact]
    public async Task ConfirmEmailAsync_ValidToken_ConfirmsEmail()
    {
        var user = _db.AddUser();
        _users.FindByIdAsync(user.Id.ToString()).Returns(user);
        _users.ConfirmEmailAsync(user, "raw").Returns(IdentityResult.Success);
        var (service, context) = Create();
        await using var _ = context;

        await service.ConfirmEmailAsync(user.Id, Encode("raw"), default);

        await _users.Received(1).ConfirmEmailAsync(user, "raw");
    }

    [Fact]
    public async Task ConfirmEmailAsync_UnknownUser_ThrowsValidationError()
    {
        _users.FindByIdAsync(Arg.Any<string>()).Returns((ApplicationUser?)null);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.ConfirmEmailAsync(Guid.NewGuid(), Encode("raw"), default));
    }

    [Fact]
    public async Task ConfirmEmailAsync_RejectedToken_ThrowsValidationError()
    {
        var user = _db.AddUser();
        _users.FindByIdAsync(user.Id.ToString()).Returns(user);
        _users.ConfirmEmailAsync(user, Arg.Any<string>()).Returns(IdentityResult.Failed());
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.ConfirmEmailAsync(user.Id, Encode("raw"), default));
    }

    [Fact]
    public async Task ConfirmEmailAsync_MalformedBase64_ThrowsValidationError()
    {
        var user = _db.AddUser();
        _users.FindByIdAsync(user.Id.ToString()).Returns(user);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.ConfirmEmailAsync(user.Id, "***", default));
    }

    [Fact]
    public async Task ForgotPasswordAsync_KnownEmail_QueuesResetLink()
    {
        var user = _db.AddUser("ann@example.com");
        _users.FindByEmailAsync("ann@example.com").Returns(user);
        _users.GeneratePasswordResetTokenAsync(user).Returns("reset-token");
        var (service, context) = Create();
        await using var _ = context;

        await service.ForgotPasswordAsync("ann@example.com", default);

        await _outbox.Received(1).EnqueueAsync(
            "ann@example.com", Arg.Any<string>(),
            Arg.Is<string>(b => b.Contains("https://app.test/reset-password") && b.Contains(Uri.EscapeDataString(Encode("reset-token")))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgotPasswordAsync_UnknownEmail_SilentlyDoesNothing()
    {
        _users.FindByEmailAsync(Arg.Any<string>()).Returns((ApplicationUser?)null);
        var (service, context) = Create();
        await using var _ = context;

        await service.ForgotPasswordAsync("nobody@example.com", default);

        await _outbox.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ResetPasswordAsync_Success_RevokesExistingSessions()
    {
        var (_, user) = await SeedRefreshTokenAsync();
        _users.FindByEmailAsync(user.Email!).Returns(user);
        _users.ResetPasswordAsync(user, "raw", "NewPassword123!").Returns(IdentityResult.Success);
        var (service, context) = Create();
        await using var _ = context;

        await service.ResetPasswordAsync(new ResetPasswordRequest { Email = user.Email!, Token = Encode("raw"), NewPassword = "NewPassword123!" }, default);

        await using var verify = _db.NewContext();
        Assert.All(await verify.RefreshTokens.ToListAsync(), t => Assert.True(t.IsRevoked));
    }

    [Fact]
    public async Task ResetPasswordAsync_UnknownEmail_ThrowsValidationError()
    {
        _users.FindByEmailAsync(Arg.Any<string>()).Returns((ApplicationUser?)null);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.ResetPasswordAsync(new ResetPasswordRequest { Email = "x@y.test", Token = Encode("raw"), NewPassword = "NewPassword123!" }, default));
    }

    [Fact]
    public async Task ResetPasswordAsync_IdentityRejects_ThrowsValidationError()
    {
        var user = _db.AddUser();
        _users.FindByEmailAsync(user.Email!).Returns(user);
        _users.ResetPasswordAsync(user, Arg.Any<string>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = "InvalidToken", Description = "bad" }));
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.ResetPasswordAsync(new ResetPasswordRequest { Email = user.Email!, Token = Encode("raw"), NewPassword = "NewPassword123!" }, default));
    }

    // ---- Current user

    [Fact]
    public async Task GetCurrentUserAsync_SignedIn_ReturnsProfile()
    {
        var user = _db.AddUser("ann@example.com", "Ann");
        _db.CurrentUser.UserId = user.Id;
        _users.FindByIdAsync(user.Id.ToString()).Returns(user);
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.GetCurrentUserAsync(default);

        Assert.Equal(new UserDto(user.Id, "ann@example.com", "Ann"), dto);
    }

    [Fact]
    public async Task GetCurrentUserAsync_Anonymous_ThrowsUnauthorized()
    {
        _db.CurrentUser.UserId = null;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.GetCurrentUserAsync(default));
    }

    [Fact]
    public async Task GetCurrentUserAsync_UserDeleted_ThrowsUnauthorized()
    {
        _db.CurrentUser.UserId = Guid.NewGuid();
        _users.FindByIdAsync(Arg.Any<string>()).Returns((ApplicationUser?)null);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.GetCurrentUserAsync(default));
    }
}

public class AuthControllerTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    private AuthController Controller() => new(_auth);

    [Fact]
    public async Task Register_ReturnsCreatedWithUser()
    {
        var dto = new UserDto(Guid.NewGuid(), "a@b.test", "A");
        _auth.RegisterAsync(Arg.Any<RegisterRequest>(), Arg.Any<CancellationToken>()).Returns(dto);

        var result = await Controller().Register(new RegisterRequest(), default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(dto, created.Value);
        Assert.Equal(nameof(AuthController.Me), created.ActionName);
    }

    [Fact]
    public async Task Login_ReturnsServiceResponse()
    {
        var response = new AuthResponse("a", DateTimeOffset.UtcNow, "r", new UserDto(Guid.NewGuid(), "e", "n"));
        _auth.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>()).Returns(response);

        var result = await Controller().Login(new LoginRequest(), default);

        Assert.Equal(response, result.Value);
    }

    [Fact]
    public async Task Refresh_PassesRefreshTokenToService()
    {
        var response = new AuthResponse("a", DateTimeOffset.UtcNow, "r2", new UserDto(Guid.NewGuid(), "e", "n"));
        _auth.RefreshAsync("old", Arg.Any<CancellationToken>()).Returns(response);

        var result = await Controller().Refresh(new RefreshRequest { RefreshToken = "old" }, default);

        Assert.Equal(response, result.Value);
    }

    [Fact]
    public async Task Logout_ReturnsNoContent()
    {
        var result = await Controller().Logout(new RefreshRequest { RefreshToken = "t" }, default);

        Assert.IsType<NoContentResult>(result);
        await _auth.Received(1).LogoutAsync("t", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmEmail_ReturnsNoContent()
    {
        var id = Guid.NewGuid();

        var result = await Controller().ConfirmEmail(new ConfirmEmailRequest { UserId = id, Token = "t" }, default);

        Assert.IsType<NoContentResult>(result);
        await _auth.Received(1).ConfirmEmailAsync(id, "t", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgotPassword_ReturnsAcceptedRegardlessOfAccountExistence()
    {
        var result = await Controller().ForgotPassword(new ForgotPasswordRequest { Email = "a@b.test" }, default);

        Assert.IsType<AcceptedResult>(result);
        await _auth.Received(1).ForgotPasswordAsync("a@b.test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetPassword_ReturnsNoContent()
    {
        var request = new ResetPasswordRequest { Email = "a@b.test", Token = "t", NewPassword = "NewPassword123!" };

        var result = await Controller().ResetPassword(request, default);

        Assert.IsType<NoContentResult>(result);
        await _auth.Received(1).ResetPasswordAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Me_ReturnsCurrentUser()
    {
        var dto = new UserDto(Guid.NewGuid(), "a@b.test", "A");
        _auth.GetCurrentUserAsync(Arg.Any<CancellationToken>()).Returns(dto);

        var result = await Controller().Me(default);

        Assert.Equal(dto, result.Value);
    }
}
