using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Features.Shares;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Shares;

public class ShareServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (ShareService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        var teamAccess = new TeamAccess(context, _db.CurrentUser);
        return (new ShareService(context, new NoteAccessService(context, teamAccess, _db.CurrentUser), _db.CurrentUser), context);
    }

    private (Team Team, ApplicationUser Owner, ApplicationUser Colleague, Note Note) Seed()
    {
        var owner = _db.AddUser("owner@example.com", "Owner");
        var colleague = _db.AddUser("colleague@example.com", "Colleague");
        var team = _db.AddTeam(owner.Id);
        _db.AddMember(team.Id, colleague.Id, TeamRole.Viewer);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        return (team, owner, colleague, note);
    }

    [Fact]
    public async Task SetAsync_NewShare_IsCreatedAndReturnedWithProfile()
    {
        var (team, _, colleague, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Edit, default);

        Assert.Equal(new ShareDto(colleague.Id, "colleague@example.com", "Colleague", SharePermission.Edit), dto);
        await using var verify = _db.NewContext();
        Assert.Equal(SharePermission.Edit, (await verify.NoteShares.SingleAsync()).Permission);
    }

    [Fact]
    public async Task SetAsync_ExistingShare_UpdatesPermission()
    {
        var (team, _, colleague, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        await service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Edit, default);

        await service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Read, default);

        await using var verify = _db.NewContext();
        Assert.Equal(SharePermission.Read, (await verify.NoteShares.SingleAsync()).Permission);
    }

    [Fact]
    public async Task SetAsync_UserOutsideTeam_ThrowsValidationError()
    {
        var (team, _, _, note) = Seed();
        var outsider = _db.AddUser();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.SetAsync(team.Id, note.Id, outsider.Id, SharePermission.Read, default));
    }

    [Fact]
    public async Task SetAsync_NoteAuthorWithEditorRole_CanManageSharing()
    {
        var (team, _, colleague, _) = Seed();
        var editor = _db.AddUser();
        _db.AddMember(team.Id, editor.Id, TeamRole.Editor);
        var note = _db.AddNote(team.Id, editor.Id, "mine");
        _db.CurrentUser.UserId = editor.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Read, default);

        Assert.Equal(SharePermission.Read, dto.Permission);
    }

    [Fact]
    public async Task SetAsync_EditorWhoIsNotTheAuthor_IsForbidden()
    {
        var (team, _, colleague, note) = Seed();
        var editor = _db.AddUser();
        _db.AddMember(team.Id, editor.Id, TeamRole.Editor);
        _db.CurrentUser.UserId = editor.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Read, default));
    }

    [Fact]
    public async Task SetAsync_AdminWhoIsNotTheAuthor_CanManageSharing()
    {
        var (team, _, colleague, note) = Seed();
        var admin = _db.AddUser();
        _db.AddMember(team.Id, admin.Id, TeamRole.Admin);
        _db.CurrentUser.UserId = admin.Id;
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Read, default);

        Assert.Equal(colleague.Id, dto.UserId);
    }

    [Fact]
    public async Task ListAsync_ReturnsSharesByDisplayName()
    {
        var (team, owner, colleague, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        await service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Read, default);
        await service.SetAsync(team.Id, note.Id, owner.Id, SharePermission.Edit, default);

        var shares = await service.ListAsync(team.Id, note.Id, default);

        Assert.Equal(["Colleague", "Owner"], shares.Select(s => s.DisplayName));
    }

    [Fact]
    public async Task ListAsync_ViewerWithoutManageRights_IsForbidden()
    {
        var (team, _, colleague, note) = Seed();
        _db.CurrentUser.UserId = colleague.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.ListAsync(team.Id, note.Id, default));
    }

    [Fact]
    public async Task RemoveAsync_ExistingShare_DeletesIt()
    {
        var (team, _, colleague, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;
        await service.SetAsync(team.Id, note.Id, colleague.Id, SharePermission.Read, default);

        await service.RemoveAsync(team.Id, note.Id, colleague.Id, default);

        await using var verify = _db.NewContext();
        Assert.Empty(await verify.NoteShares.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_NoSuchShare_ThrowsNotFound()
    {
        var (team, _, colleague, note) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveAsync(team.Id, note.Id, colleague.Id, default));
    }
}

public class SharesControllerTests
{
    [Fact]
    public async Task Actions_DelegateToService()
    {
        var service = Substitute.For<IShareService>();
        var teamId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var share = new ShareDto(userId, "e", "n", SharePermission.Edit);
        IReadOnlyList<ShareDto> shares = [share];
        service.ListAsync(teamId, noteId, Arg.Any<CancellationToken>()).Returns(shares);
        service.SetAsync(teamId, noteId, userId, SharePermission.Edit, Arg.Any<CancellationToken>()).Returns(share);
        var controller = new SharesController(service);

        Assert.Equal(shares, Assert.IsType<OkObjectResult>((await controller.List(teamId, noteId, default)).Result).Value);
        Assert.Equal(share, (await controller.Set(teamId, noteId, userId, new SetShareRequest { Permission = SharePermission.Edit }, default)).Value);
        Assert.IsType<NoContentResult>(await controller.Remove(teamId, noteId, userId, default));
        await service.Received(1).RemoveAsync(teamId, noteId, userId, Arg.Any<CancellationToken>());
    }
}
