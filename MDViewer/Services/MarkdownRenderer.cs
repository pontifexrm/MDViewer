using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
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
    /// The task-list checkboxes Markdig renders are disabled, which is right for the
    /// exports and print but not for reading: there they are a to-do list. This is
    /// the on-screen HTML with them switched on; clicks are caught in wwwroot/tasks.js.
    /// </summary>
    public static string WithClickableTasks(string html) =>
        html.Replace("<input disabled=\"disabled\" type=\"checkbox\"", "<input type=\"checkbox\"");

    /// <summary>
    /// The markdown with the index'th task-list item ticked or unticked, counting in
    /// document order — the same order the checkboxes appear on screen. Only the one
    /// character inside the brackets changes, so the rest of the file is untouched.
    /// Null when there is no such task, or it is not in the state the caller saw
    /// (the page was stale), so a click never flips the wrong box.
    /// </summary>
    public static string? ToggleTask(string markdown, int index, bool wasChecked)
    {
        if (index < 0) return null;

        var task = Markdown.Parse(markdown, Pipeline)
            .Descendants<TaskList>()
            .ElementAtOrDefault(index);
        if (task is null || task.Checked != wasChecked) return null;

        // The span covers "[ ]" / "[x]"; the mark sits one past its start.
        var mark = task.Span.Start + 1;
        if (mark >= markdown.Length || markdown[task.Span.Start] != '[') return null;

        return string.Concat(markdown.AsSpan(0, mark), wasChecked ? " " : "x", markdown.AsSpan(mark + 1));
    }

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
