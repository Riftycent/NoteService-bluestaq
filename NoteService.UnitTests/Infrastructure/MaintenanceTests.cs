using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Maintenance;
using NoteService.Infrastructure.Storage;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Infrastructure;

public class TrashPurgeJobTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IBlobStore _blobs = Substitute.For<IBlobStore>();
    private readonly MaintenanceOptions _options = new() { TrashRetentionDays = 30, PurgeBatchSize = 10 };

    public void Dispose() => _db.Dispose();

    private TrashPurgeJob CreateJob(Infrastructure_Context context) =>
        new(context.Db, _blobs, _db.Time, Options.Create(_options), NullLogger<TrashPurgeJob>.Instance);

    private sealed class Infrastructure_Context(NoteService.Infrastructure.Persistence.NoteDbContext db) : IAsyncDisposable
    {
        public NoteService.Infrastructure.Persistence.NoteDbContext Db { get; } = db;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_NoteTrashedLongAgo_IsPurgedWithChildrenAndBlobs()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var old = _db.AddNote(team.Id, user.Id, "old", deleted: true);
        var tag = _db.AddTag(team.Id);
        await using (var seed = _db.NewContext())
        {
            seed.NoteTags.Add(new NoteTag { NoteId = old.Id, TagId = tag.Id });
            seed.NoteShares.Add(new NoteShare { NoteId = old.Id, UserId = user.Id, Permission = SharePermission.Read });
            seed.NoteVersions.Add(NoteVersion.Snapshot(old, 1, user.Id, _db.Time.GetUtcNow()));
            seed.Attachments.Add(new Attachment { Id = Guid.NewGuid(), NoteId = old.Id, FileName = "f", ContentType = "text/plain", StorageKey = "k/1", UploadedAt = _db.Time.GetUtcNow() });
            await seed.SaveChangesAsync();
        }

        _db.Time.Advance(TimeSpan.FromDays(31));
        await using var context = new Infrastructure_Context(_db.NewContext());
        await CreateJob(context).RunAsync(CancellationToken.None);

        await using var verify = _db.NewContext();
        Assert.Empty(await verify.Notes.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await verify.NoteTags.ToListAsync());
        Assert.Empty(await verify.NoteShares.ToListAsync());
        Assert.Empty(await verify.NoteVersions.ToListAsync());
        Assert.Empty(await verify.Attachments.ToListAsync());
        await _blobs.Received(1).DeleteAsync("k/1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RecentlyTrashedAndLiveNotes_AreKept()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        _db.AddNote(team.Id, user.Id, "live");
        _db.Time.Advance(TimeSpan.FromDays(10));
        _db.AddNote(team.Id, user.Id, "recent trash", deleted: true);
        _db.Time.Advance(TimeSpan.FromDays(10));

        await using var context = new Infrastructure_Context(_db.NewContext());
        await CreateJob(context).RunAsync(CancellationToken.None);

        await using var verify = _db.NewContext();
        Assert.Equal(2, await verify.Notes.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task RunAsync_BlobDeletionFails_StillCompletes()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var old = _db.AddNote(team.Id, user.Id, "old", deleted: true);
        await using (var seed = _db.NewContext())
        {
            seed.Attachments.Add(new Attachment { Id = Guid.NewGuid(), NoteId = old.Id, FileName = "f", ContentType = "text/plain", StorageKey = "k/1", UploadedAt = _db.Time.GetUtcNow() });
            await seed.SaveChangesAsync();
        }

        _blobs.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        _db.Time.Advance(TimeSpan.FromDays(31));
        await using var context = new Infrastructure_Context(_db.NewContext());

        await CreateJob(context).RunAsync(CancellationToken.None);

        await using var verify = _db.NewContext();
        Assert.Empty(await verify.Notes.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task RunAsync_RespectsBatchSize()
    {
        _options.PurgeBatchSize = 2;
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        for (var i = 0; i < 5; i++)
        {
            _db.AddNote(team.Id, user.Id, $"n{i}", deleted: true);
        }

        _db.Time.Advance(TimeSpan.FromDays(31));
        await using var context = new Infrastructure_Context(_db.NewContext());
        await CreateJob(context).RunAsync(CancellationToken.None);

        await using var verify = _db.NewContext();
        Assert.Equal(3, await verify.Notes.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public void Name_IdentifiesJob()
    {
        using var context = _db.NewContext();
        Assert.Equal(nameof(TrashPurgeJob), new TrashPurgeJob(context, _blobs, _db.Time, Options.Create(_options), NullLogger<TrashPurgeJob>.Instance).Name);
    }
}

public class ExpiredTokenCleanupJobTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RunAsync_RemovesExpiredAndLongRevokedTokensAndExpiredInvites()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var now = _db.Time.GetUtcNow();
        await using (var seed = _db.NewContext())
        {
            seed.RefreshTokens.Add(RefreshToken.Create(user.Id, "active", now, TimeSpan.FromDays(10)));
            seed.RefreshTokens.Add(RefreshToken.Create(user.Id, "expired", now.AddDays(-20), TimeSpan.FromDays(10)));
            var revokedOld = RefreshToken.Create(user.Id, "revoked-old", now, TimeSpan.FromDays(100));
            revokedOld.Revoke(now.AddDays(-10));
            seed.RefreshTokens.Add(revokedOld);
            var revokedRecent = RefreshToken.Create(user.Id, "revoked-recent", now, TimeSpan.FromDays(100));
            revokedRecent.Revoke(now.AddDays(-1));
            seed.RefreshTokens.Add(revokedRecent);
            seed.TeamInvites.Add(TeamInvite.Create(team.Id, "old@x.test", TeamRole.Viewer, "h1", user.Id, now.AddDays(-20), TimeSpan.FromDays(7)));
            seed.TeamInvites.Add(TeamInvite.Create(team.Id, "new@x.test", TeamRole.Viewer, "h2", user.Id, now, TimeSpan.FromDays(7)));
            await seed.SaveChangesAsync();
        }

        await using var context = _db.NewContext();
        var job = new ExpiredTokenCleanupJob(context, _db.Time, Options.Create(new MaintenanceOptions { RevokedTokenRetentionDays = 7 }));
        await job.RunAsync(CancellationToken.None);

        await using var verify = _db.NewContext();
        Assert.Equal(["active", "revoked-recent"], (await verify.RefreshTokens.Select(t => t.TokenHash).ToListAsync()).Order().ToList());
        Assert.Equal(["new@x.test"], await verify.TeamInvites.Select(i => i.Email).ToListAsync());
        Assert.Equal(nameof(ExpiredTokenCleanupJob), job.Name);
    }
}

public class EmailOutboxJobTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IEmailSender _sender = Substitute.For<IEmailSender>();

    public void Dispose() => _db.Dispose();

    private EmailOutboxJob CreateJob(NoteService.Infrastructure.Persistence.NoteDbContext context) =>
        new(context, _sender, _db.Time, Options.Create(new MaintenanceOptions { EmailBatchSize = 10 }), NullLogger<EmailOutboxJob>.Instance);

    private async Task SeedAsync(params OutboxEmail[] emails)
    {
        await using var seed = _db.NewContext();
        seed.OutboxEmails.AddRange(emails);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task RunAsync_PendingEmail_IsSentAndMarked()
    {
        await SeedAsync(OutboxEmail.Create("a@b.test", "Hi", "Body", _db.Time.GetUtcNow()));

        await using var context = _db.NewContext();
        await CreateJob(context).RunAsync(CancellationToken.None);

        await _sender.Received(1).SendAsync("a@b.test", "Hi", "Body", Arg.Any<CancellationToken>());
        await using var verify = _db.NewContext();
        var email = await verify.OutboxEmails.SingleAsync();
        Assert.NotNull(email.SentAt);
        Assert.Equal(1, email.Attempts);
    }

    [Fact]
    public async Task RunAsync_SenderFails_RecordsErrorAndContinuesWithNext()
    {
        var failing = OutboxEmail.Create("bad@b.test", "Hi", "Body", _db.Time.GetUtcNow());
        var good = OutboxEmail.Create("good@b.test", "Hi", "Body", _db.Time.GetUtcNow().AddSeconds(1));
        await SeedAsync(failing, good);
        _sender.SendAsync("bad@b.test", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("smtp down"));

        await using var context = _db.NewContext();
        await CreateJob(context).RunAsync(CancellationToken.None);

        await using var verify = _db.NewContext();
        var emails = await verify.OutboxEmails.ToDictionaryAsync(e => e.To);
        Assert.Null(emails["bad@b.test"].SentAt);
        Assert.Equal("smtp down", emails["bad@b.test"].LastError);
        Assert.NotNull(emails["good@b.test"].SentAt);
    }

    [Fact]
    public async Task RunAsync_AlreadySentOrExhaustedEmails_AreSkipped()
    {
        var sent = OutboxEmail.Create("sent@b.test", "Hi", "Body", _db.Time.GetUtcNow());
        sent.MarkSent(_db.Time.GetUtcNow());
        var exhausted = OutboxEmail.Create("dead@b.test", "Hi", "Body", _db.Time.GetUtcNow());
        for (var i = 0; i < OutboxEmail.MaxAttempts; i++)
        {
            exhausted.MarkFailed("x");
        }

        await SeedAsync(sent, exhausted);

        await using var context = _db.NewContext();
        await CreateJob(context).RunAsync(CancellationToken.None);

        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_NothingPending_DoesNothing()
    {
        await using var context = _db.NewContext();
        var job = CreateJob(context);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(nameof(EmailOutboxJob), job.Name);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }
}

public class OrphanBlobCleanupJobTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IBlobStore _blobs = Substitute.For<IBlobStore>();

    public void Dispose() => _db.Dispose();

    private static async IAsyncEnumerable<BlobInfo> Blobs(params BlobInfo[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_DeletesOnlyOldUnreferencedBlobs()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        var note = _db.AddNote(team.Id, user.Id);
        await using (var seed = _db.NewContext())
        {
            seed.Attachments.Add(new Attachment { Id = Guid.NewGuid(), NoteId = note.Id, FileName = "f", ContentType = "text/plain", StorageKey = "known", UploadedAt = _db.Time.GetUtcNow() });
            await seed.SaveChangesAsync();
        }

        var now = _db.Time.GetUtcNow();
        _blobs.ListAsync(Arg.Any<CancellationToken>()).Returns(Blobs(
            new BlobInfo("known", now.AddDays(-5)),
            new BlobInfo("orphan-old", now.AddDays(-5)),
            new BlobInfo("orphan-new", now.AddHours(-1))));

        await using var context = _db.NewContext();
        var job = new OrphanBlobCleanupJob(context, _blobs, _db.Time, Options.Create(new MaintenanceOptions { OrphanBlobGraceHours = 24 }), NullLogger<OrphanBlobCleanupJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        await _blobs.Received(1).DeleteAsync("orphan-old", Arg.Any<CancellationToken>());
        await _blobs.DidNotReceive().DeleteAsync("known", Arg.Any<CancellationToken>());
        await _blobs.DidNotReceive().DeleteAsync("orphan-new", Arg.Any<CancellationToken>());
        Assert.Equal(nameof(OrphanBlobCleanupJob), job.Name);
    }
}

public class MaintenanceWorkerTests
{
    [Fact]
    public async Task RunOnceAsync_RunsEveryJobEvenWhenOneFails()
    {
        var failing = Substitute.For<IMaintenanceJob>();
        failing.Name.Returns("failing");
        failing.RunAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var healthy = Substitute.For<IMaintenanceJob>();
        healthy.Name.Returns("healthy");

        var services = new ServiceCollection();
        services.AddScoped(_ => failing);
        services.AddScoped(_ => healthy);
        services.AddScoped<IMaintenanceJob>(sp => sp.GetRequiredService<IMaintenanceJob>());
        await using var provider = new ServiceCollection()
            .AddSingleton<IEnumerable<IMaintenanceJob>>([failing, healthy])
            .BuildServiceProvider();

        var factory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        factory.CreateScope().Returns(scope);

        var worker = new MaintenanceWorker(factory, Options.Create(new MaintenanceOptions()), NullLogger<MaintenanceWorker>.Instance);
        await worker.RunOnceAsync(CancellationToken.None);

        await failing.Received(1).RunAsync(Arg.Any<CancellationToken>());
        await healthy.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StopsWhenCancelled()
    {
        var job = Substitute.For<IMaintenanceJob>();
        await using var provider = new ServiceCollection().AddSingleton<IEnumerable<IMaintenanceJob>>([job]).BuildServiceProvider();
        var factory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        factory.CreateScope().Returns(scope);
        var worker = new MaintenanceWorker(factory, Options.Create(new MaintenanceOptions { IntervalSeconds = 1 }), NullLogger<MaintenanceWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        await job.Received().RunAsync(Arg.Any<CancellationToken>());
    }
}
