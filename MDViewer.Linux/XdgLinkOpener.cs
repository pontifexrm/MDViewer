using System.Diagnostics;
using MDViewer.Services;

namespace MDViewer.Linux;

/// <summary>The desktop's default browser, via xdg-open.</summary>
public sealed class XdgLinkOpener : ILinkOpener
{
    public Task OpenAsync(Uri uri)
    {
        // ArgumentList, not a command string: the URL came out of a book, and must
        // reach xdg-open as one argument however it is spelled.
        Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { uri.AbsoluteUri } })?.Dispose();
        return Task.CompletedTask;
    }
}
