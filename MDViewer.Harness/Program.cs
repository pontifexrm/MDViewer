using System.IO.Compression;
using System.Text;
using MDViewer.Services;

var samplePath = args.Length > 0 ? args[0] : "sample.md";
var outDir = Path.Combine(Path.GetDirectoryName(samplePath)!, "out");
Directory.CreateDirectory(outDir);

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : $"  -> {detail}")}"); }
}

Console.WriteLine("== MarkdownFile: load, fidelity, round-trip ==");

// A CRLF + BOM file must survive an edit round-trip byte-for-byte.
var crlfPath = Path.Combine(outDir, "crlf-bom.md");
// No literal U+FEFF here — UTF8Encoding(true) supplies the BOM. Writing both
// would put two BOMs in the file and make this test lie.
var original = "# Title\r\n\r\nLine one.\r\nLine two.\r\n";
await File.WriteAllTextAsync(crlfPath, original, new UTF8Encoding(true));
var originalBytes = await File.ReadAllBytesAsync(crlfPath);

var mf = await MarkdownFile.LoadAsync(crlfPath);
Check("CRLF normalised to LF in memory", !mf.Text.Contains('\r'));
Check("BOM stripped from in-memory text", !mf.Text.StartsWith('﻿'));

await mf.SaveAsync(mf.Text); // save with no edits at all
var afterBytes = await File.ReadAllBytesAsync(crlfPath);
Check("no-op save is byte-identical (BOM + CRLF preserved)",
    originalBytes.SequenceEqual(afterBytes),
    $"{originalBytes.Length} bytes in, {afterBytes.Length} bytes out");

// An LF-only file must stay LF-only.
var lfPath = Path.Combine(outDir, "lf.md");
await File.WriteAllBytesAsync(lfPath, new UTF8Encoding(false).GetBytes("# T\n\nbody\n"));
var lfFile = await MarkdownFile.LoadAsync(lfPath);
await lfFile.SaveAsync(lfFile.Text + "extra\n");
var lfOut = await File.ReadAllTextAsync(lfPath);
Check("LF-only file does not gain CRLF", !lfOut.Contains('\r'));
Check("LF-only file does not gain a BOM", !lfOut.StartsWith('﻿'));

Console.WriteLine();
Console.WriteLine("== MarkdownRenderer ==");

var md = await File.ReadAllTextAsync(samplePath);
var html = MarkdownRenderer.ToHtmlString(md);

Check("renders tables (advanced extensions on)", html.Contains("<table"));
Check("renders task lists", html.Contains("type=\"checkbox\""));
Check("renders footnotes", html.Contains("footnote"));
Check("renders fenced code", html.Contains("<pre><code"));
Check("renders blockquote", html.Contains("<blockquote"));
Check("raw HTML is not passed through (DisableHtml)",
    !MarkdownRenderer.ToHtmlString("<script>alert(1)</script>").Contains("<script>"));

Check("LeadingTitle finds the opening H1",
    MarkdownRenderer.LeadingTitle(md) == "Quarterly Operations Review",
    MarkdownRenderer.LeadingTitle(md));
Check("LeadingTitle skips YAML front matter",
    MarkdownRenderer.LeadingTitle("---\ntitle: x\n---\n\n# Real Title\n") == "Real Title");
Check("LeadingTitle returns null when body starts with prose",
    MarkdownRenderer.LeadingTitle("Just a paragraph.\n\n# Later heading\n") is null);

Console.WriteLine();
Console.WriteLine("== WordExporter ==");

var docBytes = WordExporter.ToDoc("Quarterly Operations Review", html, includeTitleHeading: false);
var docPath = Path.Combine(outDir, "sample.doc");
await File.WriteAllBytesAsync(docPath, docBytes);
var docText = new UTF8Encoding(true).GetString(docBytes);

Check(".doc has the Word namespace header", docText.Contains("urn:schemas-microsoft-com:office:word"));
Check(".doc has the mso conditional block", docText.Contains("w:WordDocument"));
Check(".doc carries a UTF-8 BOM", docBytes[0] == 0xEF && docBytes[1] == 0xBB && docBytes[2] == 0xBF);
Check(".doc contains the rendered table", docText.Contains("<table"));
Check(".doc suppresses duplicate title when asked",
    !docText.Contains("<h1>Quarterly Operations Review</h1>"));
Check(".doc adds a title when asked",
    new UTF8Encoding(true).GetString(WordExporter.ToDoc("T", "<p>x</p>", includeTitleHeading: true))
        .Contains("<h1>T</h1>"));

Console.WriteLine();
Console.WriteLine("== EpubGenerator ==");

var epubBytes = await EpubGenerator.GenerateAsync("Quarterly Operations Review", html, includeTitleHeading: false);
var epubPath = Path.Combine(outDir, "sample.epub");
await File.WriteAllBytesAsync(epubPath, epubBytes);

