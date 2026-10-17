using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Features.Teams;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.Infrastructure.Storage;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Teams;

public class TeamServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IBlobStore _blobs = Substitute.For<IBlobStore>();

    public void Dispose() => _db.Dispose();

    private (TeamService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        return (new TeamService(context, _db.CurrentUser, new TeamAccess(context, _db.CurrentUser), _blobs, _db.Time), context);
    }

    [Fact]
    public async Task CreateAsync_MakesCallerTheOwner()
    {
        var user = _db.AddUser();
        _db.CurrentUser.UserId = user.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.CreateAsync("  Core  ", default);

        Assert.Equal("Core", dto.Name);
        Assert.Equal(TeamRole.Owner, dto.Role);
        await using var verify = _db.NewContext();
        var member = await verify.TeamMembers.SingleAsync();
        Assert.Equal((user.Id, dto.Id, TeamRole.Owner), (member.UserId, member.TeamId, member.Role));
    }

    [Fact]
    public async Task CreateAsync_InvalidName_Throws()
    {
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.CreateAsync(" ", default));
    }

    [Fact]
    public async Task CreateAsync_Anonymous_ThrowsUnauthorized()
    {
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.CreateAsync("Core", default));
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyCallersTeamsWithRolesSortedByName()
    {
        var me = _db.AddUser();
        var other = _db.AddUser();
        _db.AddTeam(me.Id, "Zeta");
        var shared = _db.AddTeam(other.Id, "Alpha");
        _db.AddMember(shared.Id, me.Id, TeamRole.Viewer);
        _db.AddTeam(other.Id, "Hidden");
        _db.CurrentUser.UserId = me.Id;
        var (service, context) = Create();
        await using var _ = context;

        var teams = await service.ListAsync(default);

        Assert.Equal([("Alpha", TeamRole.Viewer), ("Zeta", TeamRole.Owner)], teams.Select(t => (t.Name, t.Role)));
    }

    [Fact]
    public async Task GetAsync_Member_ReturnsTeamWithCallersRole()
    {
        var me = _db.AddUser();
        var team = _db.AddTeam(me.Id, "Core");
        _db.CurrentUser.UserId = me.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.GetAsync(team.Id, default);

        Assert.Equal(new TeamDto(team.Id, "Core", TeamRole.Owner, dto.CreatedAt), dto);
    }

    [Fact]
    public async Task GetAsync_NonMember_ThrowsNotFound()
    {
        var team = _db.AddTeam(_db.AddUser().Id);
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(team.Id, default));
    }

    [Fact]
    public async Task RenameAsync_UpdatesName()
    {
        var me = _db.AddUser();
        var team = _db.AddTeam(me.Id, "Old");
        _db.CurrentUser.UserId = me.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.RenameAsync(team.Id, "New", default);

        Assert.Equal("New", dto.Name);
        await using var verify = _db.NewContext();
        Assert.Equal("New", (await verify.Teams.SingleAsync()).Name);
    }

    [Fact]
    public async Task RenameAsync_NonMember_ThrowsNotFound()
    {
        var team = _db.AddTeam(_db.AddUser().Id);
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RenameAsync(team.Id, "New", default));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTeamEverythingInsideItAndBlobs()
    {
        var me = _db.AddUser();
        var team = _db.AddTeam(me.Id);
        var otherTeam = _db.AddTeam(me.Id, "Keep");
        var folder = _db.AddFolder(team.Id);
        var tag = _db.AddTag(team.Id);
        var note = _db.AddNote(team.Id, me.Id, folderId: folder.Id);
        var trashed = _db.AddNote(team.Id, me.Id, "trashed", deleted: true);
        var keptNote = _db.AddNote(otherTeam.Id, me.Id, "keep");
        await using (var seed = _db.NewContext())
        {
            seed.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = tag.Id });
            seed.NoteShares.Add(new NoteShare { NoteId = note.Id, UserId = me.Id, Permission = SharePermission.Read });
            seed.NoteVersions.Add(NoteVersion.Snapshot(note, 1, me.Id, _db.Time.GetUtcNow()));
            seed.Attachments.Add(new Attachment { Id = Guid.NewGuid(), NoteId = trashed.Id, FileName = "f", ContentType = "text/plain", StorageKey = "key-1", UploadedAt = _db.Time.GetUtcNow() });
            seed.TeamInvites.Add(TeamInvite.Create(team.Id, "x@y.test", TeamRole.Viewer, "h", me.Id, _db.Time.GetUtcNow(), TimeSpan.FromDays(1)));
            await seed.SaveChangesAsync();
        }

        _db.CurrentUser.UserId = me.Id;
        var (service, context) = Create();
        await using var _ = context;

        await service.DeleteAsync(team.Id, default);

        await using var verify = _db.NewContext();
        Assert.Equal([otherTeam.Id], await verify.Teams.Select(t => t.Id).ToListAsync());
        Assert.Equal([keptNote.Id], await verify.Notes.IgnoreQueryFilters().Select(n => n.Id).ToListAsync());
        Assert.Empty(await verify.Folders.ToListAsync());
        Assert.Empty(await verify.Tags.ToListAsync());
        Assert.Empty(await verify.NoteTags.ToListAsync());
        Assert.Empty(await verify.NoteShares.ToListAsync());
        Assert.Empty(await verify.NoteVersions.ToListAsync());
        Assert.Empty(await verify.Attachments.ToListAsync());
        Assert.Empty(await verify.TeamInvites.ToListAsync());
        Assert.Equal([otherTeam.Id], await verify.TeamMembers.Select(m => m.TeamId).ToListAsync());
        await _blobs.Received(1).DeleteAsync("key-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_BlobDeletionFails_StillSucceeds()
    {
        var me = _db.AddUser();
        var team = _db.AddTeam(me.Id);
        var note = _db.AddNote(team.Id, me.Id);
        await using (var seed = _db.NewContext())
        {
            seed.Attachments.Add(new Attachment { Id = Guid.NewGuid(), NoteId = note.Id, FileName = "f", ContentType = "text/plain", StorageKey = "key-1", UploadedAt = _db.Time.GetUtcNow() });
            await seed.SaveChangesAsync();
        }

        _blobs.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("disk")));
        _db.CurrentUser.UserId = me.Id;
        var (service, context) = Create();
        await using var _ = context;

        await service.DeleteAsync(team.Id, default);

        await using var verify = _db.NewContext();
        _db.CurrentUser.UserId = me.Id;
        Assert.Empty(await verify.Teams.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_NonMember_ThrowsNotFoundAndKeepsTeam()
    {
        var team = _db.AddTeam(_db.AddUser().Id);
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(team.Id, default));

        await using var verify = _db.NewContext();
        Assert.Single(await verify.Teams.IgnoreQueryFilters().ToListAsync());
    }
}

