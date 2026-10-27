namespace MDViewer.Services;

/// <summary>
/// Hand-off point for the file the app was launched with. The host reads it at
/// startup (Windows activation in MDViewer/Platforms/Windows/App.xaml.cs, argv in
/// MDViewer.Linux/Program.cs), which runs before the DI container
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
