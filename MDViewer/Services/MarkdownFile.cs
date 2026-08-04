using System.Text;

namespace MDViewer.Services;

/// <summary>
/// Reads and writes the .md file while preserving the two things a browser
/// textarea would otherwise quietly destroy: the byte order mark, and CRLF line
/// endings. Editing is meant to be lossless, so a save must not rewrite every
/// line of a file just because it round-tripped through the DOM.
/// </summary>
public sealed class MarkdownFile
{
    public string Path { get; }
    public string Text { get; private set; }

    private readonly bool _hadBom;
    private readonly bool _crlf;

    private MarkdownFile(string path, string text, bool hadBom, bool crlf)
    {
        Path = path;
        Text = text;
        _hadBom = hadBom;
        _crlf = crlf;
    }

    public static async Task<MarkdownFile> LoadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);

        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));

        var crlf = text.Contains("\r\n");
        // Normalise for editing; the original convention is restored on save.
        text = text.Replace("\r\n", "\n");

        return new MarkdownFile(path, text, hadBom, crlf);
    }

    public async Task SaveAsync(string text, string? toPath = null)
    {
        var target = toPath ?? Path;
        var outText = _crlf ? text.Replace("\n", "\r\n") : text;

        // WriteAllText (not GetBytes) — UTF8Encoding.GetBytes never emits the
        // preamble, so writing bytes directly would silently strip the BOM.
        await File.WriteAllTextAsync(target, outText, new UTF8Encoding(_hadBom));
        Text = text;
    }

    /// <summary>A copy of this file's conventions, pointed at a new path (Save As).</summary>
    public MarkdownFile At(string newPath) => new(newPath, Text, _hadBom, _crlf);

    /// <summary>A brand-new unsaved document, using Windows-native conventions.</summary>
    public static MarkdownFile Untitled() => new(string.Empty, string.Empty, hadBom: false, crlf: true);
}
