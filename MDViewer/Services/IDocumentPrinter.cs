namespace MDViewer.Services;

/// <summary>
/// Printing and PDF, backed by the WebView the app already renders into.
/// On Windows this is WebView2's own print engine (see
/// Platforms/Windows/WebView2DocumentPrinter.cs) — no PDF library involved.
/// </summary>
public interface IDocumentPrinter
{
    /// <summary>True once the host WebView is initialised and printing is possible.</summary>
    bool IsReady { get; }

    /// <summary>Opens the system print preview dialog (which can also save a PDF).</summary>
    Task ShowPrintDialogAsync();

    /// <summary>Renders the current page straight to a PDF file using print styles.</summary>
    Task<bool> PrintToPdfAsync(string filePath);
}
