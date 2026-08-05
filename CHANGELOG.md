# Changelog

## 1.0.4 — 2026-08-05

First public release.

A Windows desktop viewer for `.md` files: double-click a markdown file and read it
rendered, with editing, printing, and PDF, Word and ePub export.

- Rendering matches the our internal knowledge base knowledge base pipeline (Markdig,
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
  the `.msix` will install. `Install.ps1` does both and self-elevates.
- Release publishes generate no symbols package while `mspdbcmf.exe` is absent.
- `MDViewer.csproj` carries two targets working around a `maui-windows` 10.0.20
  workload bug that omits the Blazor host assets from build output and the MSIX
  payload. They can be deleted once the workload is fixed upstream.
