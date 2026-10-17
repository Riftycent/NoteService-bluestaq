using NoteService.Domain.Entities;
using NoteService.Domain.Enums;
using NoteService.Domain.Rules;

namespace NoteService.UnitTests.Domain;

public class MarkdownTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ToPlainText_EmptyInput_ReturnsEmpty(string? input, string expected) =>
        Assert.Equal(expected, MarkdownText.ToPlainText(input));

    [Fact]
    public void ToPlainText_Headings_RemovesHashes() =>
        Assert.Equal("Title\nSub", MarkdownText.ToPlainText("# Title\n### Sub"));

    [Fact]
    public void ToPlainText_Emphasis_RemovesMarkers() =>
        Assert.Equal("bold italic strike code", MarkdownText.ToPlainText("**bold** _italic_ ~~strike~~ `code`"));

    [Fact]
    public void ToPlainText_Links_KeepsLinkText() =>
        Assert.Equal("see docs here", MarkdownText.ToPlainText("see [docs](https://x.test/a) here"));

    [Fact]
    public void ToPlainText_Images_KeepsAltText() =>
        Assert.Equal("a diagram", MarkdownText.ToPlainText("![a diagram](img.png)"));

    [Fact]
    public void ToPlainText_ListsAndQuotes_RemovesPrefixes() =>
        Assert.Equal("one\ntwo\nthree\nquoted", MarkdownText.ToPlainText("- one\n* two\n1. three\n> quoted"));

    [Fact]
    public void ToPlainText_CodeFences_RemovesFenceLinesButKeepsCode() =>
        Assert.Equal("var x = 1;", MarkdownText.ToPlainText("```csharp\nvar x = 1;\n```"));

    [Fact]
    public void ToPlainText_HtmlTags_AreStripped() =>
        Assert.Equal("hello", MarkdownText.ToPlainText("<b>hello</b>"));

    [Fact]
    public void ToPlainText_ManyBlankLines_AreCollapsed() =>
        Assert.Equal("a\n\nb", MarkdownText.ToPlainText("a\n\n\n\n\nb"));
}

public class NoteAccessPolicyTests
{
    [Theory]
    [InlineData(TeamRole.Viewer, null, NoteAccess.Read)]
    [InlineData(TeamRole.Editor, null, NoteAccess.Edit)]
    [InlineData(TeamRole.Admin, null, NoteAccess.Edit)]
    [InlineData(TeamRole.Owner, null, NoteAccess.Edit)]
    public void Resolve_NoShare_UsesRoleBaseline(TeamRole role, SharePermission? share, NoteAccess expected) =>
        Assert.Equal(expected, NoteAccessPolicy.Resolve(role, share));

    [Fact]
    public void Resolve_EditShareOnViewer_Elevates() =>
        Assert.Equal(NoteAccess.Edit, NoteAccessPolicy.Resolve(TeamRole.Viewer, SharePermission.Edit));

    [Fact]
    public void Resolve_ReadShareOnEditor_Restricts() =>
        Assert.Equal(NoteAccess.Read, NoteAccessPolicy.Resolve(TeamRole.Editor, SharePermission.Read));

    [Theory]
    [InlineData(TeamRole.Admin)]
    [InlineData(TeamRole.Owner)]
    public void Resolve_ReadShareOnAdminOrOwner_DoesNotRestrict(TeamRole role) =>
        Assert.Equal(NoteAccess.Edit, NoteAccessPolicy.Resolve(role, SharePermission.Read));
}

public class MembershipRulesTests
{
    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Owner, true)]
    [InlineData(TeamRole.Owner, TeamRole.Admin, true)]
    [InlineData(TeamRole.Admin, TeamRole.Editor, true)]
    [InlineData(TeamRole.Admin, TeamRole.Viewer, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, false)]
    [InlineData(TeamRole.Admin, TeamRole.Owner, false)]
    [InlineData(TeamRole.Editor, TeamRole.Viewer, false)]
    [InlineData(TeamRole.Viewer, TeamRole.Viewer, false)]
    public void CanInvite_ReflectsActorAuthority(TeamRole actor, TeamRole invited, bool expected) =>
        Assert.Equal(expected, MembershipRules.CanInvite(actor, invited));

    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Owner, TeamRole.Viewer, true)]
    [InlineData(TeamRole.Owner, TeamRole.Viewer, TeamRole.Owner, true)]
    [InlineData(TeamRole.Admin, TeamRole.Viewer, TeamRole.Editor, true)]
    [InlineData(TeamRole.Admin, TeamRole.Editor, TeamRole.Admin, false)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, TeamRole.Viewer, false)]
    [InlineData(TeamRole.Admin, TeamRole.Owner, TeamRole.Viewer, false)]
    [InlineData(TeamRole.Editor, TeamRole.Viewer, TeamRole.Editor, false)]
    public void CanChangeRole_ReflectsActorAuthority(TeamRole actor, TeamRole current, TeamRole next, bool expected) =>
        Assert.Equal(expected, MembershipRules.CanChangeRole(actor, current, next));

    [Theory]
    [InlineData(TeamRole.Viewer, TeamRole.Owner, true, true)]
    [InlineData(TeamRole.Viewer, TeamRole.Viewer, false, false)]
    [InlineData(TeamRole.Editor, TeamRole.Viewer, false, false)]
    [InlineData(TeamRole.Admin, TeamRole.Editor, false, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, false, false)]
    [InlineData(TeamRole.Owner, TeamRole.Admin, false, true)]
    public void CanRemove_AllowsSelfAndAuthorizedRemoval(TeamRole actor, TeamRole target, bool isSelf, bool expected) =>
        Assert.Equal(expected, MembershipRules.CanRemove(actor, target, isSelf));
}

public class VersionThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static NoteVersion Version(Guid editor, DateTimeOffset at) =>
        new() { EditedBy = editor, CreatedAt = at };

    [Fact]
    public void ShouldSnapshot_NoPreviousVersion_ReturnsTrue() =>
        Assert.True(VersionThrottle.ShouldSnapshot(null, Guid.NewGuid(), Now, Interval));

    [Fact]
    public void ShouldSnapshot_DifferentEditor_ReturnsTrue() =>
        Assert.True(VersionThrottle.ShouldSnapshot(Version(Guid.NewGuid(), Now), Guid.NewGuid(), Now, Interval));

    [Fact]
    public void ShouldSnapshot_SameEditorWithinInterval_ReturnsFalse()
    {
        var editor = Guid.NewGuid();
        Assert.False(VersionThrottle.ShouldSnapshot(Version(editor, Now.AddMinutes(-4)), editor, Now, Interval));
    }

    [Fact]
    public void ShouldSnapshot_SameEditorAtInterval_ReturnsTrue()
    {
        var editor = Guid.NewGuid();
        Assert.True(VersionThrottle.ShouldSnapshot(Version(editor, Now.AddMinutes(-5)), editor, Now, Interval));
    }

    [Fact]
    public void ShouldSnapshot_ZeroInterval_AlwaysSnapshots()
    {
        var editor = Guid.NewGuid();
        Assert.True(VersionThrottle.ShouldSnapshot(Version(editor, Now), editor, Now, TimeSpan.Zero));
    }
}
