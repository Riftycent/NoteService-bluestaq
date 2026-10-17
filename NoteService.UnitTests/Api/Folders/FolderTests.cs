using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Features.Folders;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Folders;

public class FolderServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (FolderService Service, NoteDbContext Context, Guid TeamId, Guid UserId) Setup()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        _db.CurrentUser.UserId = user.Id;
        var context = _db.NewContext();
        return (new FolderService(context, new TeamAccess(context, _db.CurrentUser)), context, team.Id, user.Id);
    }

    [Fact]
    public async Task CreateAsync_TopLevelFolder_IsPersisted()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;

        var dto = await service.CreateAsync(teamId, new CreateFolderRequest { Name = " Specs " }, default);

        Assert.Equal("Specs", dto.Name);
        Assert.Null(dto.ParentId);
        await using var verify = _db.NewContext();
        Assert.Single(await verify.Folders.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_WithExistingParent_NestsFolder()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var parent = _db.AddFolder(teamId, "Parent");

        var dto = await service.CreateAsync(teamId, new CreateFolderRequest { Name = "Child", ParentId = parent.Id }, default);

        Assert.Equal(parent.Id, dto.ParentId);
    }

    [Fact]
    public async Task CreateAsync_ParentInAnotherTeam_ThrowsValidationError()
    {
        var (service, context, teamId, userId) = Setup();
        await using var _ = context;
        var otherTeam = _db.AddTeam(userId, "Other");
        var foreign = _db.AddFolder(otherTeam.Id);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.CreateAsync(teamId, new CreateFolderRequest { Name = "X", ParentId = foreign.Id }, default));
    }

    [Fact]
    public async Task ListAsync_ReturnsTeamFoldersByName()
    {
        var (service, context, teamId, userId) = Setup();
        await using var _ = context;
        _db.AddFolder(teamId, "B");
        _db.AddFolder(teamId, "A");
        _db.AddFolder(_db.AddTeam(userId, "Other").Id, "Z");

        var folders = await service.ListAsync(teamId, default);

        Assert.Equal(["A", "B"], folders.Select(f => f.Name));
    }

    [Fact]
    public async Task ListAsync_NonMember_ThrowsNotFound()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        _db.CurrentUser.UserId = _db.AddUser().Id;

        await Assert.ThrowsAsync<NotFoundException>(() => service.ListAsync(teamId, default));
    }

    [Fact]
    public async Task UpdateAsync_Rename_ChangesName()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var folder = _db.AddFolder(teamId, "Old");

        var dto = await service.UpdateAsync(teamId, folder.Id, new UpdateFolderRequest { Name = "New" }, default);

        Assert.Equal("New", dto.Name);
    }

    [Fact]
    public async Task UpdateAsync_MoveUnderAnotherFolder_ChangesParent()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var a = _db.AddFolder(teamId, "A");
        var b = _db.AddFolder(teamId, "B");

        var dto = await service.UpdateAsync(teamId, b.Id, new UpdateFolderRequest { ParentId = a.Id }, default);

        Assert.Equal(a.Id, dto.ParentId);
    }

    [Fact]
    public async Task UpdateAsync_MoveToRoot_ClearsParent()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var a = _db.AddFolder(teamId, "A");
        var b = _db.AddFolder(teamId, "B", a.Id);

        var dto = await service.UpdateAsync(teamId, b.Id, new UpdateFolderRequest { MoveToRoot = true }, default);

        Assert.Null(dto.ParentId);
    }

    [Fact]
    public async Task UpdateAsync_NoChanges_LeavesFolderAsIs()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var a = _db.AddFolder(teamId, "A");
        var b = _db.AddFolder(teamId, "B", a.Id);

        var dto = await service.UpdateAsync(teamId, b.Id, new UpdateFolderRequest(), default);

        Assert.Equal(("B", a.Id), (dto.Name, dto.ParentId));
    }

    [Fact]
    public async Task UpdateAsync_MoveIntoOwnDescendant_ThrowsValidationError()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var a = _db.AddFolder(teamId, "A");
        var b = _db.AddFolder(teamId, "B", a.Id);
        var c = _db.AddFolder(teamId, "C", b.Id);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync(teamId, a.Id, new UpdateFolderRequest { ParentId = c.Id }, default));
    }

    [Fact]
    public async Task UpdateAsync_MoveIntoItself_ThrowsValidationError()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var a = _db.AddFolder(teamId, "A");

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync(teamId, a.Id, new UpdateFolderRequest { ParentId = a.Id }, default));
    }

    [Fact]
    public async Task UpdateAsync_UnknownParent_ThrowsValidationError()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;
        var a = _db.AddFolder(teamId, "A");

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync(teamId, a.Id, new UpdateFolderRequest { ParentId = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task UpdateAsync_UnknownFolder_ThrowsNotFound()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(teamId, Guid.NewGuid(), new UpdateFolderRequest { Name = "x" }, default));
    }

    [Fact]
    public async Task DeleteAsync_MovesSubfoldersAndNotesUpToParent()
    {
        var (service, context, teamId, userId) = Setup();
        await using var _ = context;
        var parent = _db.AddFolder(teamId, "Parent");
        var doomed = _db.AddFolder(teamId, "Doomed", parent.Id);
        var child = _db.AddFolder(teamId, "Child", doomed.Id);
        var note = _db.AddNote(teamId, userId, folderId: doomed.Id);
        var trashed = _db.AddNote(teamId, userId, "trashed", folderId: doomed.Id, deleted: true);

        await service.DeleteAsync(teamId, doomed.Id, default);

        await using var verify = _db.NewContext();
        Assert.DoesNotContain(await verify.Folders.ToListAsync(), f => f.Id == doomed.Id);
        Assert.Equal(parent.Id, (await verify.Folders.SingleAsync(f => f.Id == child.Id)).ParentId);
        var notes = await verify.Notes.IgnoreQueryFilters().ToListAsync();
        Assert.All(notes, n => Assert.Equal(parent.Id, n.FolderId));
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Id == trashed.Id);
        Assert.Contains(notes, n => n.Id == note.Id);
    }

    [Fact]
    public async Task DeleteAsync_TopLevelFolder_MovesContentsToRoot()
    {
        var (service, context, teamId, userId) = Setup();
        await using var _ = context;
        var doomed = _db.AddFolder(teamId, "Doomed");
        var note = _db.AddNote(teamId, userId, folderId: doomed.Id);

        await service.DeleteAsync(teamId, doomed.Id, default);

        await using var verify = _db.NewContext();
        Assert.Null((await verify.Notes.IgnoreQueryFilters().SingleAsync(n => n.Id == note.Id)).FolderId);
    }

    [Fact]
    public async Task DeleteAsync_UnknownFolder_ThrowsNotFound()
    {
        var (service, context, teamId, _) = Setup();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(teamId, Guid.NewGuid(), default));
    }
}

