using System.ComponentModel.DataAnnotations;

namespace NoteService.API.Features.Auth;

public sealed record RegisterRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 10)]
    public string Password { get; init; } = string.Empty;

    [Required, StringLength(100)]
    public string DisplayName { get; init; } = string.Empty;
}

public sealed record LoginRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(128)]
    public string Password { get; init; } = string.Empty;
}

public sealed record RefreshRequest
{
    [Required, StringLength(200)]
    public string RefreshToken { get; init; } = string.Empty;
}

public sealed record ConfirmEmailRequest
{
    [Required]
    public Guid UserId { get; init; }

    [Required, StringLength(2000)]
    public string Token { get; init; } = string.Empty;
}

public sealed record ForgotPasswordRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;
}

public sealed record ResetPasswordRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(2000)]
    public string Token { get; init; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 10)]
    public string NewPassword { get; init; } = string.Empty;
}

public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken, UserDto User);
