using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Features.Notes;
using NoteService.API.Features.Tags;
using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Tags;

public class TagServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (TagService Service, NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        var teamAccess = new TeamAccess(context, _db.CurrentUser);
        return (new TagService(context, teamAccess, new NoteAccessService(context, teamAccess, _db.CurrentUser)), context);
    }

    private (Team Team, ApplicationUser Owner) Seed()
    {
        var owner = _db.AddUser();
        var team = _db.AddTeam(owner.Id);
        _db.CurrentUser.UserId = owner.Id;
        return (team, owner);
    }

    [Fact]
    public async Task CreateAsync_NormalizesName()
    {
        var (team, _) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        var dto = await service.CreateAsync(team.Id, "  Urgent ", default);

        Assert.Equal(("urgent", 0), (dto.Name, dto.NoteCount));
    }

    [Fact]
    public async Task CreateAsync_DuplicateIgnoringCase_ThrowsConflict()
    {
        var (team, _) = Seed();
        _db.AddTag(team.Id, "bug");
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(team.Id, "BUG", default));
    }

    [Fact]
    public async Task CreateAsync_BlankName_ThrowsValidationError()
    {
        var (team, _) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.CreateAsync(team.Id, " ", default));
    }

    [Fact]
    public async Task ListAsync_ReturnsTagsWithUsageCounts()
    {
        var (team, owner) = Seed();
        var used = _db.AddTag(team.Id, "used");
        _db.AddTag(team.Id, "unused");
        var note = _db.AddNote(team.Id, owner.Id);
        await using (var seed = _db.NewContext())
        {
            seed.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = used.Id });
            await seed.SaveChangesAsync();
        }

        var (service, context) = Create();
        await using var _ = context;

        var tags = await service.ListAsync(team.Id, default);

        Assert.Equal([("unused", 0), ("used", 1)], tags.Select(t => (t.Name, t.NoteCount)));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTagAndItsLinks()
    {
        var (team, owner) = Seed();
        var tag = _db.AddTag(team.Id);
        var note = _db.AddNote(team.Id, owner.Id);
        await using (var seed = _db.NewContext())
        {
            seed.NoteTags.Add(new NoteTag { NoteId = note.Id, TagId = tag.Id });
            await seed.SaveChangesAsync();
        }

        var (service, context) = Create();
        await using var _ = context;

        await service.DeleteAsync(team.Id, tag.Id, default);

        await using var verify = _db.NewContext();
        Assert.Empty(await verify.Tags.ToListAsync());
        Assert.Empty(await verify.NoteTags.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_UnknownTag_ThrowsNotFound()
    {
        var (team, _) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(team.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task AddToNoteAsync_LinksTagAndIsIdempotent()
    {
        var (team, owner) = Seed();
        var tag = _db.AddTag(team.Id, "bug");
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = Create();
        await using var _ = context;

        await service.AddToNoteAsync(team.Id, note.Id, tag.Id, default);
        var tags = await service.AddToNoteAsync(team.Id, note.Id, tag.Id, default);

        Assert.Equal([new TagRefDto(tag.Id, "bug")], tags);
    }

    [Fact]
    public async Task AddToNoteAsync_TagFromAnotherTeam_ThrowsNotFound()
    {
        var (team, owner) = Seed();
        var foreign = _db.AddTag(_db.AddTeam(owner.Id, "Other").Id, "foreign");
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.AddToNoteAsync(team.Id, note.Id, foreign.Id, default));
    }

    [Fact]
    public async Task AddToNoteAsync_ViewerWithoutEditAccess_IsForbidden()
    {
        var (team, owner) = Seed();
        var viewer = _db.AddUser();
        _db.AddMember(team.Id, viewer.Id, TeamRole.Viewer);
        var tag = _db.AddTag(team.Id);
        var note = _db.AddNote(team.Id, owner.Id);
        _db.CurrentUser.UserId = viewer.Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ForbiddenException>(() => service.AddToNoteAsync(team.Id, note.Id, tag.Id, default));
    }

    [Fact]
    public async Task RemoveFromNoteAsync_UnlinksTagAndReturnsRemainingTags()
    {
        var (team, owner) = Seed();
        var keep = _db.AddTag(team.Id, "keep");
        var drop = _db.AddTag(team.Id, "drop");
        var note = _db.AddNote(team.Id, owner.Id);
        var (service, context) = Create();
        await using var _ = context;
        await service.AddToNoteAsync(team.Id, note.Id, keep.Id, default);
        await service.AddToNoteAsync(team.Id, note.Id, drop.Id, default);

        var remaining = await service.RemoveFromNoteAsync(team.Id, note.Id, drop.Id, default);

        Assert.Equal(["keep"], remaining.Select(t => t.Name));
    }
}

public class TagControllersTests
{
    [Fact]
    public async Task TagsController_Actions_DelegateToService()
    {
        var service = Substitute.For<ITagService>();
        var teamId = Guid.NewGuid();
        var tag = new TagDto(Guid.NewGuid(), "bug", 0);
        IReadOnlyList<TagDto> tags = [tag];
        service.ListAsync(teamId, Arg.Any<CancellationToken>()).Returns(tags);
        service.CreateAsync(teamId, "bug", Arg.Any<CancellationToken>()).Returns(tag);
        var controller = new TagsController(service);

        Assert.Equal(tags, Assert.IsType<OkObjectResult>((await controller.List(teamId, default)).Result).Value);
        Assert.Equal(tag, Assert.IsType<CreatedAtActionResult>((await controller.Create(teamId, new CreateTagRequest { Name = "bug" }, default)).Result).Value);
        Assert.IsType<NoContentResult>(await controller.Delete(teamId, tag.Id, default));
        await service.Received(1).DeleteAsync(teamId, tag.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoteTagsController_AddAndRemove_ReturnNoteTags()
    {
        var service = Substitute.For<ITagService>();
        var (teamId, noteId, tagId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        IReadOnlyList<TagRefDto> after = [new TagRefDto(tagId, "bug")];
        service.AddToNoteAsync(teamId, noteId, tagId, Arg.Any<CancellationToken>()).Returns(after);
        service.RemoveFromNoteAsync(teamId, noteId, tagId, Arg.Any<CancellationToken>()).Returns([]);
        var controller = new NoteTagsController(service);

        Assert.Equal(after, Assert.IsType<OkObjectResult>((await controller.Add(teamId, noteId, tagId, default)).Result).Value);
        Assert.Empty((IReadOnlyList<TagRefDto>)Assert.IsType<OkObjectResult>((await controller.Remove(teamId, noteId, tagId, default)).Result).Value!);
    }
}
