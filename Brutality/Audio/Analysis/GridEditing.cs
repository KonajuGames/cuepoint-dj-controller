namespace Brutality.Audio.Analysis;

/// <summary>
/// A beat grid fixed by hand: the grid, and the pins it was made from, the times (in seconds) a beat was set to by
/// hand, in order. One pin shifts the grid; two set a steady tempo through both; more set the tempo between each pair
/// and the next, for a song whose tempo changes or drifts.
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
    /// How many pins there can be; pinning another beat past this replaces the nearest.
    /// </summary>
    public const int MAX_PINS = 64;

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
    /// A beat pinned at <paramref name="seconds"/>: the grid's nearest beat moved there (see <see cref="MoveBeat"/>).
    ///
    /// A pin means the grid's nearest beat, so a pin far from the others should go where the grid is still less than
    /// half a beat out; pinning a nearer beat first, then a farther one, gets there in steps.
    /// </summary>
    public static GridEdit SetBeat(GridEdit edit, double seconds) => MoveBeat(edit, NearestBeat(edit.Grid, seconds), seconds);

    /// <summary>
    /// Beat <paramref name="beat"/> of the grid (by number, as <see cref="BeatGrid.BeatNumberAt"/> counts) moved to
    /// <paramref name="seconds"/>, and pinned there. With no pin yet, the whole grid shifts. A beat already pinned
    /// moves with its pin: alone, shifting the grid; among others, changing the tempo either side. Any other beat
    /// becomes a pin too, and the tempo between each pin and the next is whatever fits the beats between them (as the
    /// grid counted them), carrying on at the first's before them and the last's after. A beat dragged past a
    /// neighbouring pin goes no further than just short of it.
    /// </summary>
    public static GridEdit MoveBeat(GridEdit edit, long beat, double seconds)
    {
        var grid = edit.Grid;
        var pins = PinnedBeats(edit);
        var index = pins.FindIndex(pin => pin.Beat == beat);
        if (index < 0)
        {
            if (pins.Count >= MAX_PINS)
                pins.RemoveAt(pins.Select((pin, i) => (Distance: Math.Abs(pin.Time - seconds), i)).Min().i);
            pins.Add((seconds, beat));
            pins.Sort((a, b) => a.Beat.CompareTo(b.Beat));
            index = pins.FindIndex(pin => pin.Beat == beat);
        }

        // Kept where the tempo either side of it no more than doubles or halves, so it can't pass a neighbour.
        var (low, high) = (double.NegativeInfinity, double.PositiveInfinity);
        if (index > 0)
        {
            var before = pins[index - 1];
            var span = grid.TimeOfBeat(beat) - grid.TimeOfBeat(before.Beat);
            (low, high) = (before.Time + span / 2, before.Time + span * 2);
        }
        if (index + 1 < pins.Count)
        {
            var after = pins[index + 1];
            var span = grid.TimeOfBeat(after.Beat) - grid.TimeOfBeat(beat);
            (low, high) = (Math.Max(low, after.Time - span * 2), Math.Min(high, after.Time - span / 2));
        }
        pins[index] = (Math.Clamp(seconds, low, Math.Max(low, high)), beat);

        if (pins.Count == 1)
            return new GridEdit(Shift(grid, pins[0].Time - grid.TimeOfBeat(beat)), [pins[0].Time]);
        return new GridEdit(Through(grid, pins), pins.Select(pin => pin.Time).ToList());
    }

    /// <summary>
    /// The pin nearest <paramref name="seconds"/> taken out, and the grid made again from those left: the tempo
    /// through them, or, with one or none left, the grid as it is.
    /// </summary>
    public static GridEdit RemovePin(GridEdit edit, double seconds)
    {
        if (edit.Pins.Count == 0)
            return edit;
        var pins = PinnedBeats(edit);
        pins.RemoveAt(pins.Select((pin, i) => (Distance: Math.Abs(pin.Time - seconds), i)).Min().i);
        var grid = pins.Count >= 2 ? Through(edit.Grid, pins) : edit.Grid;
        return new GridEdit(grid, pins.Select(pin => pin.Time).ToList());
    }

    /// <summary>
    /// Each pin with the beat it's on, in order.
    /// </summary>
    static List<(double Time, long Beat)> PinnedBeats(GridEdit edit) =>
        edit.Pins.Order().Select(pin => (pin, NearestBeat(edit.Grid, pin))).ToList();

    /// <summary>
    /// A grid through <paramref name="pins"/> (at least two, each a time and the beat of <paramref name="before"/>
    /// that's there, in order): from each to the next, the tempo that fits the beats between them, carrying on before
    /// the first and after the last. Bars start on the same beats as before.
    /// </summary>
    static BeatGrid Through(BeatGrid before, IReadOnlyList<(double Time, long Beat)> pins)
    {
        var markers = new List<GridMarker>();
        for (var i = 0; i + 1 < pins.Count; i++)
        {
            var bpm = 60 * (pins[i + 1].Beat - pins[i].Beat) / (pins[i + 1].Time - pins[i].Time);
            // Where the tempo doesn't change, one marker carries on through.
            if (markers.Count > 0 && Math.Abs(markers[^1].Bpm - bpm) < 1e-6)
                continue;
            markers.Add(new GridMarker(pins[i].Time, bpm));
        }

        // The first marker is beat 0 now; it starts its bar where its beat did.
        var barPosition = before.BarPosition(pins[0].Beat);
        return new BeatGrid(markers, before.Duration) { DownbeatOffset = ((-barPosition % BeatGrid.BEATS_PER_BAR) + BeatGrid.BEATS_PER_BAR) % BeatGrid.BEATS_PER_BAR };
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
    /// The grid moved by <paramref name="seconds"/> (later for positive). With two pins or more, only the one nearest
    /// <paramref name="playhead"/> moves, and the tempo either side follows; otherwise the whole grid moves, with
    /// its pin.
    /// </summary>
    public static GridEdit Nudge(GridEdit edit, double seconds, double playhead)
    {
        if (edit.Pins.Count < 2)
            return new GridEdit(Shift(edit.Grid, seconds), edit.Pins.Select(pin => pin + seconds).ToList());

        var nearest = edit.Pins.MinBy(pin => Math.Abs(pin - playhead));
        return MoveBeat(edit, NearestBeat(edit.Grid, nearest), nearest + seconds);
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
