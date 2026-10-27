using MDViewer.Components;
using MDViewer.Services;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.NET;

namespace MDViewer.Linux;

internal static class Program
{
    public const string AppId = "nz.pontifex.mdviewer";

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);

        // Photino starts GTK with no argv, so GTK never learns a program name. Set
        // one before it starts: it becomes the X11 WM_CLASS and the Wayland app id,
        // which is how the desktop matches the window to nz.pontifex.mdviewer.desktop
        // (its icon, its taskbar grouping). Same id as the Windows package.
        Gtk.g_set_prgname(AppId);

        // A .desktop launcher or the command line passes the file as the first
        // argument (unlike Windows, argv here does not include the program).
        if (args.Length > 0 && File.Exists(args[0]))
            PendingFile.Path = Path.GetFullPath(args[0]);

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
#if !DEBUG
        // Photino logs every window setting to stdout, including the ones
        // Photino.Blazor makes while building the app — before anything below
        // could turn it off. A window registered quiet replaces its default one.
        builder.Services.AddSingleton(new PhotinoWindow { LogVerbosity = 0 });
#endif
        builder.Services.AddSingleton<IFileDialogs, LinuxFileDialogs>();
        builder.Services.AddSingleton<IDocumentPrinter, WebKitDocumentPrinter>();
        builder.Services.AddSingleton<ILinkOpener, XdgLinkOpener>();
        builder.RootComponents.Add<Routes>("#app");

        var app = builder.Build();
        app.MainWindow
            .SetTitle("Markdown Viewer")
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "mdviewer.png"))
            .SetSize(1100, 850)
            .Center()
#if DEBUG
            .SetDevToolsEnabled(true);
#else
            .SetDevToolsEnabled(false);
#endif

        app.Run();
    }

    /// <summary>Same crash log as the Windows host, in the same temp folder convention.</summary>
    private static void Log(Exception? ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "mdviewer-crash.log");
            File.AppendAllText(path, $"=== {DateTime.Now:s} ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { /* nothing useful to do if even logging fails */ }
    }
}
