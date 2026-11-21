namespace Cuepoint.Audio.Analysis;

/// <summary>
/// The range of tempos analysed songs are shown in: an octave from <see cref="Min"/> to twice it, chosen in the
/// options so a genre reads as its DJs expect it to, drum &amp; bass at 174 rather than 87, say, or hip-hop at 90
/// rather than 180. Analysis finds tempos from <see cref="BeatDetector.MIN_BPM"/> to twice that; a tempo outside the
/// range chosen is doubled or halved into it, with its beat grid, when the song's looked up, so nothing's analysed
/// again. A grid fixed by hand, or brought in from other DJ software, is left as it is.
/// </summary>
static class TempoRange
{
    /// <summary>
    /// The lowest tempos there are to choose from, each the bottom of an octave.
    /// </summary>
    public static readonly IReadOnlyList<double> Choices = [60, 70, 75, BeatDetector.MIN_BPM, 88, 90, 100];

    static double _min = BeatDetector.MIN_BPM;

    /// <summary>
    /// The slowest tempo shown; the fastest is just under twice it. One of <see cref="Choices"/>, the nearest if set
    /// to another. Set on the UI thread.
    /// </summary>
    public static double Min
    {
        get => Volatile.Read(ref _min);
        set => Volatile.Write(ref _min, Choices.MinBy(choice => Math.Abs(choice - value)));
    }

    /// <summary>
    /// What to call a range, as "88–176 BPM".
    /// </summary>
    public static string Name(double min) => $"{min:0}–{2 * min:0} BPM";

    /// <summary>
    /// <paramref name="analysis"/> with its tempo doubled or halved, beat grid and all, until its main tempo's within
    /// the range from <paramref name="min"/> (<see cref="Min"/> if not given) to twice it; as it is if it already is.
    /// </summary>
    public static TrackAnalysis Fold(TrackAnalysis analysis, double? min = null)
    {
        var low = min ?? Min;
        var edit = new GridEdit(analysis.Grid, []);
        for (var i = 0; i < 4 && edit.Grid.MainBpm < low; i++)
            edit = GridEditing.Scale(edit, 2);
        for (var i = 0; i < 4 && edit.Grid.MainBpm >= 2 * low; i++)
            edit = GridEditing.Scale(edit, 0.5);
        return ReferenceEquals(edit.Grid, analysis.Grid) ? analysis : analysis with { Grid = edit.Grid };
    }
}
