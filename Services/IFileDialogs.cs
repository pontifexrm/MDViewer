namespace MDViewer.Services;

public interface IFileDialogs
{
    /// <summary>Open picker filtered to markdown files. Returns null if cancelled.</summary>
    Task<string?> PickMarkdownAsync();

    /// <summary>
    /// Save picker. <paramref name="extension"/> includes the dot, e.g. ".md".
    /// Returns the chosen path, or null if cancelled.
    /// </summary>
    Task<string?> SaveAsAsync(string suggestedName, string extension, string extensionLabel);
}
