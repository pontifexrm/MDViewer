# Changelog

## Unreleased

### Reads ePub

The app already wrote ePub; it now opens them too. Selecting a `.epub` — from the
picker, the file association, or the command line — switches to a reader: contents
sidebar, one chapter at a time, Prev/Next through the spine.

- Scrolling only. No page turns and no font controls: WebView2's own zoom already
  reflows the text, and this only runs on a desktop.
- Both table-of-contents formats are read — EPUB 3 `nav.xhtml` and EPUB 2
  `toc.ncx` — so books predating 2015 open with real chapter titles rather than
  "Chapter 7". Nesting depth is kept, and contents entries pointing *into* a
  chapter scroll to the right place.
- Chapter markup goes through an allow-list sanitiser (`Services/EpubHtml.cs`)
  before it reaches the DOM. Markdown's `DisableHtml` rule cannot apply to a format
  that is nothing but authored HTML, and EPUB 3 permits scripting, so elements and
  attributes not explicitly allowed are removed instead.
- Links never navigate the WebView away from the running app: internal links become
  in-app chapter navigation, `http(s)` links open in the default browser, and every
  other scheme is dropped.
- The book's own CSS is dropped and chapters render through the app's own styles,
  so custom fonts, drop caps and verse indentation are lost. Remote images are not
  fetched. SVG and MathML are discarded, though cover art survives.
- Editing and Save are hidden in reader mode. Print and PDF act on the current
  chapter and still land beside the book.
- Registered as a handler for `.epub`, separately from the markdown association, so
  it can be the default for one and not the other.
- 35 new harness checks (43 → 78), including a round-trip that reads back a book the app's
  own generator wrote, a hand-built EPUB 2, and the sanitiser's scripting vectors.

## 1.0.4 — 2026-08-05

First public release.

A Windows desktop viewer for `.md` files: double-click a markdown file and read it
rendered, with editing, printing, and PDF, Word and ePub export.

- Rendering matches our internal knowledge base pipeline (Markdig,
  `UseAdvancedExtensions().DisableHtml()`), so a file looks here exactly as it would
  after being pasted into a KB article. Inline HTML stays literal by design.
- Editing works on the markdown source rather than rich text, so saving is a
  byte-for-byte write with BOM and CRLF preserved.
- Print and every export render through the `@media print` rules in `app.css`, so
  output is the document alone — no toolbar, no editor pane.
- Exports land beside the source file and never overwrite; a name collision gets a
  ` (2)` suffix. Unsaved documents fall back to Documents.
- Registered as a handler for `.md`, `.markdown`, `.mdown`, `.mkd` and `.mdtext`.

### Fixed since the first working build

- The rendered document's title no longer draws a focus outline. Blazor's
  `FocusOnNavigate` was focusing the first `h1` on load, so Chromium painted its
  default focus ring around it.
- Debug and Release package versions no longer collide, so debugging in Visual
  Studio stopped prompting to uninstall the release build on every run.

### Housekeeping

- Dropped 808 KB of unreferenced Bootstrap CSS from `wwwroot`, taking 130 KB off
  the signed package.
- Replaced the .NET template icon and splash with a rendered-page mark.
- Package version scheme is now `1.0.4.0` — the fourth part stays `0`, which the
  Microsoft Store requires on submission.
- Builds are pinned to the GA .NET 10.0.1xx SDK via `global.json`.

### Known issues

- The installer certificate is self-signed, so the `.cer` must be trusted before
  the `.msix` will install, which requires an elevated prompt.
- The package depends on `Microsoft.WindowsAppRuntime.1.7`, which ships as a
  release asset and must be installed first on a machine that lacks it.
- `Install.ps1` expects the build output's `Dependencies\x64\` folder layout, so it
  does not work against release assets downloaded flat into one folder.
- Release publishes generate no symbols package while `mspdbcmf.exe` is absent.
- `MDViewer.csproj` carries two targets working around a `maui-windows` 10.0.20
  workload bug that omits the Blazor host assets from build output and the MSIX
  payload. They can be deleted once the workload is fixed upstream.
