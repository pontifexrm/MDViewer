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
Console.WriteLine("== Task list toggling ==");

const string tasks = "# Todo\n\n- [ ] one\n- [x] two\n  - [X] nested\n\n1. [ ] numbered\n\nNot a task: [ ] here\n\n```\n- [ ] in code\n```\n";
Check("first task ticks", MarkdownRenderer.ToggleTask(tasks, 0, false) == tasks.Replace("- [ ] one", "- [x] one"));
Check("ticked task unticks", MarkdownRenderer.ToggleTask(tasks, 1, true) == tasks.Replace("- [x] two", "- [ ] two"));
Check("nested capital-X task unticks", MarkdownRenderer.ToggleTask(tasks, 2, true) == tasks.Replace("[X] nested", "[ ] nested"));
Check("ordered-list task ticks", MarkdownRenderer.ToggleTask(tasks, 3, false) == tasks.Replace("1. [ ]", "1. [x]"));
Check("brackets outside a list and in code are not tasks", MarkdownRenderer.ToggleTask(tasks, 4, false) is null);
Check("stale state is refused", MarkdownRenderer.ToggleTask(tasks, 0, true) is null);
Check("negative index is refused", MarkdownRenderer.ToggleTask(tasks, -1, false) is null);

var taskHtml = MarkdownRenderer.ToHtmlString(tasks);
var clickable = MarkdownRenderer.WithClickableTasks(taskHtml);
Check("rendered checkboxes count matches the tasks",
    System.Text.RegularExpressions.Regex.Matches(taskHtml, "type=\"checkbox\"").Count == 4);
Check("clickable HTML has no disabled checkboxes", !clickable.Contains("disabled"), clickable);
Check("clickable HTML keeps the ticked state",
    System.Text.RegularExpressions.Regex.Matches(clickable, "checked=\"checked\"").Count == 2);

var screen = MarkdownRenderer.ForScreen("# Head\n\nPara one\nstill one\n\n- item\n\n```\ncode\n```\n");
Check("screen HTML tags the heading with line 0", screen.Contains("<h1 id=\"head\" data-line=\"0\">"), screen);
Check("screen HTML tags a paragraph with its first line", screen.Contains("<p data-line=\"2\">"), screen);
Check("screen HTML tags list items", screen.Contains("<li data-line=\"5\">"), screen);
Check("screen HTML tags code blocks", screen.Contains("<pre><code data-line=\"7\">"), screen);
Check("screen HTML has clickable tasks", !MarkdownRenderer.ForScreen(tasks).Contains("disabled"));
Check("export HTML carries no line tags", !html.Contains("data-line"));

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

// A book's class names style nothing once its stylesheet is gone, but they still
// collide: a documentation ePub wrapping chapters in <div class="content"> picked
// up the app shell's own .content { display: flex } and laid every paragraph out
// as a column. class="no-print" would have dropped chapters from a printout.
Check("class attribute dropped, so book classes cannot collide with the app's",
    !(await Clean("<div class=\"content\"><p>x</p></div>")).Contains("class="));
Check("dropping the class keeps the element and its text",
    (await Clean("<div class=\"content\"><p>kept</p></div>")) is var declassed &&
    declassed.Contains("<div>") && declassed.Contains("kept"));
Check("id survives, because intra-chapter anchors need it",
    (await Clean("<h2 id=\"the-mindset\">M</h2>")).Contains("id=\"the-mindset\""));

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
Console.WriteLine("== ReadingPositions ==");

// Somewhere disposable, not the user's real app data.
var posDir = Path.Combine(outDir, "positions");
if (Directory.Exists(posDir)) Directory.Delete(posDir, recursive: true);
ReadingPositions.StorageFolder = posDir;

Check("unknown book has no stored position", ReadingPositions.For(epub2Path) is null);

await ReadingPositions.SaveAsync(epub2Path, chapter: 1, block: 7, fraction: 0.25);
var stored = ReadingPositions.For(epub2Path);
Check("position round-trips", stored is { Chapter: 1, Block: 7 }, $"{stored?.Chapter}/{stored?.Block}");
Check("fraction round-trips", stored is not null && Math.Abs(stored.Fraction - 0.25) < 0.0001);

