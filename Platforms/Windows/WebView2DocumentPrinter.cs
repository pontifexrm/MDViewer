using MDViewer.Services;
using Microsoft.Web.WebView2.Core;

namespace MDViewer.WinUI;

/// <summary>
/// Printing and PDF via WebView2's own print engine — the same one Edge uses.
/// This is why the app needs no PDF library: PrintToPdfAsync renders the page
/// through the @media print rules in wwwroot/app.css, which hide the toolbar and
/// the editor pane, so the output is just the document.
/// </summary>
public sealed class WebView2DocumentPrinter : IDocumentPrinter
{
	private CoreWebView2? _core;

	public bool IsReady => _core != null;

	/// <summary>Called from MainPage once the BlazorWebView has a live CoreWebView2.</summary>
	public void Attach(CoreWebView2 core) => _core = core;

	public async Task ShowPrintDialogAsync()
	{
		if (_core == null) return;

		// The browser dialog gives print preview *and* a "Save as PDF" destination,
		// so it covers the "I want to choose where the PDF goes" case too.
		_core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
		await Task.CompletedTask;
	}

	public async Task<bool> PrintToPdfAsync(string filePath)
	{
		if (_core == null) return false;

		var settings = _core.Environment.CreatePrintSettings();
		settings.ShouldPrintBackgrounds = true;
		settings.ShouldPrintHeaderAndFooter = false;
		settings.MarginTop = 0.5;
		settings.MarginBottom = 0.5;
		settings.MarginLeft = 0.4;
		settings.MarginRight = 0.4;

		return await _core.PrintToPdfAsync(filePath, settings);
	}
}