public class FoldersControllerTests
{
    [Fact]
    public async Task Actions_DelegateToService()
    {
        var service = Substitute.For<IFolderService>();
        var teamId = Guid.NewGuid();
        var folder = new FolderDto(Guid.NewGuid(), null, "F");
        IReadOnlyList<FolderDto> folders = [folder];
        service.ListAsync(teamId, Arg.Any<CancellationToken>()).Returns(folders);
        service.CreateAsync(teamId, Arg.Any<CreateFolderRequest>(), Arg.Any<CancellationToken>()).Returns(folder);
        service.UpdateAsync(teamId, folder.Id, Arg.Any<UpdateFolderRequest>(), Arg.Any<CancellationToken>()).Returns(folder with { Name = "G" });
        var controller = new FoldersController(service);

        Assert.Equal(folders, Assert.IsType<OkObjectResult>((await controller.List(teamId, default)).Result).Value);
        Assert.Equal(folder, Assert.IsType<CreatedAtActionResult>((await controller.Create(teamId, new CreateFolderRequest { Name = "F" }, default)).Result).Value);
        Assert.Equal("G", (await controller.Update(teamId, folder.Id, new UpdateFolderRequest { Name = "G" }, default)).Value!.Name);
        Assert.IsType<NoContentResult>(await controller.Delete(teamId, folder.Id, default));
        await service.Received(1).DeleteAsync(teamId, folder.Id, Arg.Any<CancellationToken>());
    }
}