await ReadingPositions.SaveAsync(epub2Path, chapter: 2, block: 0, fraction: 0);
Check("saving again replaces rather than duplicates",
    ReadingPositions.For(epub2Path) is { Chapter: 2, Block: 0 });

Check("lookup ignores path casing",
    ReadingPositions.For(epub2Path.ToUpperInvariant()) is { Chapter: 2 });
Check("lookup normalises a relative path",
    ReadingPositions.For(Path.Combine(Path.GetDirectoryName(epub2Path)!, ".", "epub2.epub")) is { Chapter: 2 });

// Two books must not share an entry.
await ReadingPositions.SaveAsync(epubPath, chapter: 0, block: 3, fraction: 0);
Check("books are tracked separately",
    ReadingPositions.For(epub2Path) is { Chapter: 2 } && ReadingPositions.For(epubPath) is { Block: 3 });

await ReadingPositions.ForgetAsync(epub2Path);
Check("a forgotten book is gone", ReadingPositions.For(epub2Path) is null);
Check("forgetting one leaves the others", ReadingPositions.For(epubPath) is { Block: 3 });

// A half-written or hand-mangled file must not stop a book from opening.
await File.WriteAllTextAsync(Path.Combine(posDir, "reading-positions.json"), "{ not json at all");
Check("corrupt store degrades to no position rather than throwing",
    ReadingPositions.For(epubPath) is null);
await ReadingPositions.SaveAsync(epubPath, chapter: 4, block: 1, fraction: 0.5);
Check("corrupt store is replaced by the next save",
    ReadingPositions.For(epubPath) is { Chapter: 4 });

// The file is capped, so a heavy reader's store cannot grow without bound.
for (var i = 0; i < 240; i++)
    await ReadingPositions.SaveAsync(Path.Combine(posDir, $"book{i}.epub"), i, 0, 0);
var kept = Enumerable.Range(0, 240)
    .Count(i => ReadingPositions.For(Path.Combine(posDir, $"book{i}.epub")) is not null);
Check("store is capped at 200 books", kept <= 200, $"{kept} kept");
Check("the most recent books are the ones kept",
    ReadingPositions.For(Path.Combine(posDir, "book239.epub")) is not null);

Console.WriteLine();
Console.WriteLine("== FileStamp: noticing an outside edit ==");

var watchPath = Path.Combine(outDir, "watched.md");
await File.WriteAllTextAsync(watchPath, "# One\n\nBody.\n");
var watched = await MarkdownFile.LoadAsync(watchPath);

Check("a loaded file carries a stamp", watched.Stamp is not null);
Check("an untouched file looks unchanged", FileStamp.Of(watchPath) == watched.Stamp);

// Something else rewrites it. Same length as well as different, since a stamp on
// last-write time alone would miss an edit that happened to preserve the size.
await Task.Delay(20);
await File.WriteAllTextAsync(watchPath, "# One\n\nBodY.\n");
Check("an outside edit of the same length is noticed",
    FileStamp.Of(watchPath) != watched.Stamp);

// The app's own save must not look like someone else's edit, or the viewer would
// prompt to reload the document the user just wrote.
var saver = await MarkdownFile.LoadAsync(watchPath);
await saver.SaveAsync(saver.Text + "\nMore.\n");
Check("the app's own save re-stamps", FileStamp.Of(watchPath) == saver.Stamp);

// Save As points the stamp at the new file, not the one left behind.
var savedAsPath = Path.Combine(outDir, "watched-copy.md");
await saver.SaveAsync(saver.Text, savedAsPath);
var movedOn = saver.At(savedAsPath);
Check("Save As stamps the new file", FileStamp.Of(savedAsPath) == movedOn.Stamp);

Check("a missing file has no stamp", FileStamp.Of(Path.Combine(outDir, "not-here.md")) is null);
Check("an unsaved document has no stamp", MarkdownFile.Untitled().Stamp is null);

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
Console.WriteLine("== HtmlToMarkdown: smart paste ==");

