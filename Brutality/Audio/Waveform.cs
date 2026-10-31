namespace Brutality.Audio;

/// <summary>
/// A song's loudness from start to end, at a fixed resolution, for drawing an overview. Each bin holds the RMS level
/// of its stretch of the song, scaled so the loudest bin is 1, so quiet recordings still fill the height. RMS rather
/// than peaks because modern masters are limited to near full scale almost throughout, so their peaks draw a flat
/// block, whereas RMS still shows verses, choruses and breakdowns.
/// </summary>
sealed class Waveform
{
    /// <summary>
    /// Enough for a smooth overview at any likely width; a few kilobytes per song.
    /// </summary>
    public const int DEFAULT_BINS = 2048;

    readonly float[] _peaks;

    Waveform(float[] peaks) => _peaks = peaks;

    public int BinCount => _peaks.Length;

    public float this[int bin] => _peaks[bin];

    /// <summary>
    /// The loudest peak between <paramref name="start"/> and <paramref name="end"/>, as fractions of the way through
    /// the song: what a column of pixels covering that stretch should show.
    /// </summary>
    public float PeakBetween(float start, float end)
    {
        var first = Math.Clamp((int)(start * _peaks.Length), 0, _peaks.Length - 1);
        var last = Math.Clamp((int)MathF.Ceiling(end * _peaks.Length), first + 1, _peaks.Length);
        var peak = 0f;
        for (var i = first; i < last; i++)
            peak = MathF.Max(peak, _peaks[i]);
        return peak;
    }

    public static Waveform From(PcmAudio audio, int bins = DEFAULT_BINS)
    {
        var peaks = new float[bins];
        var frames = audio.Frames;
        if (frames == 0)
            return new Waveform(peaks);

        var samples = audio.Samples;
        var channels = audio.Channels;
        for (var bin = 0; bin < bins; bin++)
        {
            var start = frames * bin / bins * channels;
            var end = frames * (bin + 1) / bins * channels;
            var sumOfSquares = 0.0;
            for (var i = start; i < end; i++)
                sumOfSquares += samples[i] * samples[i];
            peaks[bin] = end > start ? (float)Math.Sqrt(sumOfSquares / (end - start)) : 0;
        }

        var loudest = peaks.Max();
        if (loudest > 0)
        {
            for (var bin = 0; bin < bins; bin++)
                peaks[bin] /= loudest;
        }
        return new Waveform(peaks);
    }
}
