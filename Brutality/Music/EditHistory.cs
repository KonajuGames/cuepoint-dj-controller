using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// The hot cue and beat grid changes made since the app started, newest last, to take back one at a time. Each is
/// kept with its song, so it can be undone after the song's left the deck. UI thread only.
/// </summary>
sealed class EditHistory(LibraryDatabase database, MusicLibrary library, IReadOnlyList<Deck> decks)
{
    /// <summary>
    /// How many changes it remembers; the oldest go first.
    /// </summary>
    public const int MAX_EDITS = 100;

    readonly record struct Edit(string Path, string What, Action Undo);

    readonly List<Edit> _edits = [];

    public int Count => _edits.Count;

    void Add(Edit edit)
    {
        if (_edits.Count == MAX_EDITS)
            _edits.RemoveAt(0);
        _edits.Add(edit);
    }

    /// <summary>
    /// The song at <paramref name="path"/>'s hot cues went from <paramref name="before"/> to <paramref name="after"/>.
    /// </summary>
    public void RecordHotcues(string path, IReadOnlyList<TimeSpan?> before, IReadOnlyList<TimeSpan?> after)
    {
        var kept = before.ToArray();
        Add(
            new Edit(
                path,
                Describe(before, after),
                () =>
                {
                    database.SetHotcues(path, kept);
                    foreach (var deck in DecksWith(path))
                        deck.SetHotcues(kept);
                }
            )
        );
    }

    static string Describe(IReadOnlyList<TimeSpan?> before, IReadOnlyList<TimeSpan?> after)
    {
        for (var i = 0; i < Math.Max(before.Count, after.Count); i++)
        {
            var (was, now) = (before.ElementAtOrDefault(i), after.ElementAtOrDefault(i));
            if (was != now)
                return now is null ? $"clearing hot cue {i + 1}" : $"setting hot cue {i + 1}";
        }
        return "a hot cue change";
    }

    /// <summary>
    /// The song at <paramref name="path"/>'s memory cues went from <paramref name="before"/> to <paramref name="after"/>.
    /// </summary>
    public void RecordMemoryCues(string path, IReadOnlyList<TimeSpan> before, IReadOnlyList<TimeSpan> after)
    {
        var kept = before.ToArray();
        Add(
            new Edit(
                path,
                after.Count > before.Count ? "adding a memory cue" : "removing a memory cue",
                () =>
                {
                    database.SetMemoryCues(path, kept);
                    foreach (var deck in DecksWith(path))
                        deck.ReplaceMemoryCues(kept);
                }
            )
        );
    }

    /// <summary>
    /// The song at <paramref name="path"/>'s beat grid was changed from <paramref name="before"/>; null if it hadn't
    /// been fixed by hand.
    /// </summary>
    public void RecordGrid(string path, GridEdit? before) =>
        Add(
            new Edit(
                path,
                "a beat grid change",
                () =>
                {
                    database.SetGridEdit(path, before);
                    if (library.Find(path) is { } song && database.TryGetAnalysed(song, out var analysed, out _) && analysed is not null)
                    {
                        var analysis = database.WithEdits(path, analysed);
                        song.Analysis = analysis;
                        foreach (var deck in DecksWith(path))
                            deck.Analysis = analysis;
                    }
                    else if (before is not null)
                    {
                        foreach (var deck in DecksWith(path))
                        {
                            if (deck.Analysis is { } shown)
                                deck.Analysis = shown with { Grid = before.Grid };
                        }
                    }
                }
            )
        );

    IEnumerable<Deck> DecksWith(string path) => decks.Where(deck => string.Equals(deck.FilePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Takes back the latest change and saves. Says what happened, for the status line.
    /// </summary>
    public string Undo()
    {
        if (_edits.Count == 0)
            return "Nothing to undo";
        var edit = _edits[^1];
        _edits.RemoveAt(_edits.Count - 1);
        edit.Undo();
        var song = library.Find(edit.Path)?.Title ?? Path.GetFileNameWithoutExtension(edit.Path);
        try
        {
            database.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Undid {edit.What} on {song}, but couldn't save it: {e.Message}";
        }
        return $"Undid {edit.What} on {song}";
    }
}
