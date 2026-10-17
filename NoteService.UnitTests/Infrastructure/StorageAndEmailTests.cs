using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Infrastructure;

public class LocalDiskBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noteservice-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDiskBlobStore _store;

    public LocalDiskBlobStoreTests() =>
        _store = new LocalDiskBlobStore(Options.Create(new BlobStorageOptions { RootPath = _root }));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static MemoryStream Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task SaveAsync_ThenOpenRead_ReturnsSameContent()
    {
        await _store.SaveAsync("team/note/file", Bytes("hello"));

        await using var stream = await _store.OpenReadAsync("team/note/file");

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SaveAsync_ExistingKey_Overwrites()
    {
        await _store.SaveAsync("k/a", Bytes("one"));
        await _store.SaveAsync("k/a", Bytes("two"));

        await using var stream = await _store.OpenReadAsync("k/a");
        using var reader = new StreamReader(stream!);
        Assert.Equal("two", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task OpenReadAsync_MissingKey_ReturnsNull() => Assert.Null(await _store.OpenReadAsync("missing/key"));

    [Fact]
    public async Task DeleteAsync_ExistingKey_RemovesFile()
    {
        await _store.SaveAsync("k/a", Bytes("x"));

        await _store.DeleteAsync("k/a");

        Assert.Null(await _store.OpenReadAsync("k/a"));
    }

    [Fact]
    public async Task DeleteAsync_MissingKey_DoesNotThrow() => await _store.DeleteAsync("nothing/here");

    [Fact]
    public async Task ListAsync_ReturnsKeysWithForwardSlashes()
    {
        await _store.SaveAsync("t/n/a", Bytes("1"));
        await _store.SaveAsync("t/n/b", Bytes("2"));

        var keys = new List<string>();
        await foreach (var blob in _store.ListAsync())
        {
            keys.Add(blob.Key);
            Assert.True(blob.LastModified > DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        Assert.Equal(["t/n/a", "t/n/b"], keys.Order().ToList());
    }

    [Fact]
    public async Task ListAsync_NoRootDirectory_ReturnsEmpty()
    {
        var count = 0;
        await foreach (var _ in _store.ListAsync())
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    public void Resolve_PathTraversal_Throws(string key) => Assert.Throws<ArgumentException>(() => _store.Resolve(key));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_BlankKey_Throws(string key) => Assert.Throws<ArgumentException>(() => _store.Resolve(key));

    [Fact]
    public void Resolve_ValidKey_StaysUnderRoot() =>
        Assert.StartsWith(Path.GetFullPath(_root), _store.Resolve("a/b/c"));
}

public class LoggingEmailSenderTests
{
    [Fact]
    public async Task SendAsync_LogsRecipientAtInformationLevel()
    {
        var logger = Substitute.For<ILogger<LoggingEmailSender>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        await new LoggingEmailSender(logger).SendAsync("a@b.test", "Hello", "Body");

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("a@b.test")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}

public class EmailOutboxTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task EnqueueAsync_PersistsUnsentEmail()
    {
        await using var context = _db.NewContext();
        var outbox = new EmailOutbox(context, _db.Time);

        await outbox.EnqueueAsync("a@b.test", "Subject", "Body");

        await using var verify = _db.NewContext();
        var email = await verify.OutboxEmails.SingleAsync();
        Assert.Equal("a@b.test", email.To);
        Assert.Equal("Subject", email.Subject);
        Assert.Equal("Body", email.Body);
        Assert.Null(email.SentAt);
        Assert.Equal(_db.Time.GetUtcNow(), email.CreatedAt);
    }
}

public class DbContextExtensionsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task InTransactionAsync_WorkSucceeds_CommitsChanges()
    {
        await using var context = _db.NewContext();

        await context.InTransactionAsync(async ct =>
        {
            context.OutboxEmails.Add(NoteService.Domain.Entities.OutboxEmail.Create("a@b.test", "s", "b", _db.Time.GetUtcNow()));
            await context.SaveChangesAsync(ct);
        });

        await using var verify = _db.NewContext();
        Assert.Equal(1, await verify.OutboxEmails.CountAsync());
    }

    [Fact]
    public async Task InTransactionAsync_WorkThrows_RollsBackChanges()
    {
        await using var context = _db.NewContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.InTransactionAsync(async ct =>
        {
            context.OutboxEmails.Add(NoteService.Domain.Entities.OutboxEmail.Create("a@b.test", "s", "b", _db.Time.GetUtcNow()));
            await context.SaveChangesAsync(ct);
            throw new InvalidOperationException("fail");
        }));

        await using var verify = _db.NewContext();
        Assert.Equal(0, await verify.OutboxEmails.CountAsync());
    }
}
