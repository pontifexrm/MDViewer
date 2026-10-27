using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

// Microsoft.Maui also defines IElement and IText, and MAUI's implicit usings pull them in.
using IElement = AngleSharp.Dom.IElement;
using IText = AngleSharp.Dom.IText;

namespace MDViewer.Services;

/// <summary>What a smart paste produced.</summary>
/// <param name="ImagesDropped">
/// Images left out because they have nowhere to point: a data: URI, an email's cid:
/// attachment, or the temp file Word copies alongside the text.
/// </param>
public sealed record PastedMarkdown(string Markdown, int ImagesDropped);

/// <summary>
/// Turns the HTML that Word, Outlook, Google Docs and browsers put on the clipboard
/// into markdown for the editor.
///
/// It runs in two passes. <see cref="Prepare"/> rewrites each source's quirks into
/// plain semantic HTML — Word's lists are styled paragraphs rather than ul/ol, and
/// Google Docs expresses bold as a span's font-weight inside a &lt;b&gt; that says it
/// is <em>not</em> bold. The writer then only has to understand ordinary HTML.
///
/// The output has to be pure markdown: the renderer runs with <c>DisableHtml()</c>,
/// so a leftover tag would show as literal text. Whatever markdown cannot express —
/// underline, colour, merged table cells, paragraphs inside a cell — is flattened
/// rather than kept as HTML.
///
/// Returns null when the HTML carries no structure worth converting. The caller then
/// lets the plain text through untouched, which matters most for code editors: VS
/// Code puts coloured HTML on the clipboard too, and converting someone's markdown
/// source would escape every # and * in it.
/// </summary>
public static class HtmlToMarkdown
{
    public static PastedMarkdown? Convert(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var doc = new HtmlParser().ParseDocument(html);
        if (doc.Body is not { } body) return null;

        Prepare(body);
        if (IsPreformattedSource(body)) return null;

        var writer = new Writer();
        var markdown = string.Join("\n\n", writer.Blocks(body, default));
        if (!writer.Structured || markdown.Length == 0) return null;

        return new PastedMarkdown(markdown, writer.ImagesDropped);
    }

    // ── Preparation: whatever the source wrote, down to semantic HTML ────────

