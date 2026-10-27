using System.Runtime.InteropServices;
using MDViewer.Services;
using Photino.NET;

namespace MDViewer.Linux;

/// <summary>
/// GTK file choosers, called directly rather than through Photino's: Photino's
/// save dialog can't suggest a file name, and Save As without one is a step back
/// from the Windows app.
/// </summary>
public sealed class LinuxFileDialogs(PhotinoWindow window) : IFileDialogs
{
    private static readonly string[] DocumentExtensions = [".md", ".markdown", ".mdown", ".mkd", ".mdtext", ".txt", ".epub"];

    // The Windows save picker reopens wherever it was last used; GTK's doesn't
    // remember, so this does it for the session.
    private string? _lastSaveFolder;

    public Task<string?> PickDocumentAsync() => window.RunAsync(() =>
        Run(Gtk.FileChooserActionOpen, "Open", "_Open", ExportPaths.DocumentsFolder(), null,
            "Markdown and ePub", DocumentExtensions));

    public async Task<string?> SaveAsAsync(string suggestedName, string extension, string extensionLabel)
    {
        var folder = _lastSaveFolder ?? ExportPaths.DocumentsFolder();
        var path = await window.RunAsync(() =>
            Run(Gtk.FileChooserActionSave, "Save As", "_Save", folder, suggestedName + extension,
                extensionLabel, [extension]));
        if (path is null) return null;

        // Unlike the Windows picker, GTK never adds the file type's extension, so
        // someone who clears the suggested name and types "notes" gets "notes.md".
        if (Path.GetExtension(path).Length == 0) path += extension;

        _lastSaveFolder = Path.GetDirectoryName(path);
        return path;
    }

    private static string? Run(int action, string title, string acceptLabel, string folder, string? name,
        string filterName, string[] extensions)
    {
        var dialog = Gtk.gtk_file_chooser_native_new(title, Gtk.FindWebView()?.Window ?? IntPtr.Zero, action, acceptLabel, "_Cancel");
        try
        {
            Gtk.gtk_file_chooser_set_current_folder(dialog, folder);
            if (action == Gtk.FileChooserActionSave)
            {
                Gtk.gtk_file_chooser_set_do_overwrite_confirmation(dialog, true);
                if (name is not null) Gtk.gtk_file_chooser_set_current_name(dialog, name);
            }

            // GTK 3 patterns are case-sensitive, and Windows file names often aren't
            // lower case — README.MD has to show up too.
            var filter = Gtk.gtk_file_filter_new();
            Gtk.gtk_file_filter_set_name(filter, filterName);
            foreach (var ext in extensions)
            {
                Gtk.gtk_file_filter_add_pattern(filter, "*" + ext);
                Gtk.gtk_file_filter_add_pattern(filter, "*" + ext.ToUpperInvariant());
            }
            Gtk.gtk_file_chooser_add_filter(dialog, filter);

            if (Gtk.gtk_native_dialog_run(dialog) != Gtk.ResponseAccept) return null;

            var raw = Gtk.gtk_file_chooser_get_filename(dialog);
            if (raw == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(raw); }
            finally { Gtk.g_free(raw); }
        }
        finally
        {
            Gtk.g_object_unref(dialog);
        }
    }
}
