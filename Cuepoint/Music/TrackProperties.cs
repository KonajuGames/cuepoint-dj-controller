namespace Cuepoint.Music;

/// <summary>
/// What's kept about a song, besides its file: where it's kept and how much there is of it. Gathered for its
/// properties.
/// </summary>
sealed record StoredData(
    string LibraryFile,
    int Hotcues,
    int MemoryCues,
    int MemoryLoops,
    bool GridFixed,
    string? StemsFile,
    long? StemsBytes,
    string? DjEditFile,
    long? DjEditBytes
);

/// <summary>
/// A song's properties, as rows of a name and a value: its details, from its file and tags, and the data kept for it,
/// from its analysis to its stems. The stems are what take room, and can go; the rest is small, or the song's own.
/// </summary>
sealed record TrackProperties(string Title, IReadOnlyList<(string Name, string Value)> Details, IReadOnlyList<(string Name, string Value)> Stored, long? StemsBytes)
{
    public static TrackProperties Of(Song song, StoredData stored)
    {
        var details = new List<(string, string)>
        {
            ("Title", song.Title),
            ("Artist", Or(song.Artist)),
            ("Album", Or(song.Album)),
            ("Genre", Or(song.Genre)),
            ("Length", song.Duration is { } length ? Length(length) : "Not known yet"),
            ("File", song.Path),
            ("Size", Size(song.Size)),
            ("Modified", song.Modified.ToLocalTime().ToString("d MMM yyyy, HH:mm")),
        };

        var data = new List<(string, string)>
        {
            (
                "Analysis",
                song.Analysis is { } analysis
                    ? $"{(analysis.Grid.IsVariable ? "~" : "")}{analysis.Bpm:F1} BPM, {analysis.Key.Camelot} ({analysis.Key.ShortName})"
                        + (analysis.Energy is { } energy ? $", energy {energy}" : "")
                        + $", {analysis.Loudness:F1} LUFS"
                        + (stored.GridFixed ? "; beat grid fixed by hand" : "")
                : song.AnalysisError is { } error ? $"Couldn't be analysed: {error}"
                : "Not analysed yet"
            ),
            ("Cues", Cues(stored)),
            ("Notes", Notes(song.Notes)),
            ("Kept in", stored.LibraryFile),
            ("Stems", stored.StemsBytes is { } bytes ? $"{Size(bytes)}, in {stored.StemsFile}" : "None: it's separated when it's played in STEMS mode or edited"),
        };
        if (stored.DjEditFile is { } edit)
            data.Add(("DJ edit", stored.DjEditBytes is { } editBytes ? $"{edit} ({Size(editBytes)}), a song of its own" : edit));
        return new TrackProperties(song.Title, details, data, stored.StemsBytes);
    }

    static string Or(string text) => text.Length > 0 ? text : "None";

    internal static string Length(TimeSpan length) =>
        length.TotalHours >= 1 ? $"{(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}" : $"{(int)length.TotalMinutes}:{length.Seconds:00}";

    /// <summary>
    /// A size on disk: bytes, KB, MB or GB, whichever reads best.
    /// </summary>
    internal static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";

    static string Cues(StoredData stored)
    {
        var parts = new List<string>();
        if (stored.Hotcues > 0)
            parts.Add(Count(stored.Hotcues, "hot cue"));
        if (stored.MemoryCues > 0)
            parts.Add(Count(stored.MemoryCues, "memory cue"));
        if (stored.MemoryLoops > 0)
            parts.Add(Count(stored.MemoryLoops, "saved loop"));
        return parts.Count > 0 ? string.Join(", ", parts) : "None";
    }

    static string Notes(SongNotes notes)
    {
        var parts = new List<string>();
        if (notes.Rating > 0)
            parts.Add($"rated {new string('*', notes.Rating)}");
        if (notes.Color is { } colour)
            parts.Add(colour);
        if (notes.Comment is { } comment)
            parts.Add($"\"{comment}\"");
        if (parts.Count == 0)
            return "None";
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";
}
