namespace Cuepoint.Audio.Analysis;

/// <summary>
/// The beat grid sums behind quantise and beat jumps: snapping a moment to its nearest beat, moving a number of beats
/// on, and landing somewhere at the same point in the beat as now, so a jump while playing stays in time.
/// </summary>
static class Quantizing
{
    /// <summary>
    /// The beat nearest <paramref name="seconds"/>.
    /// </summary>
    public static double Snap(BeatGrid grid, double seconds) => grid.TimeOfBeat(Math.Round(grid.BeatNumberAt(seconds)));

    /// <summary>
    /// <paramref name="beats"/> beats on from <paramref name="seconds"/> (back, if negative), counting through any
    /// tempo changes, at the same point in the beat.
    /// </summary>
    public static double Jump(BeatGrid grid, double seconds, double beats) => grid.TimeOfBeat(grid.BeatNumberAt(seconds) + beats);

    /// <summary>
    /// Where to land, going to <paramref name="target"/> while at <paramref name="from"/>: the beat nearest the target,
    /// as far into it as <paramref name="from"/> is into its own, so the music carries on in time.
    /// </summary>
    public static double InPhase(BeatGrid grid, double from, double target)
    {
        var at = grid.BeatNumberAt(from);
        return grid.TimeOfBeat(Math.Round(grid.BeatNumberAt(target)) + (at - Math.Floor(at)));
    }

    /// <summary>
    /// How far a song at <paramref name="seconds"/> into <paramref name="grid"/> is ahead of another at
    /// <paramref name="otherSeconds"/> into <paramref name="other"/>, playing at <paramref name="otherBpm"/> as heard,
    /// in beats from -½ up to ½: behind if negative. Beats are compared in the shorter of the two songs' beats, so in
    /// half or double time (85 against 170) every beat of the slower lands on one of the faster's; <c>Units</c> is how
    /// many of those go in one of this song's beats, 1 or 2.
    /// </summary>
    public static (double Beats, double Units) PhaseAhead(BeatGrid grid, double seconds, BeatGrid other, double otherSeconds, double otherBpm)
    {
        var otherBeats = Deck.MasterBeatsPerBeat(grid.BpmAt(seconds), otherBpm);
        var units = Math.Max(otherBeats, 1);
        var phase = Fraction(grid.BeatNumberAt(seconds) * units);
        var otherPhase = Fraction(other.BeatNumberAt(otherSeconds) * units / otherBeats);
        return (Fraction(phase - otherPhase + 0.5) - 0.5, units);
    }

    static double Fraction(double value) => value - Math.Floor(value);

    /// <summary>
    /// Which bar and beat <paramref name="seconds"/> is in, both from 1: bars counted from the one holding the song's
    /// first beat, so the bars before it (in an intro the grid runs back into) are 0, -1 and so on.
    /// </summary>
    public static (long Bar, int Beat) BarAndBeat(BeatGrid grid, double seconds)
    {
        var beat = (long)Math.Floor(grid.BeatNumberAt(seconds) + 1e-6);
        var first = (long)Math.Floor(grid.BeatNumberAt(grid.FirstBeat) + 1e-6);
        var sinceFirstBar = beat - (first - grid.BarPosition(first));
        return (Math.DivRem(sinceFirstBar, BeatGrid.BEATS_PER_BAR) switch
        {
            // Rounded down, so the beats before the first bar count back through whole bars too.
            var (bars, rest) when rest < 0 => bars - 1,
            var (bars, _) => bars,
        } + 1, grid.BarPosition(beat) + 1);
    }

    /// <summary>
    /// How many beats from <paramref name="seconds"/> until <paramref name="target"/>, counting the one it's partway
    /// through: 1 from anywhere in the beat before it. 0 once it's there or past.
    /// </summary>
    public static int BeatsUntil(BeatGrid grid, double seconds, double target) =>
        Math.Max(0, (int)Math.Ceiling(grid.BeatNumberAt(target) - grid.BeatNumberAt(seconds) - 1e-6));

    /// <summary>
    /// <paramref name="beats"/> as bars and beats, as DJ software counts them down: 18 is "4.2", four bars and two.
    /// </summary>
    public static string FormatBars(int beats) => $"{beats / BeatGrid.BEATS_PER_BAR}.{beats % BeatGrid.BEATS_PER_BAR}";
}
