using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NoteService.API.Common;
using NoteService.API.Features.Attachments;
using NoteService.API.Features.Folders;
using NoteService.API.Features.Notes;
using NoteService.API.Features.Search;
using NoteService.API.Features.Shares;
using NoteService.API.Features.Tags;
using NoteService.API.Features.Versions;
using NoteService.Domain.Enums;
using NoteService.IntegrationTests.TestSupport;

namespace NoteService.IntegrationTests;

[Collection(ApiCollection.Name)]
public class NotesApiTests(ApiFactory factory)
{
    private static string NotesUrl(Guid teamId) => $"/api/v1/teams/{teamId}/notes";

    private static string NoteUrl(Guid teamId, Guid noteId) => $"{NotesUrl(teamId)}/{noteId}";

    private async Task<(ApiClient Owner, Guid TeamId)> StartAsync()
    {
        var owner = await ApiClient.RegisterAsync(factory, "owner");
        var team = await owner.CreateTeamAsync();
        return (owner, team.Id);
    }

    [Fact]
    public async Task CreateAndGet_ReturnETagHeaderMatchingBody()
    {
        var (owner, teamId) = await StartAsync();

        var created = await owner.PostAsync(NotesUrl(teamId), new CreateNoteRequest { Title = "Plan", Content = "# Hello" });
        await ApiClient.AssertStatusAsync(HttpStatusCode.Created, created);
        var note = await owner.ReadAsync<NoteDto>(created);
        var fetched = await owner.GetAsync(NoteUrl(teamId, note.Id));

        Assert.Equal(note.ETag, created.Headers.ETag!.Tag);
        Assert.Equal(note.ETag, fetched.Headers.ETag!.Tag);
        Assert.Equal(created.Headers.Location!.AbsolutePath, NoteUrl(teamId, note.Id));
    }

