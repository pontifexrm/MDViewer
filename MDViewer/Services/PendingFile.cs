namespace MDViewer.Services;

/// <summary>
/// Hand-off point for the file the app was launched with. Windows activation is
/// read in Platforms/Windows/App.xaml.cs, which runs before the MAUI DI container
/// exists — so the path parks here as a static and the Viewer component collects
/// it on first render.
/// </summary>
public static class PendingFile
{
    public static string? Path { get; set; }

    /// <summary>Reads and clears the pending path, so a reload doesn't re-open it.</summary>
    public static string? Take()
    {
        var p = Path;
        Path = null;
        return p;
    }
}