public class MemberServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (MemberService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        return (new MemberService(context, new TeamAccess(context, _db.CurrentUser), _db.CurrentUser), context);
    }

    private (Team Team, ApplicationUser Owner, ApplicationUser Member) SeedTeam(TeamRole memberRole)
    {
        var owner = _db.AddUser("owner@example.com", "Owner");
        var member = _db.AddUser("member@example.com", "Member");
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, member.Id, memberRole);
        return (team, owner, member);
    }

    [Fact]
    public async Task ListAsync_ReturnsMembersWithProfiles()
    {
        var (team, owner, member) = SeedTeam(TeamRole.Editor);
        _db.CurrentUser.UserId = member.Id;
        var (service, context) = Create();
        await using var _ = context;

        var members = await service.ListAsync(team.Id, default);

        Assert.Equal(["Member", "Owner"], members.Select(m => m.DisplayName));
        Assert.Equal(owner.Email, members.Single(m => m.UserId == owner.Id).Email);
        Assert.Equal(TeamRole.Editor, members.Single(m => m.UserId == member.Id).Role);
    }

    [Fact]
    public async Task ListAsync_NonMember_ThrowsNotFound()
    {
        var (team, _, _) = SeedTeam(TeamRole.Editor);
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.ListAsync(team.Id, default));
    }

    [Fact]
    public async Task ChangeRoleAsync_OwnerPromotesMember_Succeeds()
    {
        var (team, owner, member) = SeedTeam(TeamRole.Viewer);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.ChangeRoleAsync(team.Id, member.Id, TeamRole.Admin, default);

        Assert.Equal(TeamRole.Admin, dto.Role);
        await using var verify = _db.NewContext();
        Assert.Equal(TeamRole.Admin, (await verify.TeamMembers.SingleAsync(m => m.UserId == member.Id)).Role);
    }

    [Fact]
    public async Task ChangeRoleAsync_AdminPromotingToAdmin_IsForbidden()
    {
        var (team, _, admin) = SeedTeam(TeamRole.Admin);
        var viewer = _db.AddUser();
        _db.AddMember(team.Id, viewer.Id, TeamRole.Viewer);
        _db.CurrentUser.UserId = admin.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.ChangeRoleAsync(team.Id, viewer.Id, TeamRole.Admin, default));
    }

    [Fact]
    public async Task ChangeRoleAsync_UnknownMember_ThrowsNotFound()
    {
        var (team, owner, _) = SeedTeam(TeamRole.Viewer);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.ChangeRoleAsync(team.Id, Guid.NewGuid(), TeamRole.Editor, default));
    }

    [Fact]
    public async Task ChangeRoleAsync_DemotingLastOwner_ThrowsConflict()
    {
        var (team, owner, _) = SeedTeam(TeamRole.Viewer);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ConflictException>(() => service.ChangeRoleAsync(team.Id, owner.Id, TeamRole.Admin, default));
    }

    [Fact]
    public async Task ChangeRoleAsync_DemotingOwnerWhenAnotherOwnerExists_Succeeds()
    {
        var (team, owner, member) = SeedTeam(TeamRole.Owner);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.ChangeRoleAsync(team.Id, member.Id, TeamRole.Editor, default);

        Assert.Equal(TeamRole.Editor, dto.Role);
    }

    [Fact]
    public async Task RemoveAsync_OwnerRemovesMember_AlsoDropsTheirNoteShares()
    {
        var (team, owner, member) = SeedTeam(TeamRole.Editor);
        var note = _db.AddNote(team.Id, owner.Id);
        await using (var seed = _db.NewContext())
        {
            seed.NoteShares.Add(new NoteShare { NoteId = note.Id, UserId = member.Id, Permission = SharePermission.Read });
            await seed.SaveChangesAsync();
        }

        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await service.RemoveAsync(team.Id, member.Id, default);

        await using var verify = _db.NewContext();
        Assert.DoesNotContain(await verify.TeamMembers.ToListAsync(), m => m.UserId == member.Id);
        Assert.Empty(await verify.NoteShares.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_MemberLeavingTheTeam_Succeeds()
    {
        var (team, _, member) = SeedTeam(TeamRole.Viewer);
        _db.CurrentUser.UserId = member.Id;
        var (service, context) = Create();
        await using var _ = context;

        await service.RemoveAsync(team.Id, member.Id, default);

        await using var verify = _db.NewContext();
        Assert.Single(await verify.TeamMembers.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_ViewerRemovingSomeoneElse_IsForbidden()
    {
        var (team, owner, member) = SeedTeam(TeamRole.Viewer);
        _db.CurrentUser.UserId = member.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.RemoveAsync(team.Id, owner.Id, default));
    }

    [Fact]
    public async Task RemoveAsync_LastOwnerLeaving_ThrowsConflict()
    {
        var (team, owner, _) = SeedTeam(TeamRole.Viewer);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ConflictException>(() => service.RemoveAsync(team.Id, owner.Id, default));
    }

    [Fact]
    public async Task RemoveAsync_UnknownMember_ThrowsNotFound()
    {
        var (team, owner, _) = SeedTeam(TeamRole.Viewer);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveAsync(team.Id, Guid.NewGuid(), default));
    }
}

public class TeamControllersTests
{
    [Fact]
    public async Task TeamsController_List_ReturnsOkWithTeams()
    {
        var service = Substitute.For<ITeamService>();
        IReadOnlyList<TeamDto> teams = [new TeamDto(Guid.NewGuid(), "T", TeamRole.Owner, DateTimeOffset.UtcNow)];
        service.ListAsync(Arg.Any<CancellationToken>()).Returns(teams);

        var result = await new TeamsController(service).List(default);

        Assert.Equal(teams, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task TeamsController_Create_ReturnsCreatedAtGet()
    {
        var service = Substitute.For<ITeamService>();
        var dto = new TeamDto(Guid.NewGuid(), "T", TeamRole.Owner, DateTimeOffset.UtcNow);
        service.CreateAsync("T", Arg.Any<CancellationToken>()).Returns(dto);

        var result = await new TeamsController(service).Create(new CreateTeamRequest { Name = "T" }, default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(TeamsController.Get), created.ActionName);
        Assert.Equal(dto.Id, created.RouteValues!["teamId"]);
    }

    [Fact]
    public async Task TeamsController_GetUpdateDelete_DelegateToService()
    {
        var service = Substitute.For<ITeamService>();
        var teamId = Guid.NewGuid();
        var dto = new TeamDto(teamId, "T", TeamRole.Owner, DateTimeOffset.UtcNow);
        service.GetAsync(teamId, Arg.Any<CancellationToken>()).Returns(dto);
        service.RenameAsync(teamId, "N", Arg.Any<CancellationToken>()).Returns(dto with { Name = "N" });
        var controller = new TeamsController(service);

        Assert.Equal(dto, (await controller.Get(teamId, default)).Value);
        Assert.Equal("N", (await controller.Update(teamId, new UpdateTeamRequest { Name = "N" }, default)).Value!.Name);
        Assert.IsType<NoContentResult>(await controller.Delete(teamId, default));
        await service.Received(1).DeleteAsync(teamId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MembersController_Actions_DelegateToService()
    {
        var service = Substitute.For<IMemberService>();
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        IReadOnlyList<MemberDto> members = [new MemberDto(userId, "e", "n", TeamRole.Editor, DateTimeOffset.UtcNow)];
        service.ListAsync(teamId, Arg.Any<CancellationToken>()).Returns(members);
        service.ChangeRoleAsync(teamId, userId, TeamRole.Admin, Arg.Any<CancellationToken>()).Returns(members[0] with { Role = TeamRole.Admin });
        var controller = new MembersController(service);

        Assert.Equal(members, Assert.IsType<OkObjectResult>((await controller.List(teamId, default)).Result).Value);
        Assert.Equal(TeamRole.Admin, (await controller.ChangeRole(teamId, userId, new ChangeRoleRequest { Role = TeamRole.Admin }, default)).Value!.Role);
        Assert.IsType<NoContentResult>(await controller.Remove(teamId, userId, default));
        await service.Received(1).RemoveAsync(teamId, userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvitesController_Actions_DelegateToService()
    {
        var service = Substitute.For<IInviteService>();
        var teamId = Guid.NewGuid();
        var invite = new InviteDto(Guid.NewGuid(), "a@b.test", TeamRole.Editor, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
        var team = new TeamDto(teamId, "T", TeamRole.Editor, DateTimeOffset.UtcNow);
        IReadOnlyList<InviteDto> invites = [invite];
        service.ListAsync(teamId, Arg.Any<CancellationToken>()).Returns(invites);
        service.CreateAsync(teamId, "a@b.test", TeamRole.Editor, Arg.Any<CancellationToken>()).Returns(invite);
        service.AcceptAsync("tok", Arg.Any<CancellationToken>()).Returns(team);
        var controller = new InvitesController(service);

        Assert.Equal(invites, Assert.IsType<OkObjectResult>((await controller.List(teamId, default)).Result).Value);
        var created = Assert.IsType<CreatedAtActionResult>((await controller.Create(teamId, new CreateInviteRequest { Email = "a@b.test", Role = TeamRole.Editor }, default)).Result);
        Assert.Equal(invite, created.Value);
        Assert.IsType<NoContentResult>(await controller.Revoke(teamId, invite.Id, default));
        await service.Received(1).RevokeAsync(teamId, invite.Id, Arg.Any<CancellationToken>());
        Assert.Equal(team, (await controller.Accept("tok", default)).Value);
    }
}