using (var zip = new ZipArchive(new MemoryStream(epubBytes), ZipArchiveMode.Read))
{
    var names = zip.Entries.Select(e => e.FullName).ToList();

    Check("mimetype is the first entry", zip.Entries[0].FullName == "mimetype");
    Check("mimetype is stored uncompressed",
        zip.Entries[0].CompressedLength == zip.Entries[0].Length);

    using (var r = new StreamReader(zip.Entries[0].Open()))
        Check("mimetype content correct", r.ReadToEnd() == "application/epub+zip");

    Check("has META-INF/container.xml", names.Contains("META-INF/container.xml"));
    Check("has EPUB/content.opf", names.Contains("EPUB/content.opf"));
    Check("has EPUB/nav.xhtml", names.Contains("EPUB/nav.xhtml"));
    Check("has EPUB/chapter-001.xhtml", names.Contains("EPUB/chapter-001.xhtml"));
    Check("has EPUB/style.css", names.Contains("EPUB/style.css"));

    string Read(string entry)
    {
        using var s = new StreamReader(zip.GetEntry(entry)!.Open());
        return s.ReadToEnd();
    }

    var opf = Read("EPUB/content.opf");
    Check("opf declares the title", opf.Contains("<dc:title>Quarterly Operations Review</dc:title>"));
    Check("opf manifest references the chapter", opf.Contains("href=\"chapter-001.xhtml\""));
    Check("opf spine references the chapter", opf.Contains("<itemref idref=\"c001\"/>"));

    var chapter = Read("EPUB/chapter-001.xhtml");
    Check("chapter is well-formed XML", IsWellFormedXml(chapter), FirstXmlError(chapter));
    Check("chapter suppresses duplicate title when asked",
        !chapter.Contains("class=\"chapter-title\""));
    Check("chapter carries the table", chapter.Contains("<table"));
    Check("nav.xhtml is well-formed XML", IsWellFormedXml(Read("EPUB/nav.xhtml")), FirstXmlError(Read("EPUB/nav.xhtml")));
    Check("content.opf is well-formed XML", IsWellFormedXml(opf), FirstXmlError(opf));
}

// Data-URI images must be extracted into real manifest resources.
const string pngB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
var withImage = await EpubGenerator.GenerateAsync("Img", $"<p><img src=\"data:image/png;base64,{pngB64}\" /></p>");
using (var zip = new ZipArchive(new MemoryStream(withImage), ZipArchiveMode.Read))
{
    var names = zip.Entries.Select(e => e.FullName).ToList();
    Check("data-URI image extracted to a file", names.Any(n => n.StartsWith("EPUB/images/img-")));

    using var s = new StreamReader(zip.GetEntry("EPUB/content.opf")!.Open());
    Check("extracted image is in the opf manifest", s.ReadToEnd().Contains("media-type=\"image/png\""));
}

Console.WriteLine();
Console.WriteLine("== EpubDocument: reading back what EpubGenerator wrote ==");

// The reader is the generator inverted, so the strongest single check is that a
// book this app produced opens again with its title, spine and content intact.
var roundTrip = await EpubDocument.LoadAsync(epubPath);

Check("round-trip: title recovered from the OPF",
    roundTrip.Title == "Quarterly Operations Review", roundTrip.Title);
Check("round-trip: one spine document", roundTrip.Chapters.Count == 1, $"{roundTrip.Chapters.Count}");
Check("round-trip: contents built from nav.xhtml", roundTrip.Contents.Count == 1);
Check("round-trip: contents entry points at the chapter",
    roundTrip.Contents.Count > 0 && roundTrip.Contents[0].ChapterIndex == 0);

var roundTripHtml = await roundTrip.ChapterHtmlAsync(0);
Check("round-trip: chapter body survives", roundTripHtml.Contains("<table"));
Check("round-trip: the book's own stylesheet link is gone", !roundTripHtml.Contains("<link"));

Console.WriteLine();
Console.WriteLine("== EpubDocument: EPUB 2 (toc.ncx, no nav document) ==");

// EpubGenerator only ever emits EPUB 3, so this format has no round-trip to lean
// on and needs a book built by hand.
var epub2Path = Path.Combine(outDir, "epub2.epub");
await File.WriteAllBytesAsync(epub2Path, BuildEpub2());
var epub2 = await EpubDocument.LoadAsync(epub2Path);

Check("epub2: title from dc:title", epub2.Title == "An Older Book", epub2.Title);
Check("epub2: author from dc:creator", epub2.Author == "A. Writer", epub2.Author);
Check("epub2: both spine documents found", epub2.Chapters.Count == 2, $"{epub2.Chapters.Count}");
Check("epub2: NCX supplied the chapter titles",
    epub2.Contents.Count >= 2 && epub2.Contents[0].Title == "The Beginning",
    epub2.Contents.FirstOrDefault()?.Title);
