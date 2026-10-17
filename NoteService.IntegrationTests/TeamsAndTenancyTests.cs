using System.Net;
using NoteService.API.Features.Folders;
using NoteService.API.Features.Notes;
using NoteService.API.Features.Tags;
using NoteService.API.Features.Teams;
using NoteService.Domain.Enums;
using NoteService.IntegrationTests.TestSupport;

namespace NoteService.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TeamsAndTenancyTests(ApiFactory factory)
{
    [Fact]
    public async Task CreateTeam_MakesCreatorOwnerAndListsIt()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");

        var team = await owner.CreateTeamAsync("Core");
        var teams = await owner.ReadAsync<List<TeamDto>>(await owner.GetAsync("/api/v1/teams"));

        Assert.Equal(TeamRole.Owner, team.Role);
        Assert.Contains(teams, t => t.Id == team.Id);
    }

    [Fact]
    public async Task NonMember_CannotSeeAnythingInATeam_AndGets404NotForbidden()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var stranger = await ApiClient.RegisterAsync(factory, "stranger");
        var team = await owner.CreateTeamAsync();
        var note = await owner.CreateNoteAsync(team.Id);

        var urls = new[]
        {
            $"/api/v1/teams/{team.Id}",
            $"/api/v1/teams/{team.Id}/members",
            $"/api/v1/teams/{team.Id}/notes",
            $"/api/v1/teams/{team.Id}/notes/{note.Id}",
            $"/api/v1/teams/{team.Id}/notes/{note.Id}/versions",
            $"/api/v1/teams/{team.Id}/folders",
            $"/api/v1/teams/{team.Id}/tags",
            $"/api/v1/teams/{team.Id}/trash",
            $"/api/v1/teams/{team.Id}/search?q=content"
        };

        foreach (var url in urls)
        {
            await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await stranger.GetAsync(url));
        }

        var teams = await stranger.ReadAsync<List<TeamDto>>(await stranger.GetAsync("/api/v1/teams"));
        Assert.DoesNotContain(teams, t => t.Id == team.Id);
    }

    [Fact]
    public async Task NonMember_CannotWriteToATeam()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var stranger = await ApiClient.RegisterAsync(factory, "stranger");
        var team = await owner.CreateTeamAsync();
        var note = await owner.CreateNoteAsync(team.Id);

        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound,
            await stranger.PostAsync($"/api/v1/teams/{team.Id}/notes", new CreateNoteRequest { Title = "intruder" }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound,
            await stranger.PatchAsync($"/api/v1/teams/{team.Id}/notes/{note.Id}", new UpdateNoteRequest { Title = "x" }, note.ETag));
        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await stranger.DeleteAsync($"/api/v1/teams/{team.Id}", null));
    }

    [Fact]
    public async Task NoteIdFromAnotherTeam_IsNotReachableThroughMyTeam()
    {
        var alice = await ApiClient.RegisterAsync(factory, "alice");
        var bob = await ApiClient.RegisterAsync(factory, "bob");
        var aliceTeam = await alice.CreateTeamAsync("A");
        var bobTeam = await bob.CreateTeamAsync("B");
        var aliceNote = await alice.CreateNoteAsync(aliceTeam.Id);

        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await bob.GetAsync($"/api/v1/teams/{bobTeam.Id}/notes/{aliceNote.Id}"));
    }

    [Fact]
    public async Task Invite_Accept_GrantsMembershipWithInvitedRole()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var invitee = await ApiClient.RegisterAsync(factory, "invitee");
        var team = await owner.CreateTeamAsync("Core");

        await owner.AddMemberAsync(team.Id, invitee, TeamRole.Viewer, factory);

        var members = await owner.ReadAsync<List<MemberDto>>(await owner.GetAsync($"/api/v1/teams/{team.Id}/members"));
        Assert.Equal(TeamRole.Viewer, members.Single(m => m.UserId == invitee.UserId).Role);
        var theirTeams = await invitee.ReadAsync<List<TeamDto>>(await invitee.GetAsync("/api/v1/teams"));
        Assert.Contains(theirTeams, t => t.Id == team.Id);
    }

    [Fact]
    public async Task Invite_CannotBeAcceptedByADifferentAccount()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var invitee = await ApiClient.RegisterAsync(factory, "invitee");
        var intruder = await ApiClient.RegisterAsync(factory, "intruder");
        var team = await owner.CreateTeamAsync();
        await owner.PostAsync($"/api/v1/teams/{team.Id}/invites", new CreateInviteRequest { Email = invitee.Email, Role = TeamRole.Editor });
        var token = await InviteTokens.ReadLatestAsync(factory, invitee.Email);

        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await intruder.PostAsync($"/api/v1/invites/{token}/accept", new { }));
    }

    [Fact]
    public async Task Viewer_CanRead_ButNotWrite()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var viewer = await ApiClient.RegisterAsync(factory, "viewer");
        var team = await owner.CreateTeamAsync();
        await owner.AddMemberAsync(team.Id, viewer, TeamRole.Viewer, factory);
        var note = await owner.CreateNoteAsync(team.Id, "Shared", "readable");

        var read = await viewer.ReadAsync<NoteDto>(await viewer.GetAsync($"/api/v1/teams/{team.Id}/notes/{note.Id}"));
        Assert.Equal(NoteAccess.Read, read.Access);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden,
            await viewer.PostAsync($"/api/v1/teams/{team.Id}/notes", new CreateNoteRequest { Title = "no" }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden,
            await viewer.PatchAsync($"/api/v1/teams/{team.Id}/notes/{note.Id}", new UpdateNoteRequest { Title = "no" }, note.ETag));
        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden,
            await viewer.PostAsync($"/api/v1/teams/{team.Id}/tags", new CreateTagRequest { Name = "x" }));
    }

    [Fact]
    public async Task Editor_CanWriteNotes_ButNotManageFoldersOrMembers()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var editor = await ApiClient.RegisterAsync(factory, "editor");
        var team = await owner.CreateTeamAsync();
        await owner.AddMemberAsync(team.Id, editor, TeamRole.Editor, factory);

        var note = await editor.CreateNoteAsync(team.Id, "By editor");
        Assert.Equal(editor.UserId, note.CreatedBy);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden,
            await editor.PostAsync($"/api/v1/teams/{team.Id}/folders", new CreateFolderRequest { Name = "F" }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden,
            await editor.PostAsync($"/api/v1/teams/{team.Id}/invites", new CreateInviteRequest { Email = "x@y.test", Role = TeamRole.Viewer }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden, await editor.DeleteAsync($"/api/v1/teams/{team.Id}"));
    }

    [Fact]
    public async Task Admin_CanManageFoldersAndMembers_ButNotDeleteTeam()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var admin = await ApiClient.RegisterAsync(factory, "admin");
        var team = await owner.CreateTeamAsync();
        await owner.AddMemberAsync(team.Id, admin, TeamRole.Admin, factory);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Created,
            await admin.PostAsync($"/api/v1/teams/{team.Id}/folders", new CreateFolderRequest { Name = "F" }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.Created,
            await admin.PostAsync($"/api/v1/teams/{team.Id}/invites", new CreateInviteRequest { Email = "new@example.com", Role = TeamRole.Viewer }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden, await admin.DeleteAsync($"/api/v1/teams/{team.Id}"));
    }

    [Fact]
    public async Task Member_CanLeave_ButLastOwnerCannot()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var member = await ApiClient.RegisterAsync(factory, "member");
        var team = await owner.CreateTeamAsync();
        await owner.AddMemberAsync(team.Id, member, TeamRole.Editor, factory);

        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent, await member.DeleteAsync($"/api/v1/teams/{team.Id}/members/{member.UserId}"));
        await ApiClient.AssertStatusAsync(HttpStatusCode.Conflict, await owner.DeleteAsync($"/api/v1/teams/{team.Id}/members/{owner.UserId}"));
        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await member.GetAsync($"/api/v1/teams/{team.Id}"));
    }

    [Fact]
    public async Task OwnerPromotesMember_ThenMemberHasNewPowers()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var member = await ApiClient.RegisterAsync(factory, "member");
        var team = await owner.CreateTeamAsync();
        await owner.AddMemberAsync(team.Id, member, TeamRole.Viewer, factory);

        var changed = await owner.ReadAsync<MemberDto>(
            await owner.PatchAsync($"/api/v1/teams/{team.Id}/members/{member.UserId}", new ChangeRoleRequest { Role = TeamRole.Admin }));

        Assert.Equal(TeamRole.Admin, changed.Role);
        await ApiClient.AssertStatusAsync(HttpStatusCode.Created,
            await member.PostAsync($"/api/v1/teams/{team.Id}/folders", new CreateFolderRequest { Name = "Now allowed" }));
    }

    [Fact]
    public async Task DeleteTeam_RemovesEverythingInIt()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var team = await owner.CreateTeamAsync();
        var folder = await owner.ReadAsync<FolderDto>(await owner.PostAsync($"/api/v1/teams/{team.Id}/folders", new CreateFolderRequest { Name = "F" }));
        await owner.CreateNoteAsync(team.Id, "n", "c", folder.Id);
        await owner.PostAsync($"/api/v1/teams/{team.Id}/tags", new CreateTagRequest { Name = "t" });

        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent, await owner.DeleteAsync($"/api/v1/teams/{team.Id}"));

        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await owner.GetAsync($"/api/v1/teams/{team.Id}"));
    }

    [Fact]
    public async Task InvalidRequestBody_ReturnsValidationProblem()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");

        var response = await owner.PostAsync("/api/v1/teams", new CreateTeamRequest { Name = "" });

        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, response);
    }
}
