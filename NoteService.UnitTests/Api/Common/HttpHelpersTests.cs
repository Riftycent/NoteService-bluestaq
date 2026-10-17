using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NoteService.API.Common;
using NoteService.Domain.Exceptions;

namespace NoteService.UnitTests.Api.Common;

public class ETagHelperTests
{
    [Fact]
    public void Format_WrapsStampInQuotes()
    {
        var stamp = Guid.NewGuid();
        Assert.Equal($"\"{stamp:N}\"", ETagHelper.Format(stamp));
    }

    [Fact]
    public void Parse_RoundTripsFormattedValue()
    {
        var stamp = Guid.NewGuid();
        Assert.Equal(stamp, ETagHelper.Parse(ETagHelper.Format(stamp)));
    }

    [Fact]
    public void Parse_WeakValidator_IsAccepted()
    {
        var stamp = Guid.NewGuid();
        Assert.Equal(stamp, ETagHelper.Parse($"W/\"{stamp:N}\""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"not-a-guid\"")]
    public void Parse_MissingOrInvalid_ReturnsNull(string? value) => Assert.Null(ETagHelper.Parse(value));

    [Fact]
    public void RequireIfMatch_ValidHeader_ReturnsStamp()
    {
        var stamp = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = ETagHelper.Format(stamp);

        Assert.Equal(stamp, ETagHelper.RequireIfMatch(context.Request));
    }

    [Fact]
    public void RequireIfMatch_MissingHeader_ThrowsPreconditionRequired() =>
        Assert.Throws<PreconditionRequiredException>(() => ETagHelper.RequireIfMatch(new DefaultHttpContext().Request));

    [Fact]
    public void RequireIfMatch_MalformedHeader_ThrowsPreconditionFailed()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = "\"garbage\"";

        Assert.Throws<PreconditionFailedException>(() => ETagHelper.RequireIfMatch(context.Request));
    }
}

public class CursorTests
{
    [Fact]
    public void EncodeDecode_RoundTripsKeyAndId()
    {
        var id = Guid.NewGuid();

        var token = Cursor.Decode(Cursor.Encode("some key | with pipe", id));

        Assert.Equal(id, token.Id);
        Assert.Equal("some key | with pipe", token.Key);
    }

    [Fact]
    public void Encode_ProducesUrlSafeText()
    {
        var encoded = Cursor.Encode(new string('?', 50), Guid.NewGuid());

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("bm90LWEtY3Vyc29y")]
    public void Decode_InvalidCursor_ThrowsValidationError(string cursor) =>
        Assert.Throws<ValidationFailedException>(() => Cursor.Decode(cursor));
}

public class TokenHasherTests
{
    [Fact]
    public void Generate_ReturnsDistinctUrlSafeTokens()
    {
        var a = TokenHasher.Generate();
        var b = TokenHasher.Generate();

        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    [Fact]
    public void Hash_IsDeterministic64CharLowerHex()
    {
        var hash = TokenHasher.Hash("secret");

        Assert.Equal(hash, TokenHasher.Hash("secret"));
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.NotEqual(hash, TokenHasher.Hash("Secret"));
    }
}

public class SnippetBuilderTests
{
    [Fact]
    public void Build_EmptyText_ReturnsEmpty() => Assert.Equal(string.Empty, SnippetBuilder.Build("", ["x"]));

    [Fact]
    public void Build_ShortText_ReturnsWholeTextWithoutEllipsis() =>
        Assert.Equal("hello world", SnippetBuilder.Build("hello world", ["world"]));

    [Fact]
    public void Build_MatchDeepInText_CentersOnMatchWithEllipses()
    {
        var text = new string('a', 300) + " needle " + new string('b', 300);

        var snippet = SnippetBuilder.Build(text, ["NEEDLE"], 100);

        Assert.Contains("needle", snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Build_NoMatch_StartsAtBeginning()
    {
        var snippet = SnippetBuilder.Build(new string('a', 500), ["zzz"], 50);

        Assert.StartsWith("a", snippet);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Build_NewLines_AreFlattened() => Assert.DoesNotContain('\n', SnippetBuilder.Build("a\nb\nc", ["b"]));
}

public class HttpCurrentUserTests
{
    private static HttpCurrentUser Create(HttpContext? context)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return new HttpCurrentUser(accessor);
    }

    private static HttpContext WithClaim(string type, string value) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(type, value)], "test"))
    };

    [Fact]
    public void UserId_SubClaim_IsParsed()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Create(WithClaim("sub", id.ToString())).UserId);
    }

    [Fact]
    public void UserId_NameIdentifierClaim_IsUsedAsFallback()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Create(WithClaim(ClaimTypes.NameIdentifier, id.ToString())).UserId);
    }

    [Fact]
    public void UserId_NonGuidClaim_ReturnsNull() => Assert.Null(Create(WithClaim("sub", "abc")).UserId);

    [Fact]
    public void UserId_NoHttpContext_ReturnsNull() => Assert.Null(Create(null).UserId);

    [Fact]
    public void UserId_AnonymousUser_ReturnsNull() => Assert.Null(Create(new DefaultHttpContext()).UserId);
}
