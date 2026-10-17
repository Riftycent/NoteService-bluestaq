using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Teams;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Email;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Teams;

public class InviteServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IEmailOutbox _outbox = Substitute.For<IEmailOutbox>();
    private readonly AppOptions _app = new() { PublicBaseUrl = "https://app.test", InviteLifetimeDays = 7 };

    public void Dispose() => _db.Dispose();

    private (InviteService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        return (new InviteService(context, new TeamAccess(context, _db.CurrentUser), _db.CurrentUser, _outbox, _db.Time, Options.Create(_app)), context);
    }

    private string SeedInvite(Guid teamId, Guid invitedBy, string email, TeamRole role = TeamRole.Editor, TimeSpan? lifetime = null)
    {
        var token = TokenHasher.Generate();
        using var context = _db.NewContext();
        context.TeamInvites.Add(TeamInvite.Create(teamId, email, role, TokenHasher.Hash(token), invitedBy, _db.Time.GetUtcNow(), lifetime ?? TimeSpan.FromDays(7)));
        context.SaveChanges();
        return token;
    }

    [Fact]
    public async Task CreateAsync_AdminInvitesEditor_StoresHashedTokenAndEmailsLink()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id, "Core");
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.CreateAsync(team.Id, " New@Example.com ", TeamRole.Editor, default);

        Assert.Equal("new@example.com", dto.Email);
        Assert.Equal(_db.Time.GetUtcNow().AddDays(7), dto.ExpiresAt);
        await _outbox.Received(1).EnqueueAsync("new@example.com", Arg.Is<string>(s => s.Contains("Core")), Arg.Any<string>(), Arg.Any<CancellationToken>());
        var body = (string)_outbox.ReceivedCalls().Single().GetArguments()[2]!;
        var token = body.Split("/invites/")[1];
        await using var verify = _db.NewContext();
        var stored = await verify.TeamInvites.SingleAsync();
        Assert.Equal(TokenHasher.Hash(token), stored.TokenHash);
        Assert.DoesNotContain(token, stored.TokenHash);
    }

    [Fact]
    public async Task CreateAsync_AdminInvitingAdmin_IsForbidden()
    {
        var owner = _db.AddUser();
        var admin = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, admin.Id, TeamRole.Admin);
        _db.CurrentUser.UserId = admin.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.CreateAsync(team.Id, "x@y.test", TeamRole.Admin, default));
    }

    [Fact]
    public async Task CreateAsync_EmailOfExistingMember_ThrowsConflict()
    {
        var owner = _db.AddUser("owner@example.com");
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(team.Id, "OWNER@example.com", TeamRole.Viewer, default));
    }

    [Fact]
    public async Task CreateAsync_SecondInviteForSameEmail_ReplacesTheFirst()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await service.CreateAsync(team.Id, "x@y.test", TeamRole.Viewer, default);
        await service.CreateAsync(team.Id, "x@y.test", TeamRole.Editor, default);

        await using var verify = _db.NewContext();
        Assert.Equal(TeamRole.Editor, (await verify.TeamInvites.SingleAsync()).Role);
    }

    [Fact]
    public async Task CreateAsync_NonMember_ThrowsNotFound()
    {
        var team = _db.AddTeam(_db.AddUser().Id);
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateAsync(team.Id, "x@y.test", TeamRole.Viewer, default));
    }

    [Fact]
    public async Task ListAsync_ReturnsPendingInvitesOldestFirst()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        SeedInvite(team.Id, owner.Id, "first@y.test");
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        SeedInvite(team.Id, owner.Id, "second@y.test");
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        var invites = await service.ListAsync(team.Id, default);

        Assert.Equal(["first@y.test", "second@y.test"], invites.Select(i => i.Email));
    }

    [Fact]
    public async Task RevokeAsync_ExistingInvite_DeletesIt()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        SeedInvite(team.Id, owner.Id, "x@y.test");
        _db.CurrentUser.UserId = owner.Id;
        Guid inviteId;
        await using (var read = _db.NewContext())
        {
            inviteId = (await read.TeamInvites.SingleAsync()).Id;
        }

        var (service, context) = Create();
        await using var _ = context;

        await service.RevokeAsync(team.Id, inviteId, default);

        await using var verify = _db.NewContext();
        Assert.Empty(await verify.TeamInvites.ToListAsync());
    }

    [Fact]
    public async Task RevokeAsync_UnknownInvite_ThrowsNotFound()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RevokeAsync(team.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task AcceptAsync_ValidInviteForMatchingEmail_JoinsTeamAndConsumesInvite()
    {
        var owner = _db.AddUser();
        var invitee = _db.AddUser("invitee@example.com");
        var team = _db.AddTeam(owner.Id, "Core");
        var token = SeedInvite(team.Id, owner.Id, "Invitee@Example.com", TeamRole.Editor);
        _db.CurrentUser.UserId = invitee.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.AcceptAsync(token, default);

        Assert.Equal(("Core", TeamRole.Editor), (dto.Name, dto.Role));
        await using var verify = _db.NewContext();
        Assert.Contains(await verify.TeamMembers.ToListAsync(), m => m.UserId == invitee.Id && m.Role == TeamRole.Editor);
        Assert.Empty(await verify.TeamInvites.ToListAsync());
    }

    [Fact]
    public async Task AcceptAsync_AlreadyMember_KeepsExistingRole()
    {
        var owner = _db.AddUser();
        var invitee = _db.AddUser("invitee@example.com");
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, invitee.Id, TeamRole.Admin);
        var token = SeedInvite(team.Id, owner.Id, "invitee@example.com", TeamRole.Viewer);
        _db.CurrentUser.UserId = invitee.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.AcceptAsync(token, default);

        Assert.Equal(TeamRole.Admin, dto.Role);
    }

    [Fact]
    public async Task AcceptAsync_InviteForAnotherEmail_ThrowsNotFound()
    {
        var owner = _db.AddUser();
        var intruder = _db.AddUser("intruder@example.com");
        var team = _db.AddTeam(owner.Id);
        var token = SeedInvite(team.Id, owner.Id, "invitee@example.com");
        _db.CurrentUser.UserId = intruder.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.AcceptAsync(token, default));
    }

    [Fact]
    public async Task AcceptAsync_ExpiredInvite_ThrowsNotFound()
    {
        var owner = _db.AddUser();
        var invitee = _db.AddUser("invitee@example.com");
        var team = _db.AddTeam(owner.Id);
        var token = SeedInvite(team.Id, owner.Id, "invitee@example.com", lifetime: TimeSpan.FromDays(1));
        _db.Time.Advance(TimeSpan.FromDays(2));
        _db.CurrentUser.UserId = invitee.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.AcceptAsync(token, default));
    }

    [Fact]
    public async Task AcceptAsync_UnknownToken_ThrowsNotFound()
    {
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.AcceptAsync("bogus", default));
    }
}
