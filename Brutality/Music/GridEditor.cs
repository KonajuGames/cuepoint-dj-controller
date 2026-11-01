using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// A tool for fixing a deck's beat grid by hand, on one of its pads in GRID mode.
/// </summary>
enum GridTool
{
    /// <summary>
    /// Pins the beat nearest the playhead to it: one pin shifts the grid, two set its tempo, more set where it
    /// changes. With Shift, takes out the pin nearest the playhead instead.
    /// </summary>
    SetBeat,

    /// <summary>Makes the beat at the playhead the "one" of its bar.</summary>
    Downbeat,

    NudgeEarlier,
    NudgeLater,
    Halve,
    Double,

    /// <summary>One steady tempo in place of a varying one.</summary>
    Steady,

    /// <summary>Back to the grid as analysed.</summary>
    Reset,
}

/// <summary>
/// Fixes the beat grid of the song on a deck by hand: while <see cref="Active"/> (the deck's pads in GRID mode),
/// each pad is one of the <see cref="GridTool"/>s, acting at the playhead, and a beat on the scrolling waveform can be
/// dragged onto the music (<see cref="BeginDrag"/>). Each change is saved with the song straight away, apart from its
/// analysis, and given to the deck and the song, so everything that follows the grid (sync, loops, the waveforms, the
/// beat trackers, lighting) follows it at once. While active, the deck clicks on the grid's beats, to hear it against
/// the music.
/// </summary>
sealed class GridEditor
{
    public static readonly IReadOnlyList<GridTool> Pads =
        [GridTool.SetBeat, GridTool.Downbeat, GridTool.NudgeEarlier, GridTool.NudgeLater, GridTool.Halve, GridTool.Double, GridTool.Steady, GridTool.Reset];

    readonly MusicLibrary _library;
    readonly LibraryDatabase _database;
    bool _active;

    // A beat being dragged: the song, the edit as the drag began, which beat, and the edit it would make now.
    (string Path, TrackAnalysis Analysis, GridEdit Start, long Beat, GridEdit? Now)? _drag;

    public GridEditor(Deck deck, MusicLibrary library, LibraryDatabase database)
    {
        Deck = deck;
        _library = library;
        _database = database;
    }

    public Deck Deck { get; }