    /// <summary>Removed along with their contents — the content is not text.</summary>
    private static readonly HashSet<string> Discard = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "link", "meta", "title", "head", "xml", "template", "noscript",
        "iframe", "object", "embed", "svg", "math", "canvas", "audio", "video",
        "button", "input", "select", "textarea",
    };

    private static void Prepare(IElement body)
    {
        // Comments go first. Word wraps its list markers in <![if !supportLists]>,
        // which HTML parses as a comment, and the clipboard's own
        // <!--StartFragment--> markers are comments too.
        foreach (var node in Descendants(body).ToList())
        {
            if (node is IComment || node is IElement el && Discard.Contains(el.LocalName))
                node.Parent?.RemoveChild(node);
        }

        WordLists(body);
        StylesToElements(body);
        MergeAdjacent(body);
    }

    private static readonly Regex WordLevel = new(@"level(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex OrderedMarker = new(@"^\(?(\d+|[a-zA-Z]|[ivxlcdmIVXLCDM]+)[.)]$");

    /// <summary>
    /// Word (and classic Outlook, which is Word underneath) writes a list as a run
    /// of paragraphs, each carrying <c>mso-list:l0 level2 lfo1</c> in its style and
    /// its bullet or number as literal text in a <c>mso-list:Ignore</c> span. Each
    /// run becomes a real nested ul/ol, ordered or not by what that marker looks like.
    /// </summary>
    private static void WordLists(IElement body)
    {
        var items = body.QuerySelectorAll("p").Where(p => WordListLevel(p) is not null).ToList();

        for (var i = 0; i < items.Count; i++)
        {
            var run = new List<IElement> { items[i] };
            while (i + 1 < items.Count && items[i + 1].PreviousElementSibling == items[i])
                run.Add(items[++i]);
            BuildList(run);
        }
    }

    private static void BuildList(List<IElement> run)
    {
        var doc = run[0].Owner!;
        var holder = doc.CreateElement("div");
        run[0].Parent!.InsertBefore(holder, run[0]);

        var open = new Stack<(int Level, IElement List)>();
        foreach (var p in run)
        {
            var level = WordListLevel(p)!.Value;
            var marker = TakeWordMarker(p);

            while (open.Count > 0 && open.Peek().Level > level) open.Pop();

            if (open.Count == 0 || open.Peek().Level < level)
            {
                var ordered = OrderedMarker.Match(marker);
                var list = doc.CreateElement(ordered.Success ? "ol" : "ul");
                if (ordered.Success && int.TryParse(ordered.Groups[1].Value, out var n) && n != 1)
                    list.SetAttribute("start", n.ToString(CultureInfo.InvariantCulture));

                // A deeper level hangs off the last item of the list above it.
                IElement parent = open.Count == 0
                    ? holder
                    : open.Peek().List.LastElementChild ?? open.Peek().List;
                parent.AppendChild(list);
                open.Push((level, list));
            }

            var item = doc.CreateElement("li");
            MoveChildren(p, item);
            open.Peek().List.AppendChild(item);
            p.Remove();
        }
    }

    private static int? WordListLevel(IElement p) =>
        Css(p).GetValueOrDefault("mso-list") is { } value && WordLevel.Match(value) is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Removes Word's literal bullet or number from a list paragraph and returns it.</summary>
    private static string TakeWordMarker(IElement p)
    {
        var marker = "";
        foreach (var span in p.QuerySelectorAll("*").Where(e => Css(e).GetValueOrDefault("mso-list") == "ignore").ToList())
        {
            if (marker.Length == 0) marker = Collapse(span.TextContent).Trim();
            span.Remove();
        }
        return marker;
    }

    /// <summary>Elements whose inline style is read for bold, italic and strikethrough.</summary>
    private static readonly HashSet<string> Restylable = new(StringComparer.OrdinalIgnoreCase)
    {
        "span", "font", "a", "p",
    };

    /// <summary>
    /// Expresses formatting as elements rather than CSS, and normalises the tag
    /// names, so the writer only ever sees strong, em and s.
    /// </summary>
    private static void StylesToElements(IElement body)
    {
        foreach (var original in body.QuerySelectorAll("*").ToList())
        {
            var el = original;
            var css = Css(el);
            var bold = Weight(css);
            bool? italic = css.GetValueOrDefault("font-style") is { } style
                ? style is "italic" or "oblique"
                : null;
            var strike = (css.GetValueOrDefault("text-decoration") ?? css.GetValueOrDefault("text-decoration-line"))
                ?.Contains("line-through") == true;

            if (el.LocalName == "p" && (el.ClassName ?? "").Contains("MsoTitle", StringComparison.Ordinal))
                el = Rename(el, "h1");

            // Google Docs wraps an entire copy in <b style="font-weight:normal">, so a
            // bold tag has to be checked against what its own style says.
            if (el.LocalName is "b" or "strong" && bold == false) el = Rename(el, "span");
            if (el.LocalName is "i" or "em" && italic == false) el = Rename(el, "span");

            if (Restylable.Contains(el.LocalName))
            {
                if (bold == true) WrapChildren(el, "strong");
                if (italic == true) WrapChildren(el, "em");
                if (strike) WrapChildren(el, "s");
            }

            switch (el.LocalName)
            {
                case "b": Rename(el, "strong"); break;
                case "i": Rename(el, "em"); break;
                case "strike" or "del": Rename(el, "s"); break;
            }
        }
    }

    private static bool? Weight(Dictionary<string, string> css)
    {
        if (css.GetValueOrDefault("font-weight") is not { } weight) return null;
        if (weight is "bold" or "bolder") return true;
        if (weight is "normal" or "lighter") return false;
        return int.TryParse(weight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n >= 600 : null;
    }

    /// <summary>
    /// <c>&lt;b&gt;a&lt;/b&gt;&lt;b&gt;b&lt;/b&gt;</c> would otherwise come out as
    /// <c>**a****b**</c>, which markdown does not read as one bold run. Styled spans
    /// split at every font change, so this is the common case, not an edge.
    /// </summary>
    private static void MergeAdjacent(IElement body)
    {
        foreach (var el in body.QuerySelectorAll("strong, em, s").ToList())
        {
            if (el.Parent is null) continue;
            if (el.PreviousSibling is IElement before && before.LocalName == el.LocalName)
            {
                MoveChildren(el, before);
                el.Remove();
            }
        }
    }

    /// <summary>
    /// VS Code, and editors like it, copy code as divs under a white-space:pre
    /// wrapper. That is someone's source text, not a document, so it goes in as the
    /// plain text it already is.
    /// </summary>
    private static bool IsPreformattedSource(IElement body)
    {
        var total = body.TextContent.Trim().Length;
        if (total == 0) return false;

        return body.QuerySelectorAll("*").Any(e =>
            e.LocalName != "pre"
            && Css(e).GetValueOrDefault("white-space") == "pre"
            && e.TextContent.Trim().Length >= total * 0.9);
    }

    // ── DOM helpers ──────────────────────────────────────────────────────────

    private static IEnumerable<INode> Descendants(INode node)
    {
        foreach (var child in node.ChildNodes)
        {
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }

    private static void MoveChildren(INode from, INode to)
    {
        while (from.FirstChild is { } child) to.AppendChild(child);
    }

    /// <summary>Attributes are not carried over: by the time anything is renamed, they have been read.</summary>
    private static IElement Rename(IElement el, string name)
    {
        var renamed = el.Owner!.CreateElement(name);
        MoveChildren(el, renamed);
        el.Parent!.ReplaceChild(renamed, el);
        return renamed;
    }

    private static void WrapChildren(IElement el, string name)
    {
        var wrapper = el.Owner!.CreateElement(name);
        MoveChildren(el, wrapper);
        el.AppendChild(wrapper);
    }

    private static Dictionary<string, string> Css(IElement el)
    {
        var css = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.GetAttribute("style") is not { Length: > 0 } style) return css;

        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            css[declaration[..colon].Trim()] =
                declaration[(colon + 1)..].Replace("!important", "").Trim().ToLowerInvariant();
        }
        return css;
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A &lt;br&gt;, held as a private-use character until the block it lands in
    /// decides what it means: a hard break in a paragraph, a space in a heading or
    /// a table cell, and two in a row are a paragraph break.
    /// </summary>
    private const char Break = '';

    private static readonly Regex Whitespace = new(@"\s+");
    private static readonly Regex Spaces = new(@" {2,}");

    /// <summary>HTML whitespace rules: any run is one space. \s includes the non-breaking space Word is full of.</summary>
    private static string Collapse(string text) => Whitespace.Replace(text, " ");

    /// <summary>
    /// Escapes what markdown would otherwise read as syntax. Markdig's advanced
    /// extensions make ~ subscript and ^ superscript, so "~5 days" needs it too.
    /// An underscore inside a word is left alone — snake_case cannot start emphasis.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\' or '*' or '`' or '[' or ']' or '~' or '^':
                    sb.Append('\\');
                    break;
                case '_' when !(i > 0 && char.IsLetterOrDigit(text[i - 1])
                                && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1])):
                    sb.Append('\\');
                    break;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static readonly Regex BlockStart = new(@"^(#{1,6}(?=\s|$)|[-+](?=\s|$)|>|=+\s*$|-{3,}\s*$)");
    private static readonly Regex NumberStart = new(@"^(\d{1,9})(?=[.)](\s|$))");

    /// <summary>A paragraph line that happens to begin "# " or "1. " must not turn into a heading or a list.</summary>
    private static string EscapeLineStart(string line)
    {
        if (NumberStart.Match(line) is { Success: true } m)
            return m.Value + "\\" + line[m.Length..];
        return BlockStart.IsMatch(line) ? "\\" + line : line;
    }

    private static string Url(string url) => url
        .Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29")
        .Replace("<", "%3C").Replace(">", "%3E");

    private static int LongestRun(string text, char c)
    {
        int longest = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }

    // ── Writing markdown ─────────────────────────────────────────────────────

    /// <summary>Formatting already open around the current text, so it is not opened twice.</summary>
    private readonly record struct Emphasis(bool Bold, bool Italic, bool Strike);

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li",
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption",
        "pre", "blockquote", "hr", "section", "article", "header", "footer", "main", "nav", "aside",
        "figure", "figcaption", "dl", "dt", "dd", "address", "center", "form", "fieldset",
        "details", "summary",
    };

    private static readonly string BlockSelector = string.Join(",", BlockTags);

    private static readonly Regex LanguageClass = new(@"^(?:language|lang)-([\w+#-]+)$");
    private static readonly Regex ListMarker = new(@"^(- |\d+\. )");

    private sealed class Writer
    {
        /// <summary>Set once anything is written that plain text would not have had.</summary>
        public bool Structured;
        public int ImagesDropped;

        /// <summary>
        /// The markdown blocks for a container's children. Inline content accumulates
        /// into a paragraph until a block element interrupts it.
        /// </summary>
        public List<string> Blocks(INode container, Emphasis em)
        {
            var blocks = new List<string>();
            var paragraph = new StringBuilder();

            void Flush()
            {
                blocks.AddRange(Paragraphs(paragraph.ToString()));
                paragraph.Clear();
            }

            void Add(string? block)
            {
                Flush();
                if (!string.IsNullOrEmpty(block)) blocks.Add(block);
            }

            foreach (var node in container.ChildNodes)
            {
                if (node is not IElement el)
                {
                    if (node is IText text) paragraph.Append(Escape(Collapse(text.Data)));
                    continue;
                }

                switch (el.LocalName)
                {
                    case "h1" or "h2" or "h3" or "h4" or "h5" or "h6": Add(Heading(el)); break;
                    case "ul" or "ol": Add(List(el, em)); break;
                    case "pre": Add(Fence(el)); break;
                    case "blockquote": Add(Quote(el, em)); break;
                    case "hr": Add("---"); Structured = true; break;
                    case "table":
                        Flush();
                        blocks.AddRange(Table(el, em));
                        break;
                    default:
                        // An inline element can still hold blocks: Google Docs wraps a
                        // whole copy in one <b>, headings, lists and tables included.
                        if (BlockTags.Contains(el.LocalName) || el.QuerySelector(BlockSelector) is not null)
                        {
                            Flush();
                            blocks.AddRange(Blocks(el, em));
                        }
                        else
                        {
                            paragraph.Append(Inline(el, em));
                        }
                        break;
                }
            }

            Flush();
            return blocks;
        }

        private static IEnumerable<string> Paragraphs(string raw)
        {
            var lines = new List<string>();
            foreach (var part in raw.Split(Break))
            {
                var line = Spaces.Replace(part, " ").Trim();
                if (line.Length > 0)
                {
                    lines.Add(EscapeLineStart(line));
                }
                else if (lines.Count > 0)
                {
                    yield return string.Join("\\\n", lines);
                    lines.Clear();
                }
            }
            if (lines.Count > 0) yield return string.Join("\\\n", lines);
        }

        private string? Heading(IElement h)
        {
            // Headings are bold already; Google Docs and Word both say so again inside.
            var text = Flatten(Children(h, new Emphasis(Bold: true, Italic: false, Strike: false)));
            if (text.Length == 0) return null;

            Structured = true;
            return new string('#', h.LocalName[1] - '0') + " " + text;
        }

        private string? List(IElement list, Emphasis em)
        {
            var ordered = list.LocalName == "ol";
            var number = ordered && int.TryParse(list.GetAttribute("start"), out var start) ? start : 1;
            var items = new List<string>();
            var indent = 0;

            foreach (var child in list.Children)
            {
                // Invalid but common: a list directly inside a list, meaning the item before it.
                if (child.LocalName is "ul" or "ol")
                {
                    if (List(child, em) is not { } nested) continue;
                    if (items.Count == 0) items.Add(nested);
                    else items[^1] += "\n" + Indent(nested, indent, skipFirst: false);
                    continue;
                }

                var body = ItemBody(Blocks(child, em));
                if (body.Length == 0) continue;

                var marker = ordered ? $"{number++}. " : "- ";
                indent = marker.Length;
                items.Add(marker + Indent(body, indent, skipFirst: true));
            }

            if (items.Count == 0) return null;
            Structured = true;
            return string.Join("\n", items);
        }

        /// <summary>A nested list sits tight under its text; anything else is a separate paragraph.</summary>
        private static string ItemBody(List<string> blocks)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (i > 0) sb.Append(ListMarker.IsMatch(blocks[i]) ? "\n" : "\n\n");
                sb.Append(blocks[i]);
            }
            return sb.ToString();
        }

        private static string Indent(string text, int width, bool skipFirst)
        {
            var pad = new string(' ', width);
            var lines = text.Split('\n');
            for (var i = skipFirst ? 1 : 0; i < lines.Length; i++)
                if (lines[i].Length > 0) lines[i] = pad + lines[i];
            return string.Join("\n", lines);
        }

        private string? Quote(IElement quote, Emphasis em)
        {
            var blocks = Blocks(quote, em);
            if (blocks.Count == 0) return null;

            Structured = true;
            return string.Join("\n", string.Join("\n\n", blocks)
                .Split('\n')
                .Select(line => line.Length == 0 ? ">" : "> " + line));
        }

        private string? Fence(IElement pre)
        {
            foreach (var br in pre.QuerySelectorAll("br").ToList())
                br.Parent!.ReplaceChild(pre.Owner!.CreateTextNode("\n"), br);

            var code = pre.TextContent.Replace(' ', ' ').Replace("\r\n", "\n").Trim('\n').TrimEnd();
            if (code.Trim().Length == 0) return null;

            var language = Language(pre) ?? (pre.QuerySelector("code") is { } inner ? Language(inner) : null);
            var fence = new string('`', Math.Max(3, LongestRun(code, '`') + 1));

            Structured = true;
            return fence + language + "\n" + code + "\n" + fence;
        }

        private static string? Language(IElement el) =>
            el.ClassList.Select(c => LanguageClass.Match(c)).FirstOrDefault(m => m.Success)?.Groups[1].Value;

        /// <summary>
        /// A GFM pipe table. The first row is the header whether or not it was marked
        /// up as one — Word never uses th, and GFM cannot have a table without a header.
        /// Merged cells are split back out, the content going in the first.
        /// </summary>
        private IEnumerable<string> Table(IElement table, Emphasis em)
        {
            var rows = (table as IHtmlTableElement)?.Rows.ToList() ?? [];
            if (rows.Count == 0) return [];

            // Layout, not data. Email newsletters and signatures are built from tables,
            // and a one-column table or one holding tables is a page grid: its content
            // reads better as the paragraphs it is.
            var widest = rows.Max(r => r.Cells.Sum(c => Span(c, "colspan")));
            if (widest < 2 || table.QuerySelector("table") is not null || table.GetAttribute("role") == "presentation")
                return rows.SelectMany(r => r.Cells).SelectMany(c => Blocks(c, em)).ToList();

            var grid = new List<List<string?>>();
            for (var r = 0; r < rows.Count; r++)
            {
                var c = 0;
                foreach (var cell in rows[r].Cells)
                {
                    while (c < RowAt(grid, r).Count && grid[r][c] is not null) c++;

                    var text = Cell(cell, r == 0 ? em with { Bold = true } : em);
                    var colspan = Span(cell, "colspan");
                    var rowspan = Math.Min(Span(cell, "rowspan"), rows.Count - r);

                    for (var dr = 0; dr < rowspan; dr++)
                        for (var dc = 0; dc < colspan; dc++)
                            Set(RowAt(grid, r + dr), c + dc, dr == 0 && dc == 0 ? text : "");
                    c += colspan;
                }
            }

            var columns = grid.Max(r => r.Count);
            var cells = grid.Select(r => Enumerable.Range(0, columns).Select(i => i < r.Count ? r[i] ?? "" : "").ToList()).ToList();

            // Padded so the source lines up, but only so far: one long cell should not
            // push every row of a column out to its width.
            var widths = Enumerable.Range(0, columns)
                .Select(i => Math.Clamp(cells.Max(r => r[i].Length), 3, 30))
                .ToList();

            string Line(IEnumerable<string> values) => "| " + string.Join(" | ", values) + " |";
            string Row(List<string> row) => Line(row.Select((v, i) => v.PadRight(widths[i])));

            var lines = new List<string> { Row(cells[0]), Line(widths.Select(w => new string('-', w))) };
            lines.AddRange(cells.Skip(1).Select(Row));

            Structured = true;
            return [string.Join("\n", lines)];
        }

        private static List<string?> RowAt(List<List<string?>> grid, int r)
        {
            while (grid.Count <= r) grid.Add([]);
            return grid[r];
        }

        private static void Set(List<string?> row, int c, string value)
        {
            while (row.Count <= c) row.Add(null);
            row[c] = value;
        }

        private static int Span(IElement cell, string attribute) =>
            int.TryParse(cell.GetAttribute(attribute), out var n) ? Math.Clamp(n, 1, 100) : 1;

        /// <summary>A pipe-table cell is one line, so everything in it is run together.</summary>
        private string Cell(IElement cell, Emphasis em)
        {
            var text = string.Join(" ", Blocks(cell, em)).Replace("\\\n", " ").Replace('\n', ' ');
            return Spaces.Replace(text, " ").Trim().Replace("|", "\\|");
        }

        private string Children(INode node, Emphasis em) =>
            string.Concat(node.ChildNodes.Select(child => Inline(child, em)));

        private string Inline(INode node, Emphasis em)
        {
            if (node is IText text) return Escape(Collapse(text.Data));
            if (node is not IElement el) return "";

            switch (el.LocalName)
            {
                case "br": return Break.ToString();
                case "img": return Image(el);
                case "a": return Link(el, em);
                case "code" or "kbd" or "samp" or "tt": return Code(el.TextContent);
                case "strong":
                    return em.Bold ? Children(el, em) : Wrap(Children(el, em with { Bold = true }), "**", "**");
                case "em":
                    return em.Italic ? Children(el, em) : Wrap(Children(el, em with { Italic = true }), "*", "*");
                case "s":
                    return em.Strike ? Children(el, em) : Wrap(Children(el, em with { Strike = true }), "~~", "~~");
            }

            // A block inside a heading or a link still separates its text from what is around it.
            var inner = Children(el, em);
            return BlockTags.Contains(el.LocalName) ? Break + inner + Break : inner;
        }

        /// <summary>
        /// Markdown emphasis cannot start or end on a space — <c>**bold **</c> is not
        /// bold — so surrounding whitespace is moved outside the delimiters.
        /// </summary>
        private string Wrap(string inner, string open, string close)
        {
            static bool Gap(char c) => c == ' ' || c == Break;

            int start = 0, end = inner.Length;
            while (start < end && Gap(inner[start])) start++;
            while (end > start && Gap(inner[end - 1])) end--;
            if (start == end) return inner;

            Structured = true;
            return inner[..start] + open + inner[start..end] + close + inner[end..];
        }

        private string Code(string text)
        {
            var body = Collapse(text).Trim();
            if (body.Length == 0) return "";

            var fence = new string('`', LongestRun(body, '`') + 1);
            var pad = body.StartsWith('`') || body.EndsWith('`') ? " " : "";

            Structured = true;
            return fence + pad + body + pad + fence;
        }

        private string Link(IElement a, Emphasis em)
        {
            var text = Children(a, em).Replace(Break, ' ');
            var href = a.GetAttribute("href")?.Trim();

            // Word's footnote and bookmark links, file: paths and javascript: all keep
            // their text and lose the link.
            if (href is null || !IsWeb(href, allowMail: true)) return text;
            return Wrap(text, "[", "](" + Url(href) + ")");
        }

        private string Image(IElement img)
        {
            var src = img.GetAttribute("src")?.Trim();
            if (src is null || !IsWeb(src, allowMail: false))
            {
                ImagesDropped++;
                return "";
            }

            // A 1×1 image in an email is a tracking pixel. Keeping it would have the
            // preview report to the sender every time the document was opened.
            if (int.TryParse(img.GetAttribute("width"), out var w) && w <= 2) return "";
            if (int.TryParse(img.GetAttribute("height"), out var h) && h <= 2) return "";

            Structured = true;
            var alt = Escape(Collapse(img.GetAttribute("alt") ?? "")).Trim();
            return "![" + alt + "](" + Url(src) + ")";
        }

        private static bool IsWeb(string url, bool allowMail) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme is "http" or "https" || allowMail && uri.Scheme == "mailto");

        private static string Flatten(string text) => Spaces.Replace(text.Replace(Break, ' '), " ").Trim();
    }
}
