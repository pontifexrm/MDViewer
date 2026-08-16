using System.Globalization;
using System.Text;
using AngleSharp;
using AngleSharp.Dom;

// Microsoft.Maui also defines IElement, and MAUI's implicit usings pull it in.
using IElement = AngleSharp.Dom.IElement;

namespace MDViewer.Services;

/// <summary>
/// Turns a chapter's XHTML into HTML that is safe to hand to MarkupString.
///
/// This is the one place the app renders markup it did not generate itself. The
/// rule for markdown is <c>DisableHtml()</c> — nothing authored elsewhere reaches
/// the DOM at all, because the WebView showing it also hosts Blazor with .NET
/// interop. An ePub is nothing *but* authored HTML and EPUB 3 explicitly permits
/// scripting, so that rule cannot hold here. What replaces it is an allow-list:
/// elements and attributes not named below are dropped, so a tag or attribute
/// nobody thought about fails closed rather than open.
///
/// Consequences worth knowing:
///   * The book's own CSS is dropped (both &lt;style&gt; and &lt;link&gt;, and the
///     style attribute). Chapters render through the app's .doc-render rules
///     instead, so custom fonts, drop caps and verse indentation are lost.
///   * Remote resources are never fetched. An &lt;img&gt; pointing at a web server
///     is a tracking pixel that would tell a stranger when the file was opened.
/// </summary>
public static class EpubHtml
{
    /// <param name="readResource">
    /// Resolves an href relative to the chapter and returns the bytes from the
    /// archive, or null if there is no such entry.
    /// </param>
    /// <param name="chapterIndexOf">
    /// Resolves an href to the index of the spine document it points at, or null
    /// if it points outside the reading order.
    /// </param>
    public static async Task<string> ToSafeBodyAsync(
        string xhtml,
        Func<string, byte[]?> readResource,
        Func<string, int?> chapterIndexOf)
    {
        if (string.IsNullOrWhiteSpace(xhtml)) return string.Empty;

        var context = BrowsingContext.New(Configuration.Default);
        using var doc = await context.OpenAsync(req => req.Content(xhtml));
        if (doc.Body is not { } body) return string.Empty;

        PromoteSvgImages(body);
        RemoveDiscarded(body);
        UnwrapUnknown(body);

        foreach (var element in body.QuerySelectorAll("*"))
            StripAttributes(element);

        foreach (var anchor in body.QuerySelectorAll("a"))
            RewriteLink(anchor, chapterIndexOf);

        foreach (var image in body.QuerySelectorAll("img"))
            InlineImage(image, readResource);

        var sb = new StringBuilder();
        foreach (var node in body.ChildNodes)
            sb.Append(node.ToHtml());
        return sb.ToString();
    }

    // ── Allow-lists ──────────────────────────────────────────────────────────

