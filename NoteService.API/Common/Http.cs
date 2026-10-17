using System.Security.Claims;
using System.Text;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Exceptions;

namespace NoteService.API.Common;

/// <summary>Resolves the signed-in user from the JWT "sub" claim.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            var value = user?.FindFirstValue("sub") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }
}

public static class ETagHelper
{
    /// <summary>Formats a concurrency stamp as a strong ETag, e.g. <c>"3f2a…"</c>.</summary>
    public static string Format(Guid stamp) => $"\"{stamp:N}\"";

    /// <summary>Parses a single ETag header value (weak prefix and quotes are tolerated).</summary>
    public static Guid? Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var value = headerValue.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');
        return Guid.TryParse(value, out var stamp) ? stamp : null;
    }

    /// <summary>Reads the mandatory If-Match header of a mutating request.</summary>
    public static Guid RequireIfMatch(HttpRequest request)
    {
        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new PreconditionRequiredException();
        }

        return Parse(header) ?? throw new PreconditionFailedException("The If-Match header is not a valid ETag.");
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record CursorToken(string Key, Guid Id);

/// <summary>Opaque keyset-pagination cursors: base64url of "&lt;id&gt;|&lt;sort key&gt;".</summary>
public static class Cursor
{
    public static string Encode(string key, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id:N}|{key}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static CursorToken Decode(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var separator = text.IndexOf('|');
            if (separator > 0 && Guid.TryParseExact(text[..separator], "N", out var id))
            {
                return new CursorToken(text[(separator + 1)..], id);
            }
        }
        catch (FormatException)
        {
        }

        throw new ValidationFailedException("cursor", "The cursor is not valid.");
    }
}

/// <summary>Cryptographically random, URL-safe tokens that are stored only as SHA-256 hashes.</summary>
public static class TokenHasher
{
    public static string Generate(int bytes = 32) =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class SnippetBuilder
{
    /// <summary>Builds a short excerpt around the first occurrence of any search term.</summary>
    public static string Build(string text, IEnumerable<string> terms, int length = 160)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flat = text.ReplaceLineEndings(" ");
        var index = terms
            .Select(t => flat.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, index - length / 4);
        var take = Math.Min(length, flat.Length - start);
        var snippet = flat.Substring(start, take).Trim();
        return (start > 0 ? "…" : string.Empty) + snippet + (start + take < flat.Length ? "…" : string.Empty);
    }
}
