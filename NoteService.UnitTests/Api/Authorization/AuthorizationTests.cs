using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Authorization;

public class TeamAccessTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetRoleAsync_Member_ReturnsRole()
    {
        var owner = _db.AddUser();
        var editor = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, editor.Id, TeamRole.Editor);
        _db.CurrentUser.UserId = editor.Id;
        await using var context = _db.NewContext();

        Assert.Equal(TeamRole.Editor, await new TeamAccess(context, _db.CurrentUser).GetRoleAsync(team.Id));
    }

    [Fact]
    public async Task GetRoleAsync_NonMember_ReturnsNull()
    {
        var owner = _db.AddUser();
        var stranger = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = stranger.Id;
        await using var context = _db.NewContext();

        Assert.Null(await new TeamAccess(context, _db.CurrentUser).GetRoleAsync(team.Id));
    }

    [Fact]
    public async Task GetRoleAsync_Anonymous_ReturnsNull()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = null;
        await using var context = _db.NewContext();

        Assert.Null(await new TeamAccess(context, _db.CurrentUser).GetRoleAsync(team.Id));
    }

    [Fact]
    public async Task GetRoleAsync_RepeatedCalls_AreServedFromCache()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        await using var context = _db.NewContext();
        var access = new TeamAccess(context, _db.CurrentUser);

        Assert.Equal(TeamRole.Owner, await access.GetRoleAsync(team.Id));

        // Remove the membership behind the cache's back: the cached role must still be returned.
        await using (var other = _db.NewContext())
        {
            other.TeamMembers.RemoveRange(other.TeamMembers);
            await other.SaveChangesAsync();
        }

        Assert.Equal(TeamRole.Owner, await access.GetRoleAsync(team.Id));
    }

    [Fact]
    public async Task RequireRoleAsync_Member_ReturnsRole()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        await using var context = _db.NewContext();

        Assert.Equal(TeamRole.Owner, await new TeamAccess(context, _db.CurrentUser).RequireRoleAsync(team.Id));
    }

    [Fact]
    public async Task RequireRoleAsync_NonMember_ThrowsNotFound()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = Guid.NewGuid();
        await using var context = _db.NewContext();

        await Assert.ThrowsAsync<NotFoundException>(() => new TeamAccess(context, _db.CurrentUser).RequireRoleAsync(team.Id));
    }
}

public class TeamRoleHandlerTests
{
    private static async Task<bool> EvaluateAsync(TeamRole? actual, TeamRole required)
    {
        var access = Substitute.For<ITeamAccess>();
        access.GetRoleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(actual);
        var requirement = new TeamRoleRequirement(required);
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(), Guid.NewGuid());

        await new TeamRoleHandler(access).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Admin, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, true)]
    [InlineData(TeamRole.Editor, TeamRole.Admin, false)]
    [InlineData(TeamRole.Viewer, TeamRole.Viewer, true)]
    [InlineData(TeamRole.Viewer, TeamRole.Editor, false)]
    public async Task HandleRequirement_ComparesRoles(TeamRole actual, TeamRole required, bool expected) =>
        Assert.Equal(expected, await EvaluateAsync(actual, required));

    [Fact]
    public async Task HandleRequirement_NotAMember_Fails() => Assert.False(await EvaluateAsync(null, TeamRole.Viewer));
}

public class TeamAuthorizeAttributeTests
{
    private static AuthorizationFilterContext CreateContext(
        ITeamAccess access, IAuthorizationService authorization, object? teamIdRouteValue, bool authenticated = true)
    {
        var services = new ServiceCollection()
            .AddSingleton(access)
            .AddSingleton(authorization)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        if (authenticated)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"));
        }

        var routeData = new RouteData();
        if (teamIdRouteValue is not null)
        {
            routeData.Values["teamId"] = teamIdRouteValue;
        }

