using Markdig;
using Microsoft.AspNetCore.Components;

namespace MDViewer.Services;

/// <summary>
/// Markdown to HTML, using the same pipeline as our internal knowledge base's
/// renderer, so a file renders here exactly as it would after being pasted into
/// a KB article.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // tables, task lists, footnotes, etc.
        .DisableHtml()             // don't pass raw HTML through (safety)
        .Build();

    /// <summary>Converts markdown to an HTML string.</summary>
    public static string ToHtmlString(string? markdown) =>
        Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    /// <summary>Converts markdown to a MarkupString safe for @((MarkupString)...).</summary>
    public static MarkupString ToHtml(string? markdown) =>
        new(ToHtmlString(markdown));

    /// <summary>
    /// The document's own leading title: the text of a "# Heading" that opens the
    /// file (YAML front matter skipped), else null. Deliberately only matches a
    /// *leading* heading — the exports use this both to name the document and to
    /// decide whether to add a title of their own, and a heading found halfway down
    /// the file is neither.
    /// </summary>
    public static string? LeadingTitle(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        // Skip YAML front matter (--- ... ---), which many generators emit.
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            i = 1;
            while (i < lines.Length && lines[i].Trim() != "---") i++;
            i++; // step past the closing fence
        }

        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (!line.StartsWith('#')) return null;   // content starts with something else

            var text = line.TrimStart('#').Trim();
            return text.Length > 0 ? text : null;
        }
        return null;
    }
}
