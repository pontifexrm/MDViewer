using System.Diagnostics;

namespace MDViewer.Services;

/// <summary>
/// Where exports land: beside the source .md, same base name, new extension.
/// If the source file has never been saved, falls back to the user's Documents
/// folder. Never overwrites — an existing name gets " (2)", " (3)" and so on.
/// </summary>
public static class ExportPaths
{
    public static string For(string? sourcePath, string fallbackName, string extension)
    {
        string folder, baseName;

        if (!string.IsNullOrEmpty(sourcePath))
        {
            // GetFullPath first: a relative source would otherwise yield an empty
            // directory and drop the export into the working directory.
            var full = Path.GetFullPath(sourcePath);
            folder = Path.GetDirectoryName(full) ?? DocumentsFolder();
            baseName = Path.GetFileNameWithoutExtension(full);
        }
        else
        {
            folder = DocumentsFolder();
            baseName = Sanitize(fallbackName);
        }

        if (string.IsNullOrWhiteSpace(baseName)) baseName = "document";

        var candidate = Path.Combine(folder, baseName + extension);
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{baseName} ({n++}){extension}");

        return candidate;
    }

    /// <summary>
    /// Documents, or home if there isn't one: Windows always has it, but a Linux
    /// account need not (a bare WSL or server install), and .NET names the folder
    /// whether or not it exists.
    /// </summary>
    public static string DocumentsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return cleaned.Length > 120 ? cleaned[..120].Trim() : cleaned;
    }

    /// <summary>Opens the file manager with the file selected.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                _ = Task.Run(() => RevealOnLinux(path));
        }
        catch (Exception)
        {
            // The file manager being unavailable shouldn't take down the export that just succeeded.
        }
    }

    /// <summary>
    /// The freedesktop FileManager1 D-Bus call is the only portable way to get the
    /// file selected rather than just its folder open; Nemo, Nautilus, Dolphin and
    /// Thunar all answer it. Without one listening, fall back to opening the folder.
    /// </summary>
    private static void RevealOnLinux(string path)
    {
        try
        {
            var show = new ProcessStartInfo("dbus-send")
            {
                ArgumentList =
                {
                    "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                    $"array:string:{new Uri(path).AbsoluteUri}", "string:",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(show))
            {
                if (p is not null && p.WaitForExit(5000) && p.ExitCode == 0) return;
            }

            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { folder } })?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
