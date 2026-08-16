# Markdown Viewer

[![Latest release](https://img.shields.io/github/v/release/pontifexrm/MDViewer?label=download&color=2B579A)](https://github.com/pontifexrm/MDViewer/releases/latest)
[![Licence](https://img.shields.io/github/license/pontifexrm/MDViewer?color=2B579A)](LICENSE)

A Windows desktop viewer for `.md` files — double-click a markdown file and read it
rendered, with print, PDF, Word and ePub output. It also reads `.epub` books.
.NET MAUI Blazor Hybrid (WebView2).

### ⬇ [Download the latest release](https://github.com/pontifexrm/MDViewer/releases/latest)

Windows 10 1809 (build 17763) or later, x64. The package is signed with a
self-signed certificate, so its certificate has to be trusted before Windows will
install it — three commands from an elevated prompt, see
[Install a release](#install-a-release).

The rendering pipeline is the same one our internal knowledge base uses
(Markdig, `UseAdvancedExtensions().DisableHtml()`), so a file looks here exactly as
it would after being pasted into a KB article. The ePub generator is ported from
that knowledge base's own ePub generator.

## What it does

| Action | Notes |
|---|---|
| Open | File picker, or launch with a `.md` or `.epub` (file association / command line) |
| Edit | Split pane: markdown source left, live preview right |
| Save / Save As | Writes the markdown source back **losslessly** — BOM and CRLF preserved |
| Print | WebView2 print preview (its "Save as PDF" destination works too) |
| PDF | `CoreWebView2.PrintToPdfAsync` — no PDF library involved |
| Word | HTML wrapped in the Office document header; opens straight into Word |
| ePub out | Valid ePub 3; Kindle (2022+), Kobo, Apple Books |
| ePub in | Opens `.epub` for reading — contents sidebar, chapter by chapter |

Exports are written **beside the source file** with the same base name, never
overwriting — an existing name gets ` (2)`. If the document was never saved, they
go to Documents. The status line offers "Show in folder".

Printing and all exports render through the `@media print` rules in
`wwwroot/app.css`, so output is the document alone — no toolbar, no editor pane.

Editing deliberately edits the **markdown source**, not rich text. Saving is then a
byte-for-byte write with no HTML→Markdown conversion, which would otherwise quietly
reformat tables, code fences and footnotes.

Inline HTML in markdown is **not** rendered (`DisableHtml`) — it shows as literal
text. This is deliberate: the app opens files from anywhere, and script in a
WebView with .NET interop is not a risk worth taking for the formatting.

Each `.md` opens its own window (the Notepad/Word model). There is no
single-instance redirection.

## Reading ePub

Opening a `.epub` puts the app in reader mode: a contents sidebar on the left, one
chapter at a time in the pane, and Prev/Next through the spine. It scrolls — there
are no page turns and no font controls, because WebView2's own zoom (Ctrl+`+`,
Ctrl+scroll) already reflows the text and this only ever runs on a desktop.
Editing and Save are hidden; Print and PDF stay, and act on the current chapter.

Reopening a book returns to where you left off. The position is a **chapter plus
the index of a block within it**, not a scroll offset in pixels — zooming or
resizing reflows the text and would invalidate a pixel offset, which is the same
reason a paginated reader has to store a CFI rather than a page number. It is
written on a debounced scroll rather than at window close, so being killed does
not lose it, and lives in one JSON file under `%LOCALAPPDATA%\MDViewer` — books
are keyed by path, so moving a file forgets its position.

`Services/EpubDocument.cs` is `EpubGenerator` run backwards — same container/OPF
structure — plus the two things the generator never has to handle because it only
reads back what it wrote: **EPUB 2** (a `toc.ncx` and no nav document, which is
most books that predate 2015) and hrefs that are percent-encoded, walk upwards, or
disagree with the ZIP entry on casing.

`Services/EpubHtml.cs` is the part to be careful with. Markdown gets `DisableHtml`
because script in a WebView with .NET interop is not worth the formatting — but an
ePub *is* authored HTML, and EPUB 3 permits scripting outright, so that rule cannot
hold in reader mode. What replaces it is an **allow-list**: elements and attributes
not named in that file are removed, so anything nobody anticipated fails closed.
Consequences worth knowing before changing it:

- Links never navigate the WebView. A live `href` would replace the running app
  with the linked page, so hrefs are stripped and replaced with data attributes
  that `wwwroot/epub.js` turns into in-app chapter navigation. `http(s)` links go
  to the default browser (scheme re-checked in .NET before the shell launch, since
  the URL came out of a stranger's file); every other scheme is dropped.
- The book's own CSS is dropped entirely — `<style>`, `<link>` and `style=`.
  Chapters render through the app's `.doc-render` rules, so custom fonts, drop caps
  and verse indentation are lost. This is a deliberate trade, not an oversight.
- Remote resources are never fetched: an `<img>` pointing at a web server is a
  tracking pixel that would report when the file was opened.
- SVG and MathML are discarded. Cover art survives, because an `<image>` inside an
  `<svg>` is lifted out to a plain `<img>` first.

Images are inlined as `data:` URIs — the exact inverse of what `EpubGenerator` does
on the way out — so nothing is unpacked to a temp folder and the WebView needs no
mapping into the archive. The whole file is held in memory rather than kept open,
so reading a book does not lock it against being moved or deleted.

## Layout

```
MDViewer.sln        both projects
MDViewer/           the MAUI Blazor Hybrid app
MDViewer.Harness/   test harness — compiles the app's service files directly
```

Paths in the rest of this README are relative to the repo root unless a `cd` says
otherwise. Build commands name the project folder rather than the solution,
because publishing the MSIX has to target the app project on its own.

## Install a release

From [Releases](https://github.com/pontifexrm/MDViewer/releases), download
`MDViewer_<ver>_x64.msix`, `MDViewer_<ver>_x64.cer` and
`Microsoft.WindowsAppRuntime.1.7.msix` into one folder. The package is signed with a
self-signed certificate, so Windows will not install it until that certificate is
trusted — which needs an elevated prompt:

```powershell
Import-Certificate -FilePath .\MDViewer_1.0.4.0_x64.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\Microsoft.WindowsAppRuntime.1.7.msix
Add-AppxPackage .\MDViewer_1.0.4.0_x64.msix
```

The runtime is a declared dependency of the package (`Microsoft.WindowsAppRuntime.1.7`)
and the install fails without it, so it ships as a release asset. Skip that line if
the machine already has it.

## Build

`global.json` pins the SDK to the GA 10.0.1xx band with `allowPrerelease: false`.
Without it `dotnet` picks the highest installed SDK, which on this machine is a
10.0.4xx *preview* — every build then logs `NETSDK1057`, and release artifacts
would come off a preview toolchain. The `maui-windows` workload is present in both
bands, so the pin costs nothing.

```bash
dotnet build MDViewer -c Debug            # packaged (MSIX) — needs Developer Mode to run
dotnet build MDViewer -c Debug -p:WindowsPackageType=None   # unpackaged; will NOT run, see below
```

Debug builds are versioned `<display>.9999` — currently `1.0.4.9999` — by a
config-conditioned `ApplicationVersion`
so an F5 deploy sorts *above* the installed release and Visual Studio stops
prompting to uninstall it on every run. Both configurations still share one package
identity, so they cannot be installed side by side — a Debug deploy replaces the
release, and reinstalling the release afterwards is a downgrade that needs
`Remove-AppxPackage` first. Genuine side-by-side would mean giving Debug its own
`ApplicationId`.

F5 in Visual Studio needs the `MsixPackage` profile in
`MDViewer/Properties/launchSettings.json`. The template ships `Project` instead,
which is the unpackaged launch path and fails the build outright:
*"does not contain a profile with commandName 'MsixPackage'"*. Because this app is
packaged, `MsixPackage` is the only profile that can work — see below.

Unpackaged builds crash on launch with
`Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`
because an unpackaged app has no package identity. Always run packaged.

To run a Debug build without any certificate (requires Developer Mode on):

```powershell
Add-AppxPackage -Register "MDViewer\bin\Debug\net10.0-windows10.0.19041.0\win-x64\AppxManifest.xml"
Start-Process explorer.exe "shell:AppsFolder\$((Get-AppxPackage nz.pontifex.mdviewer).PackageFamilyName)!App"
```

## Release — build a signed MSIX

The signing certificate is self-signed, subject `CN=Pontifex`, in
`Cert:\CurrentUser\My`, valid 5 years. It **must** match `Publisher` in
`Platforms/Windows/Package.appxmanifest` exactly or the package will not install.

Recreate it if needed:

```powershell
New-SelfSignedCertificate -Type Custom -Subject 'CN=Pontifex' `
  -KeyUsage DigitalSignature -FriendlyName 'MDViewer code signing' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}Subject Type:End Entity') `
  -NotAfter (Get-Date).AddYears(5)
```

Then publish:

```bash
dotnet publish MDViewer -c Release \
  -p:GenerateAppxPackageOnBuild=true \
  -p:AppxPackageSigningEnabled=true \
  -p:PackageCertificateThumbprint=<thumbprint>
```

Output: `MDViewer/bin/Release/net10.0-windows10.0.19041.0/win-x64/AppPackages/MDViewer_<ver>_Test/`

Bump the version for each release via `ApplicationDisplayVersion` in
`MDViewer/MDViewer.csproj` — the third digit (Windows refuses to install a package
over an equal or lower version). `1.0.4` + `ApplicationVersion` of `0` gives package
version `1.0.4.0`.

The counter is in the *display* version, not `ApplicationVersion`, because the
Microsoft Store reserves the fourth part of the version and requires it to be `0`
on submission. Keep `ApplicationVersion` at `0` for Release.

**Bumping it is not enough on its own.** The manifest generation step is incremental
on the timestamp of `Platforms/Windows/Package.appxmanifest`, not on the version
properties, so it silently reuses its cached output and you get a package at the
*old* version — with no warning, since the build succeeds. Delete the intermediate
first:

```bash
rm MDViewer/obj/Release/net10.0-windows10.0.19041.0/win-x64/resizetizer/m/Package.appxmanifest
```

Then publish, and confirm the version in the `.msix` filename before installing.

## Install

From an **elevated** PowerShell — trusting the certificate needs admin:

```powershell
cd "MDViewer\bin\Release\net10.0-windows10.0.19041.0\win-x64\AppPackages\MDViewer_1.0.4.0_Test"
Import-Certificate -FilePath .\MDViewer_1.0.4.0_x64.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\MDViewer_1.0.4.0_x64.msix
```

The generated `Install.ps1` in that folder does both and self-elevates.

Remove any dev registration first, or the install will conflict:

```powershell
Get-AppxPackage *mdviewer* | Remove-AppxPackage
```

### Make it the default for .md

Windows requires you to choose this yourself — it cannot be set by the installer.
Right-click any `.md` → **Open with** → **Markdown Viewer** → *Always use this app*.
The app is already registered as a handler for `.md`, `.markdown`, `.mdown`,
`.mkd` and `.mdtext`.

## Known issue — MAUI workload bug

The installed `maui-windows` workload (10.0.20) computes the Blazor host assets
correctly but **never copies them to the build output or the package**. Without a
workaround the app shows *"can't reach this page"* / `ERR_ADDRESS_UNREACHABLE`,
because BlazorWebView has no `index.html` to serve. Confirmed against a pristine
`dotnet new maui-blazor` app, so it is the workload, not this project.

`MDViewer/MDViewer.csproj` carries two targets that work around it —
`CopyMauiAssetsToOutput` (build) and `CopyMauiAssetsToPublish` (MSIX payload).
Delete both once the workload is fixed upstream.

## Tests

`MDViewer.Harness` compiles the real service files (not copies, via relative
`Compile Include` into `../MDViewer/Services`) and runs 43 checks over markdown
rendering, lossless save round-trips, ePub structure, the Word wrapper and export
path selection.

```bash
cd MDViewer.Harness && dotnet run -- sample.md
```

It writes its exports beside `sample.md`; those outputs are ignored by git.

## Licence

MIT — see [LICENSE](LICENSE).
