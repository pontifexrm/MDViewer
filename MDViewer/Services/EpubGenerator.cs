using System.IO.Compression;
using System.Text;
using AngleSharp;
using AngleSharp.Xhtml;

namespace MDViewer.Services;

/// <summary>
/// Builds a valid ePub 3 ZIP in memory from rendered markdown. Ported from our
/// internal knowledge base's ePub generator — same file layout and stylesheet,
/// with the KB DTO parameters replaced by a plain title/HTML pair.
/// ePub is the open e-reader format natively supported by Kindle (2022+), Kobo,
/// Apple Books, etc.
/// </summary>
public static class EpubGenerator
{
    /// <param name="includeTitleHeading">
    /// False when the document already opens with its own H1, so the chapter isn't
    /// topped with a duplicate of it.
    /// </param>
    public static async Task<byte[]> GenerateAsync(
        string title, string bodyHtml, bool includeTitleHeading = true, string? author = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteMimetype(zip);
            Write(zip, "META-INF/container.xml", ContainerXml());
            Write(zip, "EPUB/style.css", StyleCss());

            var images = new List<EpubImage>();
            var body = await ToXhtmlBodyAsync(bodyHtml, images);
            Write(zip, "EPUB/chapter-001.xhtml", ChapterXhtml(title, body, includeTitleHeading));

            foreach (var image in images)
                WriteBinary(zip, $"EPUB/{image.Href}", image.Bytes);

            var chapters = new List<(string File, string Title)> { ("chapter-001.xhtml", title) };
            Write(zip, "EPUB/nav.xhtml", NavXhtml(title, chapters));
            Write(zip, "EPUB/content.opf", ContentOpf(
                Guid.NewGuid().ToString("N"), title, author, DateTime.UtcNow, chapters, images));
        }
        return ms.ToArray();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void WriteMimetype(ZipArchive zip)
    {
        // Must be first entry, uncompressed
        var entry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write("application/epub+zip");
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static void WriteBinary(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(content, 0, content.Length);
    }

    private sealed record EpubImage(string Href, string MediaType, byte[] Bytes);

    // Embedded images arrive as <img src="data:image/png;base64,...">. ePub readers can't
    // resolve data URIs the way browsers do, so each one is pulled out into a real
    // manifest resource. Images referenced by relative path are left alone — they'd be
    // broken links in the ePub, which is still better than dropping them silently.
    private static async Task<string> ToXhtmlBodyAsync(string html, List<EpubImage> images)
    {
        if (string.IsNullOrWhiteSpace(html)) return "<p>&#160;</p>";

        var context = BrowsingContext.New(Configuration.Default);
        using var doc = await context.OpenAsync(req => req.Content(html));

        foreach (var img in doc.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src");
            if (string.IsNullOrEmpty(src) || !src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                continue;

            var comma = src.IndexOf(',');
            var semi = src.IndexOf(';');
            if (comma < 0 || semi < 0 || semi > comma) continue;

            var mediaType = src[5..semi]; // strip leading "data:"
            byte[] bytes;
            try { bytes = Convert.FromBase64String(src[(comma + 1)..]); }
            catch (FormatException) { continue; }

            var ext = mediaType switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpg",
                "image/gif" => "gif",
                "image/webp" => "webp",
                "image/svg+xml" => "svg",
                _ => "png"
            };
            var href = $"images/img-{images.Count + 1:D4}.{ext}";
            images.Add(new EpubImage(href, mediaType, bytes));
            img.SetAttribute("src", href);
        }

        var sb = new StringBuilder();
        foreach (var node in doc.Body!.ChildNodes)
            sb.Append(node.ToHtml(XhtmlMarkupFormatter.Instance));
        return sb.Length == 0 ? "<p>&#160;</p>" : sb.ToString();
    }

    // ── ePub file templates ──────────────────────────────────────────────────

    private static string ContainerXml() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="EPUB/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string ContentOpf(
        string uid,
        string title,
        string? author,
        DateTime? date,
        List<(string File, string Title)> chapters,
        List<EpubImage>? images = null)
    {
        var dateStr = (date ?? DateTime.UtcNow).ToString("yyyy-MM-dd");
        var safeTitle = Xml(title);
        var safeAuthor = Xml(string.IsNullOrWhiteSpace(author) ? "Markdown Viewer" : author);

        var manifest = new StringBuilder();
        manifest.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
        manifest.AppendLine("""    <item id="css" href="style.css" media-type="text/css"/>""");
        for (var i = 0; i < chapters.Count; i++)
            manifest.AppendLine($"""    <item id="c{i + 1:D3}" href="{chapters[i].File}" media-type="application/xhtml+xml"/>""");
        if (images != null)
            for (var i = 0; i < images.Count; i++)
                manifest.AppendLine($"""    <item id="img{i + 1:D4}" href="{images[i].Href}" media-type="{images[i].MediaType}"/>""");

        var spine = new StringBuilder();
        for (var i = 0; i < chapters.Count; i++)
            spine.AppendLine($"""    <itemref idref="c{i + 1:D3}"/>""");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package version="3.0" unique-identifier="uid" xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="uid">urn:uuid:{uid}</dc:identifier>
                <dc:title>{safeTitle}</dc:title>
                <dc:creator>{safeAuthor}</dc:creator>
                <dc:language>en</dc:language>
                <dc:date>{dateStr}</dc:date>
                <meta property="dcterms:modified">{dateStr}T00:00:00Z</meta>
              </metadata>
              <manifest>
            {manifest.ToString().TrimEnd()}
              </manifest>
              <spine>
            {spine.ToString().TrimEnd()}
              </spine>
            </package>
            """;
    }

    private static string NavXhtml(string title, List<(string File, string Title)> chapters)
    {
        var items = new StringBuilder();
        for (var i = 0; i < chapters.Count; i++)
            items.AppendLine($"""        <li><a href="{chapters[i].File}">{Xml(chapters[i].Title)}</a></li>""");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="en">
            <head>
              <meta charset="utf-8"/>
              <title>{Xml(title)}</title>
              <link rel="stylesheet" href="style.css"/>
            </head>
            <body>
              <nav epub:type="toc" id="toc">
                <h1>{Xml(title)}</h1>
                <ol>
            {items.ToString().TrimEnd()}
                </ol>
              </nav>
            </body>
            </html>
            """;
    }

