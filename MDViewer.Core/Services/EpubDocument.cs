using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Dom;

// Microsoft.Maui also defines IElement, and MAUI's implicit usings pull it in.
using IElement = AngleSharp.Dom.IElement;

namespace MDViewer.Services;

/// <summary>One document from the spine — the unit the reader shows at a time.</summary>
public sealed record EpubChapter(string Path, string Title);

/// <summary>
/// One line in the contents sidebar. A table of contents may point *inside* a
/// chapter file rather than at the top of it, so <paramref name="Fragment"/> is the
/// element id to scroll to once the chapter is on screen (null = top of chapter).
/// </summary>
public sealed record EpubTocEntry(string Title, int ChapterIndex, string? Fragment, int Depth);

/// <summary>
/// Reads an ePub. This is <see cref="EpubGenerator"/> run backwards — same file
/// layout, same container/OPF/nav structure — with two things the generator never
/// has to deal with, because it only ever writes what it wrote:
///
///   * EPUB 2. The generator emits EPUB 3 with nav.xhtml, but a large share of
///     real books are EPUB 2 with a toc.ncx and no nav document at all, so both
///     table-of-contents formats are parsed here.
///   * Sloppy input. Hrefs are URL-encoded, walk upwards ("../images/x.png"), and
///     occasionally disagree with the ZIP entry on casing. Anything that fails to
///     parse degrades to a weaker result rather than refusing to open the book.
///
/// The whole file is held in memory rather than kept open as a ZipArchive: a
/// viewer that locks the file it is showing stops you moving or deleting it, and
/// an ePub is a few MB.
/// </summary>
public sealed class EpubDocument
{
    private readonly byte[] _bytes;
    private readonly Dictionary<int, string> _rendered = [];
    private readonly Dictionary<string, int> _chapterIndex;

    public string Path { get; }
    public string Title { get; }
    public string? Author { get; }
    public IReadOnlyList<EpubChapter> Chapters { get; }
    public IReadOnlyList<EpubTocEntry> Contents { get; }

    private EpubDocument(
        string path, byte[] bytes, string title, string? author,
        IReadOnlyList<EpubChapter> chapters, IReadOnlyList<EpubTocEntry> contents)
    {
        Path = path;
        _bytes = bytes;
        Title = title;
        Author = author;
        Chapters = chapters;
        Contents = contents;

        _chapterIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < chapters.Count; i++)
            _chapterIndex.TryAdd(chapters[i].Path, i);
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    public static async Task<EpubDocument> LoadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = IndexEntries(zip);

        var opfPath = FindOpfPath(zip, entries)
            ?? throw new InvalidDataException("No package document — this doesn't look like an ePub.");
        var opfDir = DirectoryOf(opfPath);

        var opf = ParseXml(ReadText(zip, entries, opfPath)
            ?? throw new InvalidDataException("The package document is missing from the archive."));

        var metadata = ChildNamed(opf.Root, "metadata");
        var title = ChildNamed(metadata, "title")?.Value.Trim();
        var author = ChildNamed(metadata, "creator")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = System.IO.Path.GetFileNameWithoutExtension(path);

        var manifest = ReadManifest(opf, opfDir);
        var (chapters, tocId) = ReadSpine(opf, manifest);

        if (chapters.Count == 0)
            throw new InvalidDataException("The spine is empty — there is nothing to read.");

        var contents = await ReadContentsAsync(zip, entries, manifest, chapters, tocId, opfDir);

        // No usable table of contents: fall back to each chapter's own <title>, so
        // the sidebar still says something better than "Chapter 7".
        if (contents.Count == 0)
        {
            for (var i = 0; i < chapters.Count; i++)
            {
                var heading = TitleOf(ReadText(zip, entries, chapters[i].Path)) ?? $"Chapter {i + 1}";
                chapters[i] = chapters[i] with { Title = heading };
                contents.Add(new EpubTocEntry(heading, i, null, 0));
            }
        }
        else
        {
            // Give each spine document the first contents entry that points at it.
            foreach (var entry in contents)
                if (chapters[entry.ChapterIndex].Title.Length == 0)
                    chapters[entry.ChapterIndex] = chapters[entry.ChapterIndex] with { Title = entry.Title };

            for (var i = 0; i < chapters.Count; i++)
                if (chapters[i].Title.Length == 0)
                    chapters[i] = chapters[i] with { Title = $"Chapter {i + 1}" };
        }

