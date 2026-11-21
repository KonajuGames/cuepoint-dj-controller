namespace Cuepoint.Audio.Analysis;

/// <summary>
/// How much energy a song has, from 1 (a ballad, an ambient intro) to 10 (peak-time, flat out), for sorting and
/// finding songs to build a set's rise and fall with. It's a rule of thumb from three things a DJ hears as energy: how
/// loud the master is, how fast it goes, and how much of it is full-on rather than breakdowns and quiet stretches.
/// </summary>
static class EnergyRating
{
    // The loudness, in LUFS, and tempo, in BPM, that count as no energy and full energy from each.
    const double QUIET = -20;
    const double LOUD = -6;
    const double SLOW = 80;
    const double FAST = 140;

    // How the three count towards the rating.
    const double LOUDNESS_WEIGHT = 0.4;
    const double TEMPO_WEIGHT = 0.25;
    const double FULL_WEIGHT = 0.35;

    /// <summary>
    /// A second of the song counts as full-on when it's at least this much as loud as its loudest stretches.
    /// </summary>
    const double FULL_SHARE = 0.6;

    /// <summary>
    /// The rating for a song <paramref name="loudness"/> LUFS loud at <paramref name="bpm"/>, with
    /// <paramref name="fullShare"/> of it, from 0 to 1, full-on.
    /// </summary>
    public static int Rate(double loudness, double bpm, double fullShare)
    {
        var score =
            LOUDNESS_WEIGHT * Unit(loudness, QUIET, LOUD)
            + TEMPO_WEIGHT * Unit(bpm, SLOW, FAST)
            + FULL_WEIGHT * Math.Clamp(fullShare, 0, 1);
        return 1 + (int)Math.Round(9 * score);
    }

    /// <summary>
    /// How much of <paramref name="mono"/>, at <paramref name="rate"/>, is full-on: the share of its seconds whose
    /// level reaches <see cref="FULL_SHARE"/> of its loudest tenth's, leaving out silence at either end.
    /// </summary>
    public static double FullShare(ReadOnlySpan<float> mono, int rate)
    {
        if (rate <= 0 || mono.Length < rate)
            return 0;
        var levels = new List<double>();
        for (var start = 0; start + rate <= mono.Length; start += rate)
        {
            var sum = 0.0;
            foreach (var sample in mono.Slice(start, rate))
                sum += sample * sample;
            levels.Add(Math.Sqrt(sum / rate));
        }

        // Silence before the song starts and after it ends isn't the song being quiet.
        const double SILENCE = 0.003;
        var first = levels.FindIndex(level => level > SILENCE);
        var last = levels.FindLastIndex(level => level > SILENCE);
        if (first < 0)
            return 0;
        var heard = levels.GetRange(first, last - first + 1);
        var sorted = heard.Order().ToList();
        var loud = sorted[(int)(sorted.Count * 0.9)];
        return loud <= 0 ? 0 : (double)heard.Count(level => level >= loud * FULL_SHARE) / heard.Count;
    }

    static double Unit(double value, double low, double high) => Math.Clamp((value - low) / (high - low), 0, 1);
}
