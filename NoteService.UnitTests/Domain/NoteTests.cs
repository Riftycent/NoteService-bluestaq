using NoteService.Domain.Entities;
using NoteService.Domain.Exceptions;

namespace NoteService.UnitTests.Domain;

public class NoteTests
{
    private static readonly Guid TeamId = Guid.NewGuid();

    [Fact]
    public void Create_ValidInput_SetsFieldsAndPlainText()
    {
        var folderId = Guid.NewGuid();

        var note = Note.Create(TeamId, folderId, "  Plan  ", "# Heading\nSome **bold** text");

        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.Equal(TeamId, note.TeamId);
        Assert.Equal(folderId, note.FolderId);
        Assert.Equal("Plan", note.Title);
        Assert.Equal("# Heading\nSome **bold** text", note.ContentMarkdown);
        Assert.Equal("Heading\nSome bold text", note.ContentText);
        Assert.False(note.IsDeleted);
    }

    [Fact]
    public void Create_NullContent_StoresEmptyContent()
    {
        var note = Note.Create(TeamId, null, "Title", null);

        Assert.Equal(string.Empty, note.ContentMarkdown);
        Assert.Equal(string.Empty, note.ContentText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetContent_BlankTitle_Throws(string title)
    {
        var note = Note.Create(TeamId, null, "Title", "x");

        var ex = Assert.Throws<ValidationFailedException>(() => note.SetContent(title, "y"));

        Assert.Contains("title", ex.Errors.Keys);
    }

    [Fact]
    public void SetContent_TitleTooLong_Throws()
    {
        var note = Note.Create(TeamId, null, "Title", "x");

        Assert.Throws<ValidationFailedException>(() => note.SetContent(new string('a', Note.TitleMaxLength + 1), "y"));
    }

    [Fact]
    public void SetContent_TitleAtLimit_Succeeds()
    {
        var note = Note.Create(TeamId, null, "Title", "x");

        note.SetContent(new string('a', Note.TitleMaxLength), "y");

        Assert.Equal(Note.TitleMaxLength, note.Title.Length);
    }

    [Fact]
    public void SetContent_ContentTooLong_Throws()
    {
        var note = Note.Create(TeamId, null, "Title", "x");

        var ex = Assert.Throws<ValidationFailedException>(() => note.SetContent("Title", new string('a', Note.ContentMaxLength + 1)));

        Assert.Contains("content", ex.Errors.Keys);
    }

    [Fact]
    public void SetContent_NewContent_RefreshesPlainText()
    {
        var note = Note.Create(TeamId, null, "Title", "old");

        note.SetContent("New", "[link](http://x.test) text");

        Assert.Equal("New", note.Title);
        Assert.Equal("link text", note.ContentText);
    }

    [Fact]
    public void MoveToFolder_SetsAndClearsFolder()
    {
        var note = Note.Create(TeamId, null, "Title", "x");
        var folderId = Guid.NewGuid();

        note.MoveToFolder(folderId);
        Assert.Equal(folderId, note.FolderId);

        note.MoveToFolder(null);
        Assert.Null(note.FolderId);
    }

    [Fact]
    public void SetPinned_TogglesFlag()
    {
        var note = Note.Create(TeamId, null, "Title", "x");

        note.SetPinned(true);
        Assert.True(note.IsPinned);

        note.SetPinned(false);
        Assert.False(note.IsPinned);
    }

    [Fact]
    public void SoftDelete_SetsDeletedAt()
    {
        var note = Note.Create(TeamId, null, "Title", "x");
        var now = DateTimeOffset.UtcNow;

        note.SoftDelete(now);

        Assert.True(note.IsDeleted);
        Assert.Equal(now, note.DeletedAt);
    }

    [Fact]
    public void SoftDelete_CalledTwice_KeepsFirstTimestamp()
    {
        var note = Note.Create(TeamId, null, "Title", "x");
        var first = DateTimeOffset.UtcNow;

        note.SoftDelete(first);
        note.SoftDelete(first.AddHours(1));

        Assert.Equal(first, note.DeletedAt);
    }

    [Fact]
    public void Restore_ClearsDeletedAt()
    {
        var note = Note.Create(TeamId, null, "Title", "x");
        note.SoftDelete(DateTimeOffset.UtcNow);

        note.Restore();

        Assert.False(note.IsDeleted);
        Assert.Null(note.DeletedAt);
    }
}