    /// <summary>
    /// What's happened, worth a line on the status bar.
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// Whether the deck's pads are grid tools rather than hotcues. The deck clicks on its beats meanwhile.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            Deck.Metronome = value;
        }
    }

    /// <summary>
    /// What a tool's pad says.
    /// </summary>
    public static string Label(GridTool tool) =>
        tool switch
        {
            GridTool.SetBeat => "SET BEAT",
            GridTool.Downbeat => "DOWNBEAT",
            GridTool.NudgeEarlier => "◄ NUDGE",
            GridTool.NudgeLater => "NUDGE ►",
            GridTool.Halve => "×½",
            GridTool.Double => "×2",
            GridTool.Steady => "STEADY",
            _ => "RESET",
        };

    /// <summary>
    /// Where beats have been pinned by hand on the deck's song, in seconds; mid-drag, where they'd be.
    /// </summary>
    public IReadOnlyList<double> Pins => _drag is { Now: { } now } && _drag.Value.Path == Deck.FilePath ? now.Pins : Current()?.Pins ?? [];

    /// <summary>
    /// Whether the deck's song has a grid fixed by hand.
    /// </summary>
    public bool IsEdited => Deck.FilePath is { } path && _database.HasGridEdit(path);

    GridEdit? Current() =>
        Deck.FilePath is { } path && Deck.Analysis is { } analysis ? _database.GetGridEdit(path, analysis.Grid.Duration) : null;

    /// <summary>
    /// Pad <paramref name="index"/> was pressed; <paramref name="fine"/> (Shift held) makes a nudge a fine one.
    /// </summary>
    public void Pad(int index, bool fine = false)
    {
        if (index >= 0 && index < Pads.Count)
            Apply(Pads[index], fine);
    }

    public void Apply(GridTool tool, bool fine = false)
    {
        if (Deck.FilePath is not { } path || Deck.Analysis is not { } analysis)
        {
            Log?.Invoke($"Deck {Deck.Name}: load a song, and let it be analysed, to fix its grid");
            return;
        }

        var song = _library.Find(path);
        if (tool == GridTool.Reset)
        {
            if (!_database.HasGridEdit(path))
                return;
            _database.SetGridEdit(path, null);
            var analysed = song is not null && _database.TryGetAnalysed(song, out var stored, out _) && stored is not null ? stored : analysis;
            Use(path, song, analysed, null);
            Log?.Invoke($"Deck {Deck.Name}: grid back as analysed, {analysed.Grid}");
            return;
        }

        var edit = _database.GetGridEdit(path, analysis.Grid.Duration) ?? new GridEdit(analysis.Grid, []);
        var playhead = Deck.Position.TotalSeconds;
        if (tool == GridTool.SetBeat && fine)
        {
            RemovePin(playhead);
            return;
        }

        var step = fine ? GridEditing.FINE_NUDGE : GridEditing.NUDGE;
        var next = tool switch
        {
            GridTool.SetBeat => GridEditing.SetBeat(edit, playhead),
            GridTool.Downbeat => GridEditing.Downbeat(edit, playhead),
            GridTool.NudgeEarlier => GridEditing.Nudge(edit, -step, playhead),
            GridTool.NudgeLater => GridEditing.Nudge(edit, step, playhead),
            GridTool.Halve => GridEditing.Scale(edit, 0.5),
            GridTool.Double => GridEditing.Scale(edit, 2),
            _ => GridEditing.MakeSteady(edit, playhead),
        };
        Use(path, song, analysis with { Grid = next.Grid }, next);

        Log?.Invoke(
            tool switch
            {
                GridTool.SetBeat when next.Pins.Count == 2 => $"Deck {Deck.Name}: two beats pinned; {next.Grid.MainBpm:F2} BPM between them",
                GridTool.SetBeat when next.Pins.Count > 2 => $"Deck {Deck.Name}: {next.Pins.Count} beats pinned; {Tempos(next.Grid)}",
                GridTool.SetBeat => $"Deck {Deck.Name}: beat pinned at {Format(playhead)}; the grid moves to it",
                GridTool.Downbeat => $"Deck {Deck.Name}: the beat at {Format(playhead)} starts a bar",
                GridTool.NudgeEarlier or GridTool.NudgeLater => $"Deck {Deck.Name}: grid {(tool == GridTool.NudgeLater ? "later" : "earlier")} by {step * 1000:F0} ms",
                GridTool.Halve or GridTool.Double => $"Deck {Deck.Name}: {next.Grid.MainBpm:F2} BPM",
                _ => $"Deck {Deck.Name}: steady at {next.Grid.MainBpm:F2} BPM",
            }
        );
    }

    /// <summary>
    /// Starts dragging beat <paramref name="beat"/> of the deck's song's grid, to put it where the music's is. Returns
    /// whether it can be: not without an analysed song.
    /// </summary>
    public bool BeginDrag(long beat)
    {
        if (Deck.FilePath is not { } path || Deck.Analysis is not { } analysis)
            return false;
        var edit = _database.GetGridEdit(path, analysis.Grid.Duration) ?? new GridEdit(analysis.Grid, []);
        _drag = (path, analysis, edit, beat, null);
        return true;
    }

    /// <summary>
    /// The beat being dragged is now at <paramref name="seconds"/>: the grid follows, on the deck and the waveforms,
    /// though nothing's saved until <see cref="EndDrag"/>.
    /// </summary>
    public void DragTo(double seconds)
    {
        if (_drag is not { } drag || drag.Path != Deck.FilePath)
            return;
        var now = GridEditing.MoveBeat(drag.Start, drag.Beat, seconds);
        _drag = drag with { Now = now };
        Deck.Analysis = drag.Analysis with { Grid = now.Grid };
    }

    /// <summary>
    /// Lets go of the beat being dragged, saving where it was left.
    /// </summary>
    public void EndDrag()
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        if (drag.Now is not { } now || drag.Path != Deck.FilePath)
            return;
        Use(drag.Path, _library.Find(drag.Path), drag.Analysis with { Grid = now.Grid }, now);
        Log?.Invoke(
            now.Pins.Count switch
            {
                1 => $"Deck {Deck.Name}: beat moved to {Format(now.Pins[0])}; the grid moves with it",
                2 => $"Deck {Deck.Name}: two beats pinned; {now.Grid.MainBpm:F2} BPM between them",
                var count => $"Deck {Deck.Name}: {count} beats pinned; {Tempos(now.Grid)}",
            }
        );
    }

    /// <summary>
    /// Takes out the pin nearest <paramref name="seconds"/>, as Shift + SET BEAT does at the playhead.
    /// </summary>
    public void RemovePin(double seconds)
    {
        if (Deck.FilePath is not { } path || Deck.Analysis is not { } analysis || Current() is not { Pins.Count: > 0 } edit)
            return;
        var removed = GridEditing.RemovePin(edit, seconds);
        Use(path, _library.Find(path), analysis with { Grid = removed.Grid }, removed);
        Log?.Invoke($"Deck {Deck.Name}: pin taken out; {removed.Pins.Count switch { 0 => "none left", 1 => "1 left", var n => $"{n} left" }}");
    }

    /// <summary>
    /// The tempos a grid goes through, slowest to fastest, for the status line.
    /// </summary>
    static string Tempos(BeatGrid grid)
    {
        var tempos = grid.Markers.Select(marker => marker.Bpm).ToList();
        return tempos.Count == 1 ? $"{tempos[0]:F2} BPM" : $"{tempos.Min():F2} to {tempos.Max():F2} BPM";
    }

    /// <summary>
    /// Saves an edit (or forgets one, for null) and gives the analysis to the deck and its song.
    /// </summary>
    void Use(string path, Song? song, TrackAnalysis analysis, GridEdit? edit)
    {
        if (edit is not null)
            _database.SetGridEdit(path, edit);
        try
        {
            _database.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"Couldn't save the grid to {_database.FilePath}: {e.Message}");
        }
        Deck.Analysis = analysis;
        if (song is not null)
            song.Analysis = analysis;
    }

    static string Format(double seconds) => $"{(int)(seconds / 60)}:{seconds % 60:00.000}";
}