static string Md(string html) => HtmlToMarkdown.Convert(html)?.Markdown ?? "<null>";

// A browser: already-semantic HTML.
var web = Md("""
    <h2>Title</h2>
    <p>Some <strong>bold</strong> and <em>italic</em> with a <a href="https://example.com/a b">link</a>.</p>
    <ul><li>one</li><li>two<ul><li>nested</li></ul></li></ul>
    <ol start="3"><li>three</li><li>four</li></ol>
    """);
Check("web: heading", web.Contains("## Title"), web);
Check("web: bold, italic and link inline",
    web.Contains("Some **bold** and *italic* with a [link](https://example.com/a%20b)."), web);
Check("web: nested list indented under its item", web.Contains("- one\n- two\n  - nested"), web);
Check("web: ordered list keeps its start number", web.Contains("3. three\n4. four"), web);

// Word: lists are styled paragraphs with the bullet as literal text, wrapped in
// conditional comments, with the document's stylesheet in front.
var word = Md("""
    <html xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:w="urn:schemas-microsoft-com:office:word">
    <head><style>p.MsoNormal { margin:0cm; }</style></head>
    <body lang=EN-NZ>
    <!--StartFragment-->
    <h1>Plan<o:p></o:p></h1>
    <p class=MsoListParagraphCxSpFirst style='text-indent:-18.0pt;mso-list:l0 level1 lfo1'><![if !supportLists]><span style='font-family:Symbol'><span style='mso-list:Ignore'>·<span style='font:7.0pt "Times New Roman"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp; </span></span></span><![endif]>First <b>point</b><o:p></o:p></p>
    <p class=MsoListParagraphCxSpMiddle style='mso-list:l0 level2 lfo1'><![if !supportLists]><span style='font-family:"Courier New"'><span style='mso-list:Ignore'>o<span style='font:7.0pt "Times New Roman"'>&nbsp;&nbsp; </span></span></span><![endif]>Sub point<o:p></o:p></p>
    <p class=MsoListParagraphCxSpLast style='mso-list:l0 level1 lfo1'><![if !supportLists]><span style='font-family:Symbol'><span style='mso-list:Ignore'>·<span>&nbsp;&nbsp; </span></span></span><![endif]>Second point<o:p></o:p></p>
    <p class=MsoNormal><o:p>&nbsp;</o:p></p>
    <p class=MsoListParagraph style='mso-list:l1 level1 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>2.<span>&nbsp;&nbsp; </span></span><![endif]>Numbered<o:p></o:p></p>
    <table class=MsoTableGrid border=1>
     <tr><td><p class=MsoNormal><b>Name</b></p></td><td><p class=MsoNormal><b>Qty</b></p></td></tr>
     <tr><td><p class=MsoNormal>Apples</p></td><td><p class=MsoNormal>3</p></td></tr>
    </table>
    <!--EndFragment-->
    </body></html>
    """);
Check("word: heading", word.StartsWith("# Plan\n"), word);
Check("word: list paragraphs become a nested list",
    word.Contains("- First **point**\n  - Sub point\n- Second point"), word);
Check("word: numbered paragraph becomes an ordered list from its number", word.Contains("2. Numbered"), word);
Check("word: no bullet glyphs, stylesheet or nbsp left behind",
    !word.Contains('·') && !word.Contains("MsoNormal") && !word.Contains(' '), word);
Check("word: table header row is not double-bolded", word.Contains("| Name") && !word.Contains("**Name**"), word);
var wordHtml = MarkdownRenderer.ToHtmlString(word);
Check("word: output renders as a real nested list",
    wordHtml.Split("<ul>").Length - 1 == 2 && wordHtml.Contains("<strong>point</strong>"), wordHtml);
Check("word: output renders as a real table", wordHtml.Contains("<table") && wordHtml.Contains("Apples"));