    [Fact]
    public async Task Patch_WithoutIfMatch_Returns428()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId);

        var response = await owner.PatchAsync(NoteUrl(teamId, note.Id), new UpdateNoteRequest { Title = "x" });

        await ApiClient.AssertStatusAsync(HttpStatusCode.PreconditionRequired, response);
    }

    [Fact]
    public async Task Patch_WithCurrentETag_Succeeds_AndStaleETagIsRejectedWithCurrentOne()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId, "v1");

        var updated = await owner.PatchAsync(NoteUrl(teamId, note.Id), new UpdateNoteRequest { Title = "v2" }, note.ETag);
        var updatedNote = await owner.ReadAsync<NoteDto>(updated);
        Assert.Equal("v2", updatedNote.Title);
        Assert.NotEqual(note.ETag, updatedNote.ETag);

        var stale = await owner.PatchAsync(NoteUrl(teamId, note.Id), new UpdateNoteRequest { Title = "v3" }, note.ETag);

        await ApiClient.AssertStatusAsync(HttpStatusCode.PreconditionFailed, stale);
        using var problem = System.Text.Json.JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(updatedNote.ETag, problem.RootElement.GetProperty("currentETag").GetString());
    }

    [Fact]
    public async Task ConcurrentEditsWithTheSameETag_ExactlyOneWins()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            owner.PatchAsync(NoteUrl(teamId, note.Id), new UpdateNoteRequest { Content = $"writer {i}" }, note.ETag)));

        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(5, attempts.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
    }

    [Fact]
    public async Task Delete_MovesToTrash_ThenRestoreBringsItBack()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId, "To trash");

        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent, await owner.DeleteAsync(NoteUrl(teamId, note.Id), note.ETag));
        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await owner.GetAsync(NoteUrl(teamId, note.Id)));
        var trash = await owner.ReadAsync<List<NoteSummaryDto>>(await owner.GetAsync($"/api/v1/teams/{teamId}/trash"));
        Assert.Contains(trash, n => n.Id == note.Id);

        var restored = await owner.ReadAsync<NoteDto>(await owner.PostAsync($"{NoteUrl(teamId, note.Id)}/restore", new { }));
        Assert.Null(restored.DeletedAt);
        await ApiClient.AssertStatusAsync(HttpStatusCode.OK, await owner.GetAsync(NoteUrl(teamId, note.Id)));
    }

    [Fact]
    public async Task Delete_WithoutIfMatch_Returns428()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId);

        await ApiClient.AssertStatusAsync(HttpStatusCode.PreconditionRequired, await owner.DeleteAsync(NoteUrl(teamId, note.Id)));
    }

    [Fact]
    public async Task KeysetPagination_OnRealSqlServer_WalksEveryNoteExactlyOnce()
    {
        var (owner, teamId) = await StartAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            created.Add((await owner.CreateNoteAsync(teamId, $"note {i:00}")).Id);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"{NotesUrl(teamId)}?limit=4" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await owner.ReadAsync<Page<NoteSummaryDto>>(await owner.GetAsync(url));
            seen.AddRange(page.Items.Select(n => n.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(created.Order(), seen.Order());
    }

    [Fact]
    public async Task List_SortByTitleAndFilterByTag()
    {
        var (owner, teamId) = await StartAsync();
        var a = await owner.CreateNoteAsync(teamId, "bravo");
        var b = await owner.CreateNoteAsync(teamId, "alpha");
        var tag = await owner.ReadAsync<TagDto>(await owner.PostAsync($"/api/v1/teams/{teamId}/tags", new CreateTagRequest { Name = "Release" }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.OK, await owner.PutAsync($"{NoteUrl(teamId, a.Id)}/tags/{tag.Id}", new { }));

        var sorted = await owner.ReadAsync<Page<NoteSummaryDto>>(await owner.GetAsync($"{NotesUrl(teamId)}?sort=title"));
        var tagged = await owner.ReadAsync<Page<NoteSummaryDto>>(await owner.GetAsync($"{NotesUrl(teamId)}?tag=release"));

        Assert.Equal(["alpha", "bravo"], sorted.Items.Select(n => n.Title));
        Assert.Equal([a.Id], tagged.Items.Select(n => n.Id));
        Assert.Equal(["release"], tagged.Items.Single().Tags);
        _ = b;
    }

    [Fact]
    public async Task List_InvalidQuery_Returns400()
    {
        var (owner, teamId) = await StartAsync();

        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, await owner.GetAsync($"{NotesUrl(teamId)}?limit=0"));
        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, await owner.GetAsync($"{NotesUrl(teamId)}?cursor=garbage"));
    }

    [Fact]
    public async Task Folders_NestMoveAndDeleteKeepNotes()
    {
        var (owner, teamId) = await StartAsync();
        var parent = await owner.ReadAsync<FolderDto>(await owner.PostAsync($"/api/v1/teams/{teamId}/folders", new CreateFolderRequest { Name = "Parent" }));
        var child = await owner.ReadAsync<FolderDto>(await owner.PostAsync($"/api/v1/teams/{teamId}/folders", new CreateFolderRequest { Name = "Child", ParentId = parent.Id }));
        var note = await owner.CreateNoteAsync(teamId, "inside", "c", child.Id);

        var cycle = await owner.PatchAsync($"/api/v1/teams/{teamId}/folders/{parent.Id}", new UpdateFolderRequest { ParentId = child.Id });
        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, cycle);

        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent, await owner.DeleteAsync($"/api/v1/teams/{teamId}/folders/{child.Id}"));
        var moved = await owner.ReadAsync<NoteDto>(await owner.GetAsync(NoteUrl(teamId, note.Id)));
        Assert.Equal(parent.Id, moved.FolderId);
    }

    [Fact]
    public async Task VersionHistory_ListsAndRestoresOldContent()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId, "Title", "first draft");

        // Same editor within the throttle window does not add history, so restore the initial version later.
        var edited = await owner.ReadAsync<NoteDto>(
            await owner.PatchAsync(NoteUrl(teamId, note.Id), new UpdateNoteRequest { Content = "second draft" }, note.ETag));

        var versions = await owner.ReadAsync<List<NoteVersionSummaryDto>>(await owner.GetAsync($"{NoteUrl(teamId, note.Id)}/versions"));
        Assert.Equal([1], versions.Select(v => v.VersionNo));
        var version = await owner.ReadAsync<NoteVersionDto>(await owner.GetAsync($"{NoteUrl(teamId, note.Id)}/versions/1"));
        Assert.Equal("first draft", version.Content);

        var restore = await owner.Send(HttpMethod.Post, $"{NoteUrl(teamId, note.Id)}/versions/1/restore", null, edited.ETag);
        var restored = await owner.ReadAsync<NoteDto>(restore);
        Assert.Equal("first draft", restored.Content);

        var after = await owner.ReadAsync<List<NoteVersionSummaryDto>>(await owner.GetAsync($"{NoteUrl(teamId, note.Id)}/versions"));
        Assert.Equal([2, 1], after.Select(v => v.VersionNo));
    }

    [Fact]
    public async Task Sharing_EditShareLetsAViewerEdit_ReadShareRestrictsAnEditor()
    {
        var (owner, teamId) = await StartAsync();
        var viewer = await ApiClient.RegisterAsync(factory, "viewer");
        var editor = await ApiClient.RegisterAsync(factory, "editor");
        await owner.AddMemberAsync(teamId, viewer, TeamRole.Viewer, factory);
        await owner.AddMemberAsync(teamId, editor, TeamRole.Editor, factory);
        var note = await owner.CreateNoteAsync(teamId, "Shared note");

        await ApiClient.AssertStatusAsync(HttpStatusCode.OK,
            await owner.PutAsync($"{NoteUrl(teamId, note.Id)}/shares/{viewer.UserId}", new SetShareRequest { Permission = SharePermission.Edit }));
        await ApiClient.AssertStatusAsync(HttpStatusCode.OK,
            await owner.PutAsync($"{NoteUrl(teamId, note.Id)}/shares/{editor.UserId}", new SetShareRequest { Permission = SharePermission.Read }));

        var viewerEdit = await viewer.PatchAsync(NoteUrl(teamId, note.Id), new UpdateNoteRequest { Content = "edited by viewer" }, note.ETag);
        var viewerResult = await viewer.ReadAsync<NoteDto>(viewerEdit);
        Assert.Equal("edited by viewer", viewerResult.Content);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden,
            await editor.PatchAsync(NoteUrl(teamId, note.Id), new UpdateNoteRequest { Content = "nope" }, viewerResult.ETag));

        var shares = await owner.ReadAsync<List<ShareDto>>(await owner.GetAsync($"{NoteUrl(teamId, note.Id)}/shares"));
        Assert.Equal(2, shares.Count);
        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent, await owner.DeleteAsync($"{NoteUrl(teamId, note.Id)}/shares/{editor.UserId}"));
    }

    [Fact]
    public async Task Sharing_OnlyAuthorOrAdminCanManageShares()
    {
        var (owner, teamId) = await StartAsync();
        var editor = await ApiClient.RegisterAsync(factory, "editor");
        await owner.AddMemberAsync(teamId, editor, TeamRole.Editor, factory);
        var note = await owner.CreateNoteAsync(teamId);

        var response = await editor.GetAsync($"{NoteUrl(teamId, note.Id)}/shares");

        await ApiClient.AssertStatusAsync(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task Tags_CreateDeduplicateAttachAndDelete()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId);
        var tag = await owner.ReadAsync<TagDto>(await owner.PostAsync($"/api/v1/teams/{teamId}/tags", new CreateTagRequest { Name = "  Urgent " }));
        Assert.Equal("urgent", tag.Name);

        await ApiClient.AssertStatusAsync(HttpStatusCode.Conflict, await owner.PostAsync($"/api/v1/teams/{teamId}/tags", new CreateTagRequest { Name = "URGENT" }));

        var attached = await owner.ReadAsync<List<TagRefDto>>(await owner.PutAsync($"{NoteUrl(teamId, note.Id)}/tags/{tag.Id}", new { }));
        Assert.Equal([tag.Id], attached.Select(t => t.Id));
        var listed = await owner.ReadAsync<List<TagDto>>(await owner.GetAsync($"/api/v1/teams/{teamId}/tags"));
        Assert.Equal(1, listed.Single().NoteCount);

        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent, await owner.DeleteAsync($"/api/v1/teams/{teamId}/tags/{tag.Id}"));
        var fetched = await owner.ReadAsync<NoteDto>(await owner.GetAsync(NoteUrl(teamId, note.Id)));
        Assert.Empty(fetched.Tags);
    }

    [Fact]
    public async Task Attachments_UploadDownloadAndDelete()
    {
        var (owner, teamId) = await StartAsync();
        var note = await owner.CreateNoteAsync(teamId);
        var bytes = "hello attachment"u8.ToArray();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "..\\..\\notes.txt");

        var upload = await owner.Send(HttpMethod.Post, $"{NoteUrl(teamId, note.Id)}/attachments", form);
        await ApiClient.AssertStatusAsync(HttpStatusCode.Created, upload);
        var attachment = await owner.ReadAsync<AttachmentDto>(upload);
        Assert.Equal("notes.txt", attachment.FileName);

        var download = await owner.GetAsync($"/api/v1/teams/{teamId}/attachments/{attachment.Id}");
        await ApiClient.AssertStatusAsync(HttpStatusCode.OK, download);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());

        var listed = await owner.ReadAsync<List<AttachmentDto>>(await owner.GetAsync($"{NoteUrl(teamId, note.Id)}/attachments"));
        Assert.Single(listed);

        await ApiClient.AssertStatusAsync(HttpStatusCode.NoContent, await owner.DeleteAsync($"/api/v1/teams/{teamId}/attachments/{attachment.Id}"));
        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await owner.GetAsync($"/api/v1/teams/{teamId}/attachments/{attachment.Id}"));
    }

    [Fact]
    public async Task Attachments_DisallowedTypeIsRejected_AndOtherTeamsCannotDownload()
    {
        var (owner, teamId) = await StartAsync();
        var outsider = await ApiClient.RegisterAsync(factory, "outsider");
        var note = await owner.CreateNoteAsync(teamId);

        using var bad = new MultipartFormDataContent();
        var exe = new ByteArrayContent([1, 2, 3]);
        exe.Headers.ContentType = new MediaTypeHeaderValue("application/x-msdownload");
        bad.Add(exe, "file", "virus.exe");
        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, await owner.Send(HttpMethod.Post, $"{NoteUrl(teamId, note.Id)}/attachments", bad));

        using var good = new MultipartFormDataContent();
        var txt = new ByteArrayContent([1, 2, 3]);
        txt.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        good.Add(txt, "file", "ok.txt");
        var attachment = await owner.ReadAsync<AttachmentDto>(await owner.Send(HttpMethod.Post, $"{NoteUrl(teamId, note.Id)}/attachments", good));

        await ApiClient.AssertStatusAsync(HttpStatusCode.NotFound, await outsider.GetAsync($"/api/v1/teams/{teamId}/attachments/{attachment.Id}"));
    }

    [Fact]
    public async Task Search_FindsNotesByTitleAndBody_WithinTheTeamOnly()
    {
        var (owner, teamId) = await StartAsync();
        var otherTeam = await owner.CreateTeamAsync("Other");
        await owner.CreateNoteAsync(teamId, "Quarterly roadmap", "plans");
        await owner.CreateNoteAsync(teamId, "Meeting", "discuss the roadmap");
        await owner.CreateNoteAsync(teamId, "Unrelated", "groceries");
        await owner.CreateNoteAsync(otherTeam.Id, "Other roadmap", "elsewhere");

        var results = await owner.ReadAsync<List<SearchResultDto>>(await owner.GetAsync($"/api/v1/teams/{teamId}/search?q=roadmap"));

        Assert.Equal(["Meeting", "Quarterly roadmap"], results.Select(r => r.Title).Order());
        await ApiClient.AssertStatusAsync(HttpStatusCode.BadRequest, await owner.GetAsync($"/api/v1/teams/{teamId}/search?q=a"));
    }
}
