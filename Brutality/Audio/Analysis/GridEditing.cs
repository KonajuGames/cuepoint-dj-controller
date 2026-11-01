namespace Brutality.Audio.Analysis;

/// <summary>
/// A beat grid fixed by hand: the grid, and the pins it was made from, the times (in seconds) a beat was set to by
/// hand. One pin shifts the grid; two set a steady tempo through both.
/// </summary>
sealed record GridEdit(BeatGrid Grid, IReadOnlyList<double> Pins);

/// <summary>
/// The changes a DJ makes to a beat grid by hand, each taking the grid as it is (with its pins) and giving the one
/// to use instead. The beat numbering, and so which beat is the "one" of each bar, carries through every change.
/// </summary>
static class GridEditing
{
    /// <summary>
    /// A nudge's step, in seconds, and a fine one's.
    /// </summary>
    public const double NUDGE = 0.005;

    public const double FINE_NUDGE = 0.001;

    /// <summary>
    /// How many pins there can be: two set a steady tempo, which is as far as editing goes for now.
    /// </summary>
    public const int MAX_PINS = 2;

    /// <summary>
    /// The beat (by number, as <see cref="BeatGrid.BeatNumberAt"/> counts) nearest <paramref name="seconds"/>.
    /// </summary>
    public static long NearestBeat(BeatGrid grid, double seconds) => (long)Math.Round(grid.BeatNumberAt(seconds));

    /// <summary>
    /// Every marker moved by <paramref name="seconds"/>, so every beat is: later for positive. Beats keep their
    /// numbers, and bars their downbeats.
    /// </summary>
    public static BeatGrid Shift(BeatGrid grid, double seconds) =>
        new(grid.Markers.Select(marker => marker with { Time = marker.Time + seconds }).ToList(), grid.Duration)
        {
            DownbeatOffset = grid.DownbeatOffset,
        };

    /// <summary>
    /// A steady grid at <paramref name="bpm"/> with a beat at <paramref name="anchor"/> seconds, that beat starting
    /// its bar where beat <paramref name="anchorBeat"/> of <paramref name="before"/> did, so the bars stay put.
    /// </summary>
    public static BeatGrid Steady(BeatGrid before, double anchor, long anchorBeat, double bpm)
    {
        var beatSeconds = 60 / bpm;
        // The marker on the first beat at or after the start, so the numbering starts there.
        var first = ((anchor % beatSeconds) + beatSeconds) % beatSeconds;
        var anchorNumber = (long)Math.Round((anchor - first) / beatSeconds);
        var barPosition = before.BarPosition(anchorBeat);
        var offset = (int)(((anchorNumber - barPosition) % BeatGrid.BEATS_PER_BAR + BeatGrid.BEATS_PER_BAR) % BeatGrid.BEATS_PER_BAR);
        return BeatGrid.Constant(Math.Round(bpm, 4), Math.Round(first, 5), before.Duration).WithDownbeat(offset);
    }

    /// <summary>
    /// A beat pinned at <paramref name="seconds"/>: the grid's nearest beat moved there. With no pin yet, the whole
    /// grid shifts. With one, the grid becomes steady through both, at whatever tempo fits the beats between them
    /// (as the grid counted them). With two already, the nearer one is replaced. Pinning the same beat as the other
    /// pin just moves it, shifting the grid.
    ///
    /// A pin means the grid's nearest beat, so a second pin far from the first should go where the grid is still
    /// less than half a beat out; pinning a nearer beat first, then a farther one, gets there in steps.
    /// </summary>
    public static GridEdit SetBeat(GridEdit edit, double seconds)
    {
        var grid = edit.Grid;
        var pins = edit.Pins.ToList();
        if (pins.Count >= MAX_PINS)
            pins.RemoveAt(pins[0] == pins[1] ? 1 : Math.Abs(pins[0] - seconds) <= Math.Abs(pins[1] - seconds) ? 0 : 1);

        var beat = NearestBeat(grid, seconds);
        if (pins.Count == 0 || NearestBeat(grid, pins[0]) == beat)
            return new GridEdit(Shift(grid, seconds - grid.TimeOfBeat(beat)), [seconds]);

        var other = pins[0];
        var otherBeat = NearestBeat(grid, other);
        var bpm = 60 * Math.Abs(beat - otherBeat) / Math.Abs(seconds - other);
        return new GridEdit(Steady(grid, other, otherBeat, bpm), [.. new[] { other, seconds }.Order()]);
    }

