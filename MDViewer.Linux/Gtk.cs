using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Photino.NET;

namespace MDViewer.Linux;

/// <summary>
/// The few GTK, GObject and WebKitGTK calls Photino doesn't wrap. GTK is
/// single-threaded: every one of these must run on the GTK main thread, which in
/// practice means inside <see cref="UiThread.RunAsync{T}"/>.
/// </summary>
internal static partial class Gtk
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibWebKit = "libwebkit2gtk-4.1.so.0";

    public const int ResponseAccept = -3;
    public const int FileChooserActionOpen = 0;
    public const int FileChooserActionSave = 1;
    public const int UnitInch = 2;
    public const int PrintResponseCancel = 1;

    /// <summary>
    /// Photino's window and the WebKitWebView it adds as the window's only child.
    /// Photino keeps both private, so they are found by walking GTK's toplevels
    /// for the one window whose child is a WebKitWebView — this app only ever has
    /// the one.
    /// </summary>
    public static (IntPtr Window, IntPtr WebView)? FindWebView()
    {
        var webViewType = webkit_web_view_get_type();
        var list = gtk_window_list_toplevels();
        try
        {
            // GList is { data, next, prev }; the list owns nothing, so only it is freed.
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var window = Marshal.ReadIntPtr(node);
                var child = gtk_bin_get_child(window);
                if (child != IntPtr.Zero && g_type_check_instance_is_a(child, webViewType))
                    return (window, child);
            }
            return null;
        }
        finally
        {
            g_list_free(list);
        }
    }

    // ── GLib / GObject ───────────────────────────────────────────────────────

    [LibraryImport(LibGLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void g_set_prgname(string name);

    [LibraryImport(LibGLib)] public static partial void g_list_free(IntPtr list);
    [LibraryImport(LibGLib)] public static partial void g_free(IntPtr mem);
    [LibraryImport(LibGObject)] public static partial void g_object_unref(IntPtr obj);

    [LibraryImport(LibGObject)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool g_type_check_instance_is_a(IntPtr instance, nuint type);

    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint g_signal_connect_data(IntPtr instance, string signal, IntPtr handler, IntPtr data, IntPtr destroyData, int flags);

    // ── Windows and file choosers ────────────────────────────────────────────

    [LibraryImport(LibGtk)] public static partial IntPtr gtk_window_list_toplevels();
    [LibraryImport(LibGtk)] public static partial IntPtr gtk_bin_get_child(IntPtr bin);

    // The native variant, not GtkFileChooserDialog: its constructor isn't variadic,
    // and it goes through the desktop portal when the app is sandboxed (Flatpak).
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr gtk_file_chooser_native_new(string title, IntPtr parent, int action, string? acceptLabel, string? cancelLabel);

    [LibraryImport(LibGtk)] public static partial int gtk_native_dialog_run(IntPtr dialog);

    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool gtk_file_chooser_set_current_folder(IntPtr chooser, string folder);

    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void gtk_file_chooser_set_current_name(IntPtr chooser, string name);

    [LibraryImport(LibGtk)]
    public static partial void gtk_file_chooser_set_do_overwrite_confirmation(IntPtr chooser, [MarshalAs(UnmanagedType.Bool)] bool confirm);

    /// <summary>Caller frees with <see cref="g_free"/>.</summary>
    [LibraryImport(LibGtk)] public static partial IntPtr gtk_file_chooser_get_filename(IntPtr chooser);

    [LibraryImport(LibGtk)] public static partial void gtk_file_chooser_add_filter(IntPtr chooser, IntPtr filter);
    [LibraryImport(LibGtk)] public static partial IntPtr gtk_file_filter_new();

    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void gtk_file_filter_set_name(IntPtr filter, string name);

    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void gtk_file_filter_add_pattern(IntPtr filter, string pattern);

    // ── Printing ─────────────────────────────────────────────────────────────

    [LibraryImport(LibGtk)] public static partial IntPtr gtk_print_settings_new();

    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void gtk_print_settings_set(IntPtr settings, string key, string value);

    [LibraryImport(LibGtk)] public static partial IntPtr gtk_page_setup_new();
    [LibraryImport(LibGtk)] public static partial void gtk_page_setup_set_top_margin(IntPtr setup, double margin, int unit);
    [LibraryImport(LibGtk)] public static partial void gtk_page_setup_set_bottom_margin(IntPtr setup, double margin, int unit);
    [LibraryImport(LibGtk)] public static partial void gtk_page_setup_set_left_margin(IntPtr setup, double margin, int unit);
    [LibraryImport(LibGtk)] public static partial void gtk_page_setup_set_right_margin(IntPtr setup, double margin, int unit);

    [LibraryImport(LibGtk)]
    public static partial void gtk_enumerate_printers(IntPtr func, IntPtr data, IntPtr destroy, [MarshalAs(UnmanagedType.Bool)] bool wait);

    /// <summary>Owned by the printer; do not free.</summary>
    [LibraryImport(LibGtk)] public static partial IntPtr gtk_printer_get_name(IntPtr printer);

    [LibraryImport(LibGtk)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool gtk_printer_is_virtual(IntPtr printer);

    [LibraryImport(LibGtk)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool gtk_printer_accepts_pdf(IntPtr printer);

    [LibraryImport(LibWebKit)] public static partial nuint webkit_web_view_get_type();
    [LibraryImport(LibWebKit)] public static partial IntPtr webkit_print_operation_new(IntPtr webView);
    [LibraryImport(LibWebKit)] public static partial void webkit_print_operation_set_print_settings(IntPtr op, IntPtr settings);
    [LibraryImport(LibWebKit)] public static partial void webkit_print_operation_set_page_setup(IntPtr op, IntPtr setup);
    [LibraryImport(LibWebKit)] public static partial void webkit_print_operation_print(IntPtr op);
    [LibraryImport(LibWebKit)] public static partial int webkit_print_operation_run_dialog(IntPtr op, IntPtr parent);
}

internal static class UiThread
{
    /// <summary>
    /// Runs <paramref name="work"/> on the GTK main thread and waits for it.
    /// Photino's Invoke blocks the caller, and blocking Blazor's dispatcher on it
    /// deadlocks, hence the hop to the thread pool first. An exception thrown on
    /// the GTK side is caught there — letting it unwind through native frames
    /// would take the process down — and rethrown to the awaiting caller.
    /// </summary>
    public static Task<T> RunAsync<T>(this PhotinoWindow window, Func<T> work) => Task.Run(() =>
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        window.Invoke(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        failure?.Throw();
        return result;
    });
}