    private static string ChapterXhtml(string title, string bodyXhtml, bool includeTitleHeading) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="en">
        <head>
          <meta charset="utf-8"/>
          <title>{Xml(title)}</title>
          <link rel="stylesheet" href="style.css"/>
        </head>
        <body>
          {(includeTitleHeading ? $"""<h1 class="chapter-title">{Xml(title)}</h1>""" : "")}
          {bodyXhtml}
        </body>
        </html>
        """;

    private static string StyleCss() => """
        body {
            font-family: Georgia, serif;
            font-size: 1em;
            line-height: 1.65;
            color: #111;
            margin: 1.5em 2em;
        }
        h1, h2, h3, h4 { font-family: Arial, sans-serif; line-height: 1.3; margin-top: 1.2em; }
        h1 { font-size: 1.6em; }
        h2 { font-size: 1.3em; }
        h3 { font-size: 1.1em; }
        p { margin: 0.6em 0; }
        ul, ol { padding-left: 1.5em; margin: 0.6em 0; }
        li { margin: 0.3em 0; }
        table { border-collapse: collapse; width: 100%; margin: 1em 0; font-size: 0.9em; }
        th, td { border: 1px solid #ccc; padding: 0.4em 0.6em; text-align: left; }
        th { background: #f0f0f0; font-weight: bold; }
        code, pre { font-family: 'Courier New', monospace; font-size: 0.9em; background: #f8f8f8; }
        pre { padding: 0.8em; border-left: 3px solid #ccc; overflow-x: auto; }
        code { padding: 0.1em 0.3em; }
        blockquote { margin: 1em 1.5em; padding-left: 1em; border-left: 3px solid #ccc; color: #555; font-style: italic; }
        img { max-width: 100%; height: auto; }
        a { color: #0070c0; }
        .chapter-title { font-size: 1.5em; margin-top: 0; padding-bottom: 0.3em; border-bottom: 1px solid #ddd; }
        """;

    private static string Xml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