    /// <summary>
    /// The beat nearest <paramref name="seconds"/> made the "one" of its bar.
    /// </summary>
    public static GridEdit Downbeat(GridEdit edit, double seconds)
    {
        var beat = NearestBeat(edit.Grid, seconds);
        return edit with { Grid = edit.Grid.WithDownbeat((int)((beat % BeatGrid.BEATS_PER_BAR + BeatGrid.BEATS_PER_BAR) % BeatGrid.BEATS_PER_BAR)) };
    }

    /// <summary>
    /// The grid moved by <paramref name="seconds"/> (later for positive). With two pins, only the one nearer
    /// <paramref name="playhead"/> moves, and the tempo between them follows; otherwise the whole grid moves, with
    /// its pin.
    /// </summary>
    public static GridEdit Nudge(GridEdit edit, double seconds, double playhead)
    {
        if (edit.Pins.Count < MAX_PINS)
            return new GridEdit(Shift(edit.Grid, seconds), edit.Pins.Select(pin => pin + seconds).ToList());

        var moving = Math.Abs(edit.Pins[0] - playhead) <= Math.Abs(edit.Pins[1] - playhead) ? 0 : 1;
        var (fixedPin, movingPin) = (edit.Pins[1 - moving], edit.Pins[moving]);
        var beats = Math.Abs(NearestBeat(edit.Grid, movingPin) - NearestBeat(edit.Grid, fixedPin));
        var moved = movingPin + seconds;
        if (beats == 0 || Math.Abs(moved - fixedPin) < 1e-3)
            return edit;
        var bpm = 60 * beats / Math.Abs(moved - fixedPin);
        var grid = Steady(edit.Grid, fixedPin, NearestBeat(edit.Grid, fixedPin), bpm);
        return new GridEdit(grid, [.. new[] { fixedPin, moved }.Order()]);
    }

    /// <summary>
    /// The tempo doubled (factor 2) or halved (0.5), from the first marker, with the downbeat where it was. Pins
    /// that no longer fall on a beat (halving loses every other one) go.
    /// </summary>
    public static GridEdit Scale(GridEdit edit, double factor)
    {
        var grid = edit.Grid;
        var markers = grid.Markers.Select(marker => marker with { Bpm = Math.Round(marker.Bpm * factor, 4) }).ToList();

        // The downbeat's beat number doubles or halves with the rest; halving one between two beats takes the earlier.
        var offset = factor > 1
            ? (int)((grid.DownbeatOffset * 2) % BeatGrid.BEATS_PER_BAR)
            : grid.DownbeatOffset / 2;
        var scaled = new BeatGrid(markers, grid.Duration) { DownbeatOffset = offset };
        var pins = edit.Pins.Where(pin => Math.Abs(scaled.BeatNumberAt(pin) - Math.Round(scaled.BeatNumberAt(pin))) < 0.02).ToList();
        return new GridEdit(scaled, pins);
    }

    /// <summary>
    /// One steady tempo in place of a varying one: the grid's main tempo, with the beat nearest
    /// <paramref name="playhead"/> kept where it is, and pinned there.
    /// </summary>
    public static GridEdit MakeSteady(GridEdit edit, double playhead)
    {
        var beat = NearestBeat(edit.Grid, playhead);
        var at = edit.Grid.TimeOfBeat(beat);
        return new GridEdit(Steady(edit.Grid, at, beat, edit.Grid.MainBpm), [at]);
    }
}
