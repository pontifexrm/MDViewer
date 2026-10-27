namespace MDViewer.Services;

/// <summary>
/// Hands a web link to the system's default browser. The caller has already
/// checked the scheme; implementations just launch it.
/// </summary>
public interface ILinkOpener
{
    Task OpenAsync(Uri uri);
}
