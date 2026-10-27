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

    public static string DocumentsFolder() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return cleaned.Length > 120 ? cleaned[..120].Trim() : cleaned;
    }

    /// <summary>Opens Explorer with the file selected. Windows-only, like the rest of this app.</summary>
    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer being unavailable shouldn't take down the export that just succeeded.
        }
    }
}
