using System.Runtime.InteropServices;
using MDViewer.Services;
using Photino.NET;

namespace MDViewer.Linux;

/// <summary>
/// Printing and PDF via WebKitGTK's own print operation — the Linux counterpart
/// of the Windows host's WebView2DocumentPrinter, and like it, rendering through
/// the @media print rules in app.css with no PDF library involved. The PDF is
/// "printed" to GTK's built-in Print to File printer, which is what the print
/// dialog's own PDF option does too.
/// </summary>
public sealed class WebKitDocumentPrinter(PhotinoWindow window) : IDocumentPrinter
{
    /// <summary>
    /// A print runs on after the call that started it returns, and ends with
    /// WebKit's "finished" signal. Each operation stays referenced here until then.
    /// Only touched on the GTK main thread, so no locking.
    /// </summary>
    private static readonly Dictionary<IntPtr, Job> Running = [];

    private sealed class Job(TaskCompletionSource<bool>? done)
    {
        public TaskCompletionSource<bool>? Done { get; } = done;
        public bool Failed { get; set; }
    }

    // Blazor only renders once the WebView has loaded, so by the time there is a
    // toolbar to ask, there is a WebView to print from.
    public bool IsReady => true;

    public Task ShowPrintDialogAsync() => window.RunAsync(() =>
    {
        if (Gtk.FindWebView() is not (var parent, var webView)) return false;

        var op = Start(webView, settings: IntPtr.Zero, done: null);
        if (Gtk.webkit_print_operation_run_dialog(op, parent) == Gtk.PrintResponseCancel)
            Release(op);
        return true;
    });

    public async Task<bool> PrintToPdfAsync(string filePath)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = await window.RunAsync(() =>
        {
            if (Gtk.FindWebView() is not (_, var webView)) return false;
            if (FindPdfPrinter() is not { } printer) return false;

            var settings = Gtk.gtk_print_settings_new();
            Gtk.gtk_print_settings_set(settings, "printer", printer);
            Gtk.gtk_print_settings_set(settings, "output-file-format", "pdf");
            Gtk.gtk_print_settings_set(settings, "output-uri", new Uri(filePath).AbsoluteUri);

            var op = Start(webView, settings, done);
            Gtk.g_object_unref(settings);
            Gtk.webkit_print_operation_print(op);
            return true;
        });
        if (!started) return false;

        // The signal should always come, but a print backend that wedges would
        // otherwise leave the export spinning forever.
        return await done.Task.WaitAsync(TimeSpan.FromMinutes(2)) && File.Exists(filePath);
    }

    private static unsafe IntPtr Start(IntPtr webView, IntPtr settings, TaskCompletionSource<bool>? done)
    {
        var op = Gtk.webkit_print_operation_new(webView);
        if (settings != IntPtr.Zero) Gtk.webkit_print_operation_set_print_settings(op, settings);

        // The same margins the Windows host gives WebView2.
        var setup = Gtk.gtk_page_setup_new();
        Gtk.gtk_page_setup_set_top_margin(setup, 0.5, Gtk.UnitInch);
        Gtk.gtk_page_setup_set_bottom_margin(setup, 0.5, Gtk.UnitInch);
        Gtk.gtk_page_setup_set_left_margin(setup, 0.4, Gtk.UnitInch);
        Gtk.gtk_page_setup_set_right_margin(setup, 0.4, Gtk.UnitInch);
        Gtk.webkit_print_operation_set_page_setup(op, setup);
        Gtk.g_object_unref(setup);

        Gtk.g_signal_connect_data(op, "failed", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnFailed, IntPtr.Zero, IntPtr.Zero, 0);
        Gtk.g_signal_connect_data(op, "finished", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnFinished, IntPtr.Zero, IntPtr.Zero, 0);
        Running[op] = new Job(done);
        return op;
    }

    /// <summary>
    /// Drops an operation that will never finish (a cancelled dialog). Safe if
    /// "finished" got there first: then it is no longer in the table.
    /// </summary>
    private static void Release(IntPtr op)
    {
        if (Running.Remove(op)) Gtk.g_object_unref(op);
    }

    [UnmanagedCallersOnly]
    private static void OnFailed(IntPtr op, IntPtr error, IntPtr data)
    {
        if (Running.TryGetValue(op, out var job)) job.Failed = true;
    }

    // "finished" follows "failed" too, so this is the one place a job ends.
    [UnmanagedCallersOnly]
    private static void OnFinished(IntPtr op, IntPtr data)
    {
        if (!Running.Remove(op, out var job)) return;
        job.Done?.TrySetResult(!job.Failed);
        Gtk.g_object_unref(op);
    }

    // ── Finding the Print to File printer ────────────────────────────────────

    private static string? _pdfPrinter;

    /// <summary>
    /// GTK's file printer, by whatever name it has here. The name is translated
    /// ("Print to File", "In Datei drucken", …), so it is recognised by what it
    /// is — a virtual printer that takes PDF — rather than by what it is called.
    /// </summary>
    private static unsafe string? FindPdfPrinter()
    {
        _pdfPrinter = null;
        Gtk.gtk_enumerate_printers((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&OnPrinter, IntPtr.Zero, IntPtr.Zero, wait: true);
        return _pdfPrinter;
    }

    [UnmanagedCallersOnly]
    private static int OnPrinter(IntPtr printer, IntPtr data)
    {
        if (!Gtk.gtk_printer_is_virtual(printer) || !Gtk.gtk_printer_accepts_pdf(printer)) return 0;
        _pdfPrinter = Marshal.PtrToStringUTF8(Gtk.gtk_printer_get_name(printer));
        return 1; // stop enumerating
    }
}