Check("epub2: nested navPoint keeps its depth",
    epub2.Contents.Any(e => e.Depth == 1 && e.Title == "A Sub-section"));
Check("epub2: nested navPoint keeps its fragment",
    epub2.Contents.Any(e => e.Fragment == "sub"));

var epub2Ch1 = await epub2.ChapterHtmlAsync(0);
Check("epub2: image resolved through ../ and inlined as a data URI",
    epub2Ch1.Contains("src=\"data:image/png;base64,"), epub2Ch1);
Check("epub2: link to another chapter became a chapter reference",
    epub2Ch1.Contains("data-epub-chapter=\"1\""), epub2Ch1);
Check("epub2: percent-encoded href still resolves",
    (await epub2.ChapterHtmlAsync(1)).Contains("data-epub-chapter=\"0\""));

Console.WriteLine();
Console.WriteLine("== EpubHtml: sanitising untrusted chapter markup ==");

// Everything below reaches the DOM through MarkupString in the same WebView that
// hosts Blazor with .NET interop, so each of these is a live scripting vector.
static async Task<string> Clean(string html) =>
    await EpubHtml.ToSafeBodyAsync(html, _ => null, _ => 1);

var scripted = await Clean("<p>before</p><script>alert(1)</script><p>after</p>");
Check("script element removed", !scripted.Contains("alert(1)"), scripted);
Check("text either side of it survives", scripted.Contains("before") && scripted.Contains("after"));

Check("inline event handler removed",
    !(await Clean("<p onclick=\"alert(1)\">x</p>")).Contains("onclick"));
Check("error handler on an image removed",
    !(await Clean("<img src=\"nope.png\" onerror=\"alert(1)\">")).Contains("onerror"));
Check("javascript: href removed",
    !(await Clean("<a href=\"javascript:alert(1)\">x</a>")).Contains("javascript:"));
Check("javascript: link keeps its text",
    (await Clean("<a href=\"javascript:alert(1)\">click me</a>")).Contains("click me"));
Check("iframe removed",
    !(await Clean("<iframe src=\"https://example.com\"></iframe>")).Contains("iframe"));
Check("svg removed (it can carry script and foreignObject)",
    !(await Clean("<svg><script>alert(1)</script></svg>")).Contains("alert(1)"));
Check("form controls removed",
    !(await Clean("<form><input name=\"pw\"></form>")).Contains("<input"));

Check("book stylesheet link dropped",
    !(await Clean("<link rel=\"stylesheet\" href=\"style.css\"><p>x</p>")).Contains("<link"));
Check("style element dropped",
    !(await Clean("<style>p{color:red}</style><p>x</p>")).Contains("color:red"));
Check("style attribute dropped",
    !(await Clean("<p style=\"color:red\">x</p>")).Contains("style="));

Check("remote image is not fetched",
    !(await Clean("<img src=\"https://tracker.example/pixel.gif\">")).Contains("tracker.example"));
Check("remote image keeps its alt text",
    (await Clean("<img src=\"https://tracker.example/p.gif\" alt=\"a diagram\">")).Contains("a diagram"));

Check("external link becomes a handled reference, not a live href",
    (await Clean("<a href=\"https://example.com/x\">x</a>")) is var ext &&
    ext.Contains("data-epub-external=\"https://example.com/x\"") && !ext.Contains("href="));
Check("same-chapter anchor keeps its href",
    (await Clean("<a href=\"#note1\">1</a>")).Contains("href=\"#note1\""));

// Cover pages are almost always an <image> wrapped in an <svg>, and the <svg> is
// discarded, so the bitmap has to be lifted out or the cover vanishes.
var cover = await EpubHtml.ToSafeBodyAsync(
    "<svg viewBox=\"0 0 600 800\"><image xlink:href=\"cover.jpg\"/></svg>",
    href => href == "cover.jpg" ? [1, 2, 3] : null,
    _ => null);
Check("cover art survives the svg being discarded",
    cover.Contains("<img") && cover.Contains("data:image/jpeg;base64,"), cover);
Check("the svg wrapper itself is gone", !cover.Contains("<svg"), cover);

Check("unknown element is unwrapped, not dropped",
    (await Clean("<weirdwrapper><p>kept</p></weirdwrapper>")) is var unwrapped &&
    unwrapped.Contains("kept") && !unwrapped.Contains("weirdwrapper"));
Check("ordinary prose passes through",
    (await Clean("<h2>Title</h2><p>Some <em>emphasis</em>.</p>")) is var prose &&
    prose.Contains("<h2>") && prose.Contains("<em>"));

Console.WriteLine();
Console.WriteLine("== ExportPaths ==");

