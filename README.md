# Markdown Viewer

A Windows desktop viewer for `.md` files — double-click a markdown file and read it
rendered, with print, PDF, Word and ePub output. .NET MAUI Blazor Hybrid (WebView2).

The rendering pipeline is the same one the our internal knowledge base knowledge base uses
(Markdig, `UseAdvancedExtensions().DisableHtml()`), so a file looks here exactly as
it would after being pasted into a KB article. The ePub generator is ported from
`our knowledge base's ePub generator`.

## What it does

| Action | Notes |
|---|---|
| Open | File picker, or launch with a `.md` (file association / command line) |
| Edit | Split pane: markdown source left, live preview right |
| Save / Save As | Writes the markdown source back **losslessly** — BOM and CRLF preserved |
| Print | WebView2 print preview (its "Save as PDF" destination works too) |
| PDF | `CoreWebView2.PrintToPdfAsync` — no PDF library involved |
| Word | HTML wrapped in the Office document header; opens straight into Word |
| ePub | Valid ePub 3; Kindle (2022+), Kobo, Apple Books |

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

## Layout

```
MDViewer.sln        both projects
MDViewer/           the MAUI Blazor Hybrid app
MDViewer.Harness/   test harness — compiles the app's service files directly
```

Paths in the rest of this README are relative to the repo root unless a `cd` says
otherwise. Build commands name the project folder rather than the solution,
because publishing the MSIX has to target the app project on its own.

## Build

```bash
dotnet build MDViewer -c Debug            # packaged (MSIX) — needs Developer Mode to run
dotnet build MDViewer -c Debug -p:WindowsPackageType=None   # unpackaged; will NOT run, see below
```

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

Bump the version for each release via `ApplicationVersion` in `MDViewer/MDViewer.csproj`
(Windows refuses to install a package over an equal or lower version). `1.0` +
`ApplicationVersion` of `2` gives package version `1.0.0.2`.

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
cd "MDViewer\bin\Release\net10.0-windows10.0.19041.0\win-x64\AppPackages\MDViewer_1.0.0.1_Test"
Import-Certificate -FilePath .\MDViewer_1.0.0.1_x64.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\MDViewer_1.0.0.1_x64.msix
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
