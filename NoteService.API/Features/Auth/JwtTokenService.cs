using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NoteService.API.Common;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Auth;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public sealed record IssuedRefreshToken(string Raw, string Hash);

public interface IJwtTokenService
{
    AccessToken CreateAccessToken(ApplicationUser user);

    IssuedRefreshToken CreateRefreshToken();

    string HashToken(string rawToken);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time) : IJwtTokenService
{
    public AccessToken CreateAccessToken(ApplicationUser user)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    public IssuedRefreshToken CreateRefreshToken()
    {
        var raw = TokenHasher.Generate(48);
        return new IssuedRefreshToken(raw, TokenHasher.Hash(raw));
    }

    public string HashToken(string rawToken) => TokenHasher.Hash(rawToken);
}
