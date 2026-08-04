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
Console.WriteLine("== ExportPaths ==");

var beside = ExportPaths.For(samplePath, "ignored", ".pdf");
Check("export lands beside the source file",
    Path.GetDirectoryName(beside) == Path.GetDirectoryName(Path.GetFullPath(samplePath)),
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
