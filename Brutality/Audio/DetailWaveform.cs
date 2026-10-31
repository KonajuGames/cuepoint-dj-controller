using Brutality.Audio.Analysis;

namespace Brutality.Audio;

/// <summary>
/// A song's waveform in detail, for drawing it scrolling past at a few seconds across: at
/// <see cref="BINS_PER_SECOND"/>, each bin's peak level and how its energy splits between bass, mids and treble,
/// which says what colour to draw it. All are scaled together so the loudest peak is 1.
/// </summary>
sealed class DetailWaveform
{
    public const int BINS_PER_SECOND = 150;

    // Where the bands divide: kick drum and bass below the first, hi-hats and air above the second.
    const double BASS_TOP = 200;
    const double TREBLE_BOTTOM = 2000;

    readonly float[] _peak;
    readonly float[] _bass;
    readonly float[] _mids;
    readonly float[] _treble;

    DetailWaveform(float[] peak, float[] bass, float[] mids, float[] treble)
    {
        (_peak, _bass, _mids, _treble) = (peak, bass, mids, treble);
    }

    public int Count => _peak.Length;

    public double Duration => (double)Count / BINS_PER_SECOND;

    /// <summary>
    /// The peak level in bin <paramref name="bin"/>, from 0 to 1.
    /// </summary>
    public float Peak(int bin) => _peak[bin];

    /// <summary>
    /// How bin <paramref name="bin"/>'s energy splits between bass, mids and treble, as RMS levels.
    /// </summary>
    public (float Bass, float Mids, float Treble) Bands(int bin) => (_bass[bin], _mids[bin], _treble[bin]);

    /// <summary>
    /// The bins from <paramref name="start"/> to <paramref name="end"/> seconds together: the highest peak, and the
    /// bands' RMS levels over them all. Zero outside the song.
    /// </summary>
    public (float Peak, float Bass, float Mids, float Treble) Between(double start, double end)
    {
        if (end <= 0 || start >= Duration)
            return default;
        var first = Math.Max(0, (int)Math.Floor(start * BINS_PER_SECOND));
        var last = Math.Min(Count, Math.Max(first + 1, (int)Math.Ceiling(end * BINS_PER_SECOND)));
        if (first >= last)
            return default;

        float peak = 0, bass = 0, mids = 0, treble = 0;
        for (var i = first; i < last; i++)
        {
            peak = MathF.Max(peak, _peak[i]);
            bass += _bass[i] * _bass[i];
            mids += _mids[i] * _mids[i];
            treble += _treble[i] * _treble[i];
        }
        var n = last - first;
        return (peak, MathF.Sqrt(bass / n), MathF.Sqrt(mids / n), MathF.Sqrt(treble / n));
    }

    /// <summary>
    /// Works through the song once, splitting it into bands with second-order filters as it goes.
    /// </summary>
    public static DetailWaveform From(PcmAudio audio)
    {
        var frames = audio.Frames;
        var samplesPerBin = Math.Max(1, audio.SampleRate / BINS_PER_SECOND);
        var count = (int)((frames + samplesPerBin - 1) / samplesPerBin);
        var peak = new float[count];
        var bass = new float[count];
        var mids = new float[count];
        var treble = new float[count];

        var rate = audio.SampleRate;
        var bassFilter = Biquad.LowPass(BASS_TOP, 0.7071, rate);
        var midsLow = Biquad.HighPass(BASS_TOP, 0.7071, rate);
        var midsHigh = Biquad.LowPass(TREBLE_BOTTOM, 0.7071, rate);
        var trebleFilter = Biquad.HighPass(TREBLE_BOTTOM, 0.7071, rate);

        var samples = audio.Samples;
        var channels = audio.Channels;
        for (var bin = 0; bin < count; bin++)
        {
            var start = (long)bin * samplesPerBin;
            var end = Math.Min(frames, start + samplesPerBin);
            float top = 0;
            double b = 0, m = 0, t = 0;
            for (var frame = start; frame < end; frame++)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    var x = samples[frame * channels + c];
                    top = MathF.Max(top, MathF.Abs(x));
                    sum += x;
                }
                var mono = sum / channels;
                var low = bassFilter.Process(mono);
                var mid = midsHigh.Process(midsLow.Process(mono));
                var high = trebleFilter.Process(mono);
                b += low * low;
                m += mid * mid;
                t += high * high;
            }
            var n = Math.Max(1, end - start);
            peak[bin] = top;
            bass[bin] = (float)Math.Sqrt(b / n);
            mids[bin] = (float)Math.Sqrt(m / n);
            treble[bin] = (float)Math.Sqrt(t / n);
        }

        var loudest = count > 0 ? peak.Max() : 0;
        if (loudest > 0)
        {
            for (var i = 0; i < count; i++)
            {
                peak[i] /= loudest;
                bass[i] /= loudest;
                mids[i] /= loudest;
                treble[i] /= loudest;
            }
        }
        return new DetailWaveform(peak, bass, mids, treble);
    }
}
