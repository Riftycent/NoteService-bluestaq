using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoteService.API.Common;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Auth;

public interface IAuthService
{
    Task<UserDto> RegisterAsync(RegisterRequest request, CancellationToken ct);

    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct);

    Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct);

    Task LogoutAsync(string refreshToken, CancellationToken ct);

    Task ConfirmEmailAsync(Guid userId, string token, CancellationToken ct);

    Task ForgotPasswordAsync(string email, CancellationToken ct);

    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct);

    Task<UserDto> GetCurrentUserAsync(CancellationToken ct);
}

public sealed class AuthService(
    UserManager<ApplicationUser> users,
    NoteDbContext db,
    IJwtTokenService tokens,
    IEmailOutbox outbox,
    TimeProvider time,
    ICurrentUser currentUser,
    IOptions<JwtOptions> jwtOptions,
    IOptions<AppOptions> appOptions) : IAuthService
{
    private const string InvalidCredentials = "Invalid email or password.";

    public async Task<UserDto> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = time.GetUtcNow(),
            EmailConfirmed = !appOptions.Value.RequireConfirmedEmail
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw ToValidationException(result);
        }

        if (appOptions.Value.RequireConfirmedEmail)
        {
            var token = Encode(await users.GenerateEmailConfirmationTokenAsync(user));
            var link = $"{appOptions.Value.PublicBaseUrl.TrimEnd('/')}/confirm-email?userId={user.Id}&token={Uri.EscapeDataString(token)}";
            await outbox.EnqueueAsync(email, "Confirm your NoteService email", $"Welcome, {user.DisplayName}! Confirm your email address: {link}", ct);
        }

        return ToDto(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim()) ?? throw new UnauthorizedAppException(InvalidCredentials);

        if (await users.IsLockedOutAsync(user))
        {
            throw new UnauthorizedAppException("The account is temporarily locked. Try again later.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        if (appOptions.Value.RequireConfirmedEmail && !user.EmailConfirmed)
        {
            throw new UnauthorizedAppException("Confirm your email address before signing in.");
        }

        await users.ResetAccessFailedCountAsync(user);
        return await IssueTokensAsync(user, ct);
    }

    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = tokens.HashToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedAppException("The refresh token is not valid.");

        if (stored.IsRevoked)
        {
            // A rotated token was presented again: assume theft and end every session of this user.
            await RevokeAllAsync(stored.UserId, now, ct);
            throw new UnauthorizedAppException("The refresh token is not valid.");
        }

        if (stored.IsExpired(now))
        {
            throw new UnauthorizedAppException("The refresh token has expired.");
        }

        var user = await users.FindByIdAsync(stored.UserId.ToString()) ?? throw new UnauthorizedAppException("The refresh token is not valid.");
        stored.Revoke(now);
        return await IssueTokensAsync(user, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = tokens.HashToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is not null)
        {
            stored.Revoke(time.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ConfirmEmailAsync(Guid userId, string token, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new ValidationFailedException("token", "The confirmation link is not valid.");
        var result = await users.ConfirmEmailAsync(user, Decode(token));
        if (!result.Succeeded)
        {
            throw new ValidationFailedException("token", "The confirmation link is not valid or has expired.");
        }
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        // Always succeeds silently so the endpoint cannot be used to discover registered addresses.
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return;
        }

        var token = Encode(await users.GeneratePasswordResetTokenAsync(user));
        var link = $"{appOptions.Value.PublicBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(token)}";
        await outbox.EnqueueAsync(user.Email!, "Reset your NoteService password", $"Reset your password: {link}", ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim()) ?? throw new ValidationFailedException("token", "The reset link is not valid.");
        var result = await users.ResetPasswordAsync(user, Decode(request.Token), request.NewPassword);
        if (!result.Succeeded)
        {
            throw ToValidationException(result);
        }

        await RevokeAllAsync(user.Id, time.GetUtcNow(), ct);
    }

    public async Task<UserDto> GetCurrentUserAsync(CancellationToken ct)
    {
        var id = currentUser.RequireId();
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new UnauthorizedAppException();
        return ToDto(user);
    }

    private async Task<AuthResponse> IssueTokensAsync(ApplicationUser user, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(user);
        var refresh = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(RefreshToken.Create(user.Id, refresh.Hash, time.GetUtcNow(), TimeSpan.FromDays(jwtOptions.Value.RefreshTokenDays)));
        await db.SaveChangesAsync(ct);
        return new AuthResponse(access.Token, access.ExpiresAt, refresh.Raw, ToDto(user));
    }

    private async Task RevokeAllAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in active)
        {
            token.Revoke(now);
        }

        await db.SaveChangesAsync(ct);
    }

    private static UserDto ToDto(ApplicationUser user) => new(user.Id, user.Email ?? string.Empty, user.DisplayName);

    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static string Decode(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            throw new ValidationFailedException("token", "The token is not valid.");
        }
    }

    internal static ValidationFailedException ToValidationException(IdentityResult result)
    {
        var errors = result.Errors
            .GroupBy(e => e.Code switch
            {
                var c when c.Contains("Password", StringComparison.Ordinal) => "password",
                var c when c.Contains("Email", StringComparison.Ordinal) || c.Contains("UserName", StringComparison.Ordinal) => "email",
                _ => "request"
            })
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        return new ValidationFailedException(errors);
    }
}
