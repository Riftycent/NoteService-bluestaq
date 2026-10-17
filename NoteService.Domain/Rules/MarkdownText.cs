using System.Text.RegularExpressions;

namespace NoteService.Domain.Rules;

/// <summary>Reduces Markdown to searchable plain text. Intentionally lightweight; it is not a full Markdown parser.</summary>
public static partial class MarkdownText
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown.Replace("\r\n", "\n");
        text = FenceRegex().Replace(text, string.Empty);
        text = ImageRegex().Replace(text, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = LinePrefixRegex().Replace(text, string.Empty);
        text = EmphasisRegex().Replace(text, string.Empty);
        text = BlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"^\s*(```|~~~).*$", RegexOptions.Multiline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^[ \t]*(#{1,6}[ \t]+|>[ \t]?|[-*+][ \t]+|\d+\.[ \t]+)", RegexOptions.Multiline)]
    private static partial Regex LinePrefixRegex();

    [GeneratedRegex(@"(\*\*|__|\*|_|~~|`)")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
