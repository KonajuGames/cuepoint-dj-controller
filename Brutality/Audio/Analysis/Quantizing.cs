namespace Brutality.Audio.Analysis;

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
}