        return new AuthorizationFilterContext(new ActionContext(http, routeData, new ActionDescriptor()), []);
    }

    private static IAuthorizationService Authorization(bool succeed)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(succeed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return authorization;
    }

    [Fact]
    public async Task OnAuthorizationAsync_MemberWithSufficientRole_LeavesRequestUntouched()
    {
        var access = Substitute.For<ITeamAccess>();
        access.GetRoleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TeamRole.Admin);
        var context = CreateContext(access, Authorization(true), Guid.NewGuid().ToString());

        await new TeamAuthorizeAttribute(TeamRole.Editor).OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task OnAuthorizationAsync_NonMember_ThrowsNotFound()
    {
        var access = Substitute.For<ITeamAccess>();
        access.GetRoleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TeamRole?)null);
        var context = CreateContext(access, Authorization(true), Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<NotFoundException>(() => new TeamAuthorizeAttribute().OnAuthorizationAsync(context));
    }

    [Fact]
    public async Task OnAuthorizationAsync_MemberWithInsufficientRole_ThrowsForbidden()
    {
        var access = Substitute.For<ITeamAccess>();
        access.GetRoleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TeamRole.Viewer);
        var context = CreateContext(access, Authorization(false), Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<ForbiddenException>(() => new TeamAuthorizeAttribute(TeamRole.Admin).OnAuthorizationAsync(context));
    }

    [Fact]
    public async Task OnAuthorizationAsync_MissingOrInvalidTeamId_ThrowsNotFound()
    {
        var access = Substitute.For<ITeamAccess>();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new TeamAuthorizeAttribute().OnAuthorizationAsync(CreateContext(access, Authorization(true), null)));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            new TeamAuthorizeAttribute().OnAuthorizationAsync(CreateContext(access, Authorization(true), "not-a-guid")));
    }

    [Fact]
    public async Task OnAuthorizationAsync_Unauthenticated_ReturnsUnauthorizedResult()
    {
        var context = CreateContext(Substitute.For<ITeamAccess>(), Authorization(true), Guid.NewGuid().ToString(), authenticated: false);

        await new TeamAuthorizeAttribute().OnAuthorizationAsync(context);

        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    [Fact]
    public void Constructor_DefaultsToViewer() => Assert.Equal(TeamRole.Viewer, new TeamAuthorizeAttribute().Minimum);
}

public class NoteAccessServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (NoteAccessService Service, NoteService.Infrastructure.Persistence.NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        return (new NoteAccessService(context, new TeamAccess(context, _db.CurrentUser), _db.CurrentUser), context);
    }

    [Fact]
    public async Task RequireAsync_EditorWithoutShare_GetsEditAccess()
    {
        var owner = _db.AddUser();
        var editor = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, editor.Id, TeamRole.Editor);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = editor.Id;
        var (service, context) = Create();
        await using var _ = context;

        var result = await service.RequireAsync(team.Id, note.Id, NoteAccess.Edit);

        Assert.Equal(note.Id, result.Note.Id);
        Assert.Equal(TeamRole.Editor, result.Role);
        Assert.Equal(NoteAccess.Edit, result.Access);
    }

    [Fact]
    public async Task RequireAsync_ViewerWithoutShare_CanReadButNotEdit()
    {
        var owner = _db.AddUser();
        var viewer = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, viewer.Id, TeamRole.Viewer);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = Create();
        await using var _ = context;

        Assert.Equal(NoteAccess.Read, (await service.RequireAsync(team.Id, note.Id, NoteAccess.Read)).Access);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.RequireAsync(team.Id, note.Id, NoteAccess.Edit));
    }

    [Fact]
    public async Task RequireAsync_ViewerWithEditShare_CanEdit()
    {
        var owner = _db.AddUser();
        var viewer = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, viewer.Id, TeamRole.Viewer);
        var note = _db.AddNote(team.Id, owner.Id);
        await using (var seed = _db.NewContext())
        {
            seed.NoteShares.Add(new NoteService.Domain.Entities.NoteShare { NoteId = note.Id, UserId = viewer.Id, Permission = SharePermission.Edit });
            await seed.SaveChangesAsync();
        }

        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = Create();
        await using var _ = context;

        Assert.Equal(NoteAccess.Edit, (await service.RequireAsync(team.Id, note.Id, NoteAccess.Edit)).Access);
    }

    [Fact]
    public async Task RequireAsync_NonMember_ThrowsNotFound()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = Guid.NewGuid();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RequireAsync(team.Id, note.Id, NoteAccess.Read));
    }

    [Fact]
    public async Task RequireAsync_NoteOfAnotherTeam_ThrowsNotFound()
    {
        var owner = _db.AddUser();
        var teamA = _db.AddTeam(owner.Id, "A");
        var teamB = _db.AddTeam(owner.Id, "B");
        var note = _db.AddNote(teamB.Id, owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RequireAsync(teamA.Id, note.Id, NoteAccess.Read));
    }

    [Fact]
    public async Task RequireAsync_DeletedNote_NotFoundUnlessIncluded()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        var note = _db.AddNote(team.Id, owner.Id, deleted: true);
        _db.CurrentUser.UserId = owner.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RequireAsync(team.Id, note.Id, NoteAccess.Read));
        var result = await service.RequireAsync(team.Id, note.Id, NoteAccess.Read, includeDeleted: true);
        Assert.True(result.Note.IsDeleted);
    }

    [Fact]
    public async Task RequireAsync_Anonymous_ThrowsUnauthorized()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = null;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() => service.RequireAsync(team.Id, note.Id, NoteAccess.Read));
    }
}