// Google Docs: the whole copy sits in a <b> whose style says it is not bold, and
// the real formatting is font-weight on spans.
var gdocs = Md("""
    <meta charset="utf-8"><b style="font-weight:normal;" id="docs-internal-guid-1a2b">
    <h1 dir="ltr"><span style="font-size:20pt;font-weight:400;">Heading</span></h1>
    <p dir="ltr"><span style="font-weight:400;">Plain then </span><span style="font-weight:700;">bold</span><span style="font-style:italic;font-weight:400;"> italic</span></p>
    <ul><li dir="ltr"><p dir="ltr"><span style="font-weight:400;">item</span></p></li></ul>
    </b>
    """);
Check("gdocs: heading inside the wrapper", gdocs.Contains("# Heading"), gdocs);
Check("gdocs: the not-bold wrapper is not bold", !gdocs.Contains("**Plain"), gdocs);
Check("gdocs: span weights and styles become emphasis", gdocs.Contains("Plain then **bold** *italic*"), gdocs);
Check("gdocs: list item with a paragraph inside", gdocs.Contains("- item"), gdocs);

var escaped = Md("<p><b>x</b> Use * and _x_ and snake_case and ~5</p><p># not a heading</p><p>1. not a list</p>");
Check("escaping: markdown characters in text", escaped.Contains(@"Use \* and \_x\_ and snake_case and \~5"), escaped);
Check("escaping: a line starting like a heading", escaped.Contains(@"\# not a heading"), escaped);
Check("escaping: a line starting like a numbered item", escaped.Contains(@"1\. not a list"), escaped);

Check("adjacent bold runs merge", Md("<p><b>a</b><b>b</b></p>") == "**ab**", Md("<p><b>a</b><b>b</b></p>"));
Check("emphasis keeps its spaces outside the markers",
    Md("<p>a<b> b </b>c</p>") == "a **b** c", Md("<p>a<b> b </b>c</p>"));
Check("br is a hard line break", Md("<p><b>a</b><br>b</p>") == "**a**\\\nb", Md("<p><b>a</b><br>b</p>"));

var fenced = Md("<pre><code class=\"language-cs\">var x = 1;\nif (a) { }</code></pre>");
Check("pre becomes a fenced block with its language", fenced == "```cs\nvar x = 1;\nif (a) { }\n```", fenced);
Check("inline code", Md("<p>run <code>dotnet *run*</code></p>") == "run `dotnet *run*`");

var spans = Md("<table><tr><td>A</td><td>B</td></tr><tr><td colspan=2>a|b</td></tr></table>");
var spanLines = spans.Split('\n');
Check("colspan keeps every row the same width", spanLines.Length == 3 && spanLines[2].Count(c => c == '|') == 4, spans);
Check("pipe in a cell is escaped", spans.Contains(@"a\|b"), spans);
var layout = Md("<table><tr><td><h2>News</h2><p>body</p></td></tr></table>");
Check("one-column layout table becomes its content", layout == "## News\n\nbody", layout);

var mail = HtmlToMarkdown.Convert("<p><b>Hi</b></p><img src=\"cid:image001.png\"><img src=\"https://t.example/p.gif\" width=\"1\" height=\"1\">");
Check("cid: image counted as dropped", mail?.ImagesDropped == 1, $"{mail?.ImagesDropped}");
Check("tracking pixel left out", mail is not null && !mail.Markdown.Contains("t.example"), mail?.Markdown);
Check("web image kept", Md("<p><img src=\"https://example.com/a.png\" alt=\"A chart\"></p>") == "![A chart](https://example.com/a.png)");
Check("javascript: link keeps only its text", Md("<p><b>x</b> <a href=\"javascript:alert(1)\">y</a></p>") == "**x** y");

Check("unstructured HTML defers to the plain text", HtmlToMarkdown.Convert("<div>just <span>text</span></div>") is null);
Check("VS Code's white-space:pre copy defers to the plain text", HtmlToMarkdown.Convert("""
    <div style="color: #cccccc;font-family: Consolas;font-weight: normal;white-space: pre;"><div><span style="color: #569cd6;font-weight: bold;"># Heading</span></div><div><span>- </span><span style="font-weight: bold;">item</span></div></div>
    """) is null);

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
