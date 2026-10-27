using MDViewer.Services;

namespace MDViewer;

/// <summary>MAUI's own launcher — works on every platform MAUI targets.</summary>
public sealed class MauiLinkOpener : ILinkOpener
{
	public Task OpenAsync(Uri uri) => Launcher.Default.OpenAsync(uri);
}
