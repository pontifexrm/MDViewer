using System.Text.Json;

namespace MDViewer.Services;

/// <summary>
/// Where the reader had got to in a book: which spine document, and how far down
/// it. <paramref name="Block"/> is the index of a direct child of .doc-render
/// rather than a pixel offset, so the position survives the reflow that zooming
/// or resizing the window causes — a scroll offset in pixels would not.
/// </summary>
public sealed record ReadingPosition(int Chapter, int Block, double Fraction, DateTimeOffset Opened);

/// <summary>
/// Remembers where each book was left off, in one small JSON file under the
/// user's local app data.
///
/// Books are keyed by full path rather than by the OPF's dc:identifier, which is
/// the field nominally meant for this. An identifier would survive the file being
/// moved, but plenty of tools emit a placeholder or reuse one across a series, and
/// two unrelated books sharing a key means opening one and landing in the middle
/// of the other — a confusing failure with no obvious cause. Losing the position
/// when a file moves is the milder mistake, so that is the one made here.
/// </summary>
public static class ReadingPositions
{
    /// <summary>Oldest entries beyond this are dropped, so the file cannot grow without bound.</summary>
    private const int MaxBooks = 200;

    /// <summary>Overridable so the test harness can write somewhere disposable.</summary>
    public static string StorageFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDViewer");

    private static string FilePath => Path.Combine(StorageFolder, "reading-positions.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ReadingPosition? For(string bookPath) =>
        Load().TryGetValue(Key(bookPath), out var position) ? position : null;

    public static async Task SaveAsync(string bookPath, int chapter, int block, double fraction)
    {
        try
        {
            var all = Load();
            all[Key(bookPath)] = new ReadingPosition(chapter, block, fraction, DateTimeOffset.UtcNow);

            if (all.Count > MaxBooks)
                foreach (var stale in all.OrderByDescending(e => e.Value.Opened).Skip(MaxBooks).ToList())
                    all.Remove(stale.Key);

            Directory.CreateDirectory(StorageFolder);

            // Written to a uniquely-named temp file and moved into place. Each .md or
            // .epub opens its own window, so two of them can save at the same moment;
            // this way the loser of that race overwrites the winner rather than
            // interleaving with it and leaving a half-written file behind.
            var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(all, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception)
            {
                if (File.Exists(temp)) File.Delete(temp);
                throw;
            }
        }
        catch (Exception)
        {
            // A lost reading position is not worth interrupting the reader for.
        }
    }

    /// <summary>Forgets one book — used when its stored position no longer fits it.</summary>
    public static async Task ForgetAsync(string bookPath)
    {
        try
        {
            var all = Load();
            if (!all.Remove(Key(bookPath))) return;

            Directory.CreateDirectory(StorageFolder);
            var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(all, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // As above.
        }
    }

    private static Dictionary<string, ReadingPosition> Load()
    {
        var empty = new Dictionary<string, ReadingPosition>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return empty;

            var parsed = JsonSerializer.Deserialize<Dictionary<string, ReadingPosition>>(
                File.ReadAllText(FilePath), Options);

            return parsed is null ? empty : new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Corrupt or half-written file: start over rather than refusing to open
            // the book. The next save replaces it.
            return empty;
        }
    }

    private static string Key(string bookPath)
    {
        try { return Path.GetFullPath(bookPath); }
        catch (Exception) { return bookPath; }
    }
}