// Resolve "beside the source" against a directory of our own rather than the
// folder sample.md lives in. Exporting sample.md from the app leaves a
// sample.pdf next to it, and ExportPaths would then correctly return
// "sample (2).pdf" — failing this check for a reason that has nothing to do
// with the code under test.
var besideDir = Path.Combine(outDir, "beside");
if (Directory.Exists(besideDir)) Directory.Delete(besideDir, recursive: true);
Directory.CreateDirectory(besideDir);
var besideSrc = Path.Combine(besideDir, "sample.md");
File.Copy(samplePath, besideSrc, overwrite: true);

var beside = ExportPaths.For(besideSrc, "ignored", ".pdf");
Check("export lands beside the source file",
    Path.GetDirectoryName(beside) == Path.GetDirectoryName(Path.GetFullPath(besideSrc)),
    beside);
Check("export reuses the source base name", Path.GetFileName(beside) == "sample.pdf", Path.GetFileName(beside));

var collide = Path.Combine(outDir, "collide.pdf");
await File.WriteAllTextAsync(collide, "x");
var unique = ExportPaths.For(Path.Combine(outDir, "collide.md"), "x", ".pdf");
Check("never overwrites an existing export", Path.GetFileName(unique) == "collide (2).pdf", Path.GetFileName(unique));

Check("unsaved doc falls back to Documents",
    Path.GetDirectoryName(ExportPaths.For(null, "My Doc", ".pdf")) == ExportPaths.DocumentsFolder());
Check("illegal filename characters stripped",
    !ExportPaths.Sanitize("a/b:c*d?.md").Intersect(Path.GetInvalidFileNameChars()).Any());

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
Console.WriteLine($"artifacts in {outDir}");
return fail == 0 ? 0 : 1;

/// <summary>
/// A hand-built EPUB 2 book: toc.ncx instead of a nav document, a DOCTYPE on the
/// NCX (which the default .NET XML reader settings reject outright), an image
/// referenced through "../", and a percent-encoded cross-chapter link. Everything
/// EpubGenerator never produces, and therefore everything the round-trip test
/// above cannot reach.
/// </summary>
static byte[] BuildEpub2()
{
    const string dotPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
    {
        void Add(string path, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        Add("mimetype", "application/epub+zip");

        Add("META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

        Add("OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="uid">urn:uuid:test</dc:identifier>
                <dc:title>An Older Book</dc:title>
                <dc:creator>A. Writer</dc:creator>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="dot" href="images/dot.png" media-type="image/png"/>
              </manifest>
              <spine toc="ncx">
                <itemref idref="c1"/>
                <itemref idref="c2"/>
              </spine>
            </package>
            """);

        Add("OEBPS/toc.ncx", """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE ncx PUBLIC "-//NISO//DTD ncx 2005-1//EN" "http://www.daisy.org/z3986/2005/ncx-2005-1.dtd">
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
              <docTitle><text>An Older Book</text></docTitle>
              <navMap>
                <navPoint id="n1" playOrder="1">
                  <navLabel><text>The Beginning</text></navLabel>
                  <content src="text/ch1.xhtml"/>
                  <navPoint id="n1a" playOrder="2">
                    <navLabel><text>A Sub-section</text></navLabel>
                    <content src="text/ch1.xhtml#sub"/>
                  </navPoint>
                </navPoint>
                <navPoint id="n2" playOrder="3">
                  <navLabel><text>The End</text></navLabel>
                  <content src="text/ch2.xhtml"/>
                </navPoint>
              </navMap>
            </ncx>
            """);

        Add("OEBPS/text/ch1.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
            <head><title>Chapter One</title><link rel="stylesheet" href="../style.css"/></head>
            <body>
              <h1>The Beginning</h1>
              <p>Opening lines.</p>
              <p><img src="../images/dot.png" alt="a dot"/></p>
              <h2 id="sub">A Sub-section</h2>
              <p><a href="ch2.xhtml">Onwards</a>.</p>
            </body>
            </html>
            """);

        Add("OEBPS/text/ch2.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
            <head><title>Chapter Two</title></head>
            <body>
              <h1>The End</h1>
              <p><a href="ch%31.xhtml">Back to the start</a>.</p>
            </body>
            </html>
            """);

        var png = zip.CreateEntry("OEBPS/images/dot.png");
        using var s = png.Open();
        var bytes = Convert.FromBase64String(dotPng);
        s.Write(bytes, 0, bytes.Length);
    }
    return ms.ToArray();
}

static bool IsWellFormedXml(string xml)
{
    try { System.Xml.Linq.XDocument.Parse(xml); return true; }
    catch (Exception) { return false; }
}

static string? FirstXmlError(string xml)
{
    try { System.Xml.Linq.XDocument.Parse(xml); return null; }
    catch (Exception ex) { return ex.Message; }
}