        return new EpubDocument(path, bytes, title!, author, chapters, contents);
    }

    /// <summary>
    /// The chapter's body, sanitised and with its images inlined, ready for
    /// MarkupString. Cached — flipping back and forth between chapters is common,
    /// and re-inlining the images each time is not free.
    /// </summary>
    public async Task<string> ChapterHtmlAsync(int index)
    {
        if (index < 0 || index >= Chapters.Count) return string.Empty;
        if (_rendered.TryGetValue(index, out var cached)) return cached;

        using var zip = new ZipArchive(new MemoryStream(_bytes), ZipArchiveMode.Read);
        var entries = IndexEntries(zip);

        var chapterPath = Chapters[index].Path;
        var baseDir = DirectoryOf(chapterPath);
        var raw = ReadText(zip, entries, chapterPath) ?? string.Empty;

        var html = await EpubHtml.ToSafeBodyAsync(
            raw,
            readResource: href => ReadBytes(zip, entries, Resolve(baseDir, href)),
            chapterIndexOf: href => IndexOf(Resolve(baseDir, href)));

        _rendered[index] = html;
        return html;
    }

    private int? IndexOf(string path) => _chapterIndex.TryGetValue(path, out var i) ? i : null;

    // ── Package parsing ──────────────────────────────────────────────────────

    private sealed record ManifestItem(string Id, string Path, string MediaType, string Properties);

    private static string? FindOpfPath(ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries)
    {
        try
        {
            if (ReadText(zip, entries, "META-INF/container.xml") is { Length: > 0 } container)
            {
                var full = ParseXml(container)
                    .Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")
                    ?.Attribute("full-path")?.Value;
                if (!string.IsNullOrWhiteSpace(full)) return Resolve(string.Empty, full);
            }
        }
        catch (Exception)
        {
            // Malformed container — fall through to finding the .opf by name.
        }

        return zip.Entries
            .Select(e => e.FullName)
            .FirstOrDefault(n => n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, ManifestItem> ReadManifest(XDocument opf, string opfDir)
    {
        var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        var manifest = ChildNamed(opf.Root, "manifest");
        if (manifest is null) return items;

        foreach (var el in manifest.Elements().Where(e => e.Name.LocalName == "item"))
        {
            var id = el.Attribute("id")?.Value;
            var href = el.Attribute("href")?.Value;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(href)) continue;

            items[id] = new ManifestItem(
                id,
                Resolve(opfDir, href),
                el.Attribute("media-type")?.Value ?? string.Empty,
                el.Attribute("properties")?.Value ?? string.Empty);
        }
        return items;
    }

    private static (List<EpubChapter> Chapters, string? TocId) ReadSpine(
        XDocument opf, Dictionary<string, ManifestItem> manifest)
    {
        var chapters = new List<EpubChapter>();
        var spine = ChildNamed(opf.Root, "spine");
        if (spine is null) return (chapters, null);

        foreach (var el in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
        {
            var idref = el.Attribute("idref")?.Value;
            if (idref is null || !manifest.TryGetValue(idref, out var item)) continue;

            // Only readable documents go in the reading order. A spine can also
            // carry SVG or fallback resources, which would render as noise.
            if (item.MediaType.Length > 0 &&
                !item.MediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase) &&
                !item.MediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                continue;

            chapters.Add(new EpubChapter(item.Path, string.Empty));
        }

        return (chapters, spine.Attribute("toc")?.Value);
    }

    // ── Table of contents ────────────────────────────────────────────────────

    private static async Task<List<EpubTocEntry>> ReadContentsAsync(
        ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries,
        Dictionary<string, ManifestItem> manifest, List<EpubChapter> chapters,
        string? tocId, string opfDir)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < chapters.Count; i++) index.TryAdd(chapters[i].Path, i);

        // EPUB 3: a manifest item flagged properties="nav".
        var nav = manifest.Values.FirstOrDefault(i =>
            i.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains("nav", StringComparer.OrdinalIgnoreCase));
        if (nav is not null && ReadText(zip, entries, nav.Path) is { Length: > 0 } navXhtml)
        {
            try
            {
                var parsed = await ReadNavAsync(navXhtml, DirectoryOf(nav.Path), index);
                if (parsed.Count > 0) return parsed;
            }
            catch (Exception)
            {
                // Unparseable nav document — try the NCX instead.
            }
        }

        // EPUB 2: spine@toc names a manifest item holding an NCX.
        var ncxItem = tocId is not null && manifest.TryGetValue(tocId, out var byId)
            ? byId
            : manifest.Values.FirstOrDefault(i =>
                i.MediaType.Contains("dtbncx", StringComparison.OrdinalIgnoreCase) ||
                i.Path.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase));

        if (ncxItem is not null && ReadText(zip, entries, ncxItem.Path) is { Length: > 0 } ncx)
        {
            try
            {
                return ReadNcx(ncx, DirectoryOf(ncxItem.Path), index);
            }
            catch (Exception)
            {
                // Fall through to the spine-derived contents the caller builds.
            }
        }

        return [];
    }

    private static async Task<List<EpubTocEntry>> ReadNavAsync(
        string navXhtml, string baseDir, Dictionary<string, int> index)
    {
        // AngleSharp rather than XDocument: nav documents are XHTML in name but are
        // regularly served with HTML entities that a strict XML parser rejects.
        var context = BrowsingContext.New(Configuration.Default);
        using var doc = await context.OpenAsync(req => req.Content(navXhtml));

        // The toc nav specifically — a nav document may also carry a page list and
        // a landmarks nav, which are not reading order.
        var toc = doc.QuerySelectorAll("nav").FirstOrDefault(n =>
                      n.GetAttribute("epub:type") == "toc" || n.GetAttribute("type") == "toc")
                  ?? doc.QuerySelectorAll("nav").FirstOrDefault();
        if (toc is null) return [];

        var entries = new List<EpubTocEntry>();

        void Walk(IElement list, int depth)
        {
            foreach (var li in list.Children.Where(c => c.LocalName == "li"))
            {
                var anchor = li.Children.FirstOrDefault(c => c.LocalName == "a");
                var href = anchor?.GetAttribute("href");
                if (anchor is not null && !string.IsNullOrWhiteSpace(href))
                    Add(entries, index, baseDir, href, anchor.TextContent, depth);

                foreach (var nested in li.Children.Where(c => c.LocalName is "ol" or "ul"))
                    Walk(nested, depth + 1);
            }
        }

        foreach (var list in toc.Children.Where(c => c.LocalName is "ol" or "ul"))
            Walk(list, 0);

        return entries;
    }

    private static List<EpubTocEntry> ReadNcx(string ncx, string baseDir, Dictionary<string, int> index)
    {
        var doc = ParseXml(ncx);
        var navMap = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");
        if (navMap is null) return [];

        var entries = new List<EpubTocEntry>();

        void Walk(XElement parent, int depth)
        {
            foreach (var point in parent.Elements().Where(e => e.Name.LocalName == "navPoint"))
            {
                var label = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")
                                 ?.Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Value;
                var src = point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")
                               ?.Attribute("src")?.Value;

                if (!string.IsNullOrWhiteSpace(src))
                    Add(entries, index, baseDir, src, label ?? string.Empty, depth);

                Walk(point, depth + 1);
            }
        }

        Walk(navMap, 0);
        return entries;
    }

    private static void Add(
        List<EpubTocEntry> entries, Dictionary<string, int> index,
        string baseDir, string href, string label, int depth)
    {
        var target = Resolve(baseDir, href);
        if (!index.TryGetValue(target, out var chapter)) return; // points outside the spine

        var title = Whitespace.Replace(label, " ").Trim();
        if (title.Length == 0) title = $"Chapter {chapter + 1}";

        entries.Add(new EpubTocEntry(title, chapter, FragmentOf(href), depth));
    }

    // ── ZIP and path plumbing ────────────────────────────────────────────────

    /// <summary>
    /// ZIP lookups are case-insensitive here. The spec says entry names are
    /// case-sensitive, but books whose OPF says "Images/cover.jpg" for an entry
    /// stored as "images/cover.jpg" are common enough to be worth absorbing.
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries) map.TryAdd(entry.FullName, entry);
        return map;
    }

    private static byte[]? ReadBytes(ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries, string path)
    {
        if (!entries.TryGetValue(path, out var entry)) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string? ReadText(ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries, string path)
    {
        if (ReadBytes(zip, entries, path) is not { } bytes) return null;

        // Strip a BOM if there is one: XDocument.Parse chokes on a leading U+FEFF.
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false).GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>
    /// XML from an untrusted file. DTD processing is off and the resolver is null,
    /// so a book cannot make the app fetch an external DTD or expand an entity
    /// bomb — but NCX files legitimately carry a DOCTYPE, so it has to be ignored
    /// rather than rejected outright (the .NET default is to throw).
    /// </summary>
    private static XDocument ParseXml(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader);
    }

    private static XElement? ChildNamed(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string? FragmentOf(string href)
    {
        var hash = href.IndexOf('#');
        if (hash < 0 || hash == href.Length - 1) return null;
        return Uri.UnescapeDataString(href[(hash + 1)..]);
    }

    /// <summary>
    /// Turns an href written inside <paramref name="baseDir"/> into a ZIP entry
    /// path: fragment dropped, percent-decoding undone, "." and ".." collapsed.
    /// </summary>
    internal static string Resolve(string baseDir, string href)
    {
        var hash = href.IndexOf('#');
        if (hash >= 0) href = href[..hash];

        href = href.Replace('\\', '/');
        try { href = Uri.UnescapeDataString(href); }
        catch (UriFormatException) { /* leave the raw href — a stray % is not fatal */ }

        var segments = new List<string>();
        if (baseDir.Length > 0 && !href.StartsWith('/'))
            segments.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var part in href.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(part);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// The <c>&lt;title&gt;</c> of a chapter, by regex rather than a full parse —
    /// this only runs for books with no table of contents at all, and parsing every
    /// document in the spine to read one element each would be a poor trade.
    /// </summary>
    private static string? TitleOf(string? xhtml)
    {
        if (xhtml is null) return null;
        var match = TitleTag.Match(xhtml);
        if (!match.Success) return null;

        var title = Whitespace.Replace(match.Groups[1].Value, " ").Trim();
        return title.Length == 0 ? null : title;
    }

    private static readonly Regex TitleTag =
        new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex Whitespace = new(@"\s+");
}