    /// <summary>Removed along with their contents — the content is not text.</summary>
    private static readonly HashSet<string> Discard = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "link", "meta", "base", "head", "title", "noscript", "template",
        "iframe", "frame", "frameset", "object", "embed", "applet", "param",
        "form", "input", "button", "select", "option", "optgroup", "textarea",
        "label", "fieldset", "legend", "output", "progress", "meter",
        "audio", "video", "source", "track", "canvas", "map", "area",
        "dialog", "svg", "math", "foreignObject",
    };

    /// <summary>
    /// Kept. Anything outside this list and outside <see cref="Discard"/> is
    /// unwrapped — the element goes, its text stays — so an unfamiliar wrapper
    /// costs formatting rather than content.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "span", "br", "hr", "a", "img",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "em", "strong", "i", "b", "u", "s", "strike", "small", "big", "sub", "sup", "mark",
        "ul", "ol", "li", "dl", "dt", "dd",
        "blockquote", "q", "cite", "pre", "code", "kbd", "samp", "var", "tt",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "figure", "figcaption", "section", "article", "aside", "header", "footer", "main",
        "address", "abbr", "dfn", "time", "ins", "del", "ruby", "rt", "rp",
        "bdi", "bdo", "wbr", "details", "summary", "center", "font", "nav",
    };

    /// <summary>
    /// Kept. Deliberately excludes every URL-bearing attribute (cite, longdesc,
    /// poster, srcset, usemap, xlink:href) and <c>style</c>; href on an anchor and
    /// src on an image survive this pass and are validated separately below.
    /// Excluding rather than listing "on*" is what makes event handlers fail closed.
    /// </summary>
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "class", "alt", "title", "lang", "dir",
        "colspan", "rowspan", "headers", "scope", "abbr",
        "width", "height", "align", "valign", "start", "reversed", "value", "datetime",
    };

    // ── Passes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Cover pages are routinely <c>&lt;svg&gt;&lt;image xlink:href="cover.jpg"/&gt;&lt;/svg&gt;</c>.
    /// The SVG itself is discarded — it can carry script, foreignObject and
    /// animation events — so the bitmap is lifted out as a plain img first.
    /// </summary>
    private static void PromoteSvgImages(IElement body)
    {
        foreach (var svg in body.QuerySelectorAll("svg").ToList())
        {
            foreach (var image in svg.QuerySelectorAll("image").ToList())
            {
                var href = image.Attributes
                    .FirstOrDefault(a => a.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                if (string.IsNullOrWhiteSpace(href)) continue;

                var img = body.Owner!.CreateElement("img");
                img.SetAttribute("src", href);
                svg.Parent?.InsertBefore(img, svg);
            }
        }
    }

    private static void RemoveDiscarded(IElement body)
    {
        foreach (var element in body.QuerySelectorAll("*").ToList())
            if (Discard.Contains(element.LocalName))
                element.Remove();
    }

    private static void UnwrapUnknown(IElement body)
    {
        foreach (var element in body.QuerySelectorAll("*").ToList())
        {
            if (Allowed.Contains(element.LocalName)) continue;
            if (element.Parent is not { } parent) continue;

            while (element.FirstChild is { } child)
                parent.InsertBefore(child, element);
            element.Remove();
        }
    }

    private static void StripAttributes(IElement element)
    {
        foreach (var attribute in element.Attributes.ToList())
        {
            if (AllowedAttributes.Contains(attribute.Name)) continue;
            if (element.LocalName == "a" && attribute.Name.Equals("href", StringComparison.OrdinalIgnoreCase)) continue;
            if (element.LocalName == "img" && attribute.Name.Equals("src", StringComparison.OrdinalIgnoreCase)) continue;

            if (attribute.NamespaceUri is { Length: > 0 } ns)
                element.RemoveAttribute(ns, attribute.LocalName);
            else
                element.RemoveAttribute(attribute.Name);
        }
    }

    /// <summary>
    /// Links never navigate the WebView themselves — that would replace the running
    /// app with the linked page. The href is removed and replaced by a data
    /// attribute the click handler in wwwroot/epub.js reads, so navigation happens
    /// through the app instead of through the browser engine.
    /// </summary>
    private static void RewriteLink(IElement anchor, Func<string, int?> chapterIndexOf)
    {
        var href = anchor.GetAttribute("href")?.Trim();
        anchor.RemoveAttribute("href");
        if (string.IsNullOrWhiteSpace(href)) return;

        // An anchor within this chapter is the one case the WebView can resolve on
        // its own without leaving the page.
        if (href.StartsWith('#'))
        {
            anchor.SetAttribute("href", href);
            return;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            // Only the web schemes are offered, and only via the handler, which
            // hands them to the default browser. javascript:, data:, file: and the
            // ms-*: protocol handlers Windows registers are all dropped here.
            if (absolute.Scheme is "http" or "https")
            {
                anchor.SetAttribute("data-epub-external", absolute.AbsoluteUri);
                anchor.ClassList.Add("epub-link");
            }
            return;
        }

        if (chapterIndexOf(href) is not { } chapter) return;

        anchor.SetAttribute("data-epub-chapter", chapter.ToString(CultureInfo.InvariantCulture));
        anchor.ClassList.Add("epub-link");

        var hash = href.IndexOf('#');
        if (hash >= 0 && hash < href.Length - 1)
            anchor.SetAttribute("data-epub-frag", Uri.UnescapeDataString(href[(hash + 1)..]));
    }

    /// <summary>
    /// Images are inlined as data URIs — the exact inverse of what
    /// <see cref="EpubGenerator"/> does on the way out. It keeps the WebView from
    /// needing any mapping into the archive, and means nothing has to be unpacked
    /// to a temp folder the app would then have to clean up.
    /// </summary>
    private static void InlineImage(IElement image, Func<string, byte[]?> readResource)
    {
        var src = image.GetAttribute("src")?.Trim();
        image.RemoveAttribute("src");
        if (string.IsNullOrWhiteSpace(src)) return;

        if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            image.SetAttribute("src", src);
            return;
        }

        // Absolute means remote (or off-disk): not fetched. The alt text remains.
        if (Uri.TryCreate(src, UriKind.Absolute, out _)) return;

        if (readResource(src) is not { Length: > 0 } bytes) return;
        image.SetAttribute("src", $"data:{MediaTypeFor(src)};base64,{Convert.ToBase64String(bytes)}");
    }

    private static string MediaTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "image/png",
        };
}
