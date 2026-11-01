namespace Brutality.Audio.Analysis;

/// <summary>
/// Finds which beat of a song's grid is the "one" of each bar, assuming four beats to a bar. Each of the four
/// possible places is scored by how much, on beats there, the music tends to change, since that's where songs change:
/// <list type="bullet">
/// <item>the harmony, against the beat before (chords mostly change on the bar);</item>
/// <item>the loudness, of the four beats after against the four before (sections start on bars);</item>
/// <item>and a little, a kick drum, as low-frequency energy jumping on the beat (kicks fall on the one, though in
/// four-to-the-floor music on every beat, so it counts for less).</item>
/// </list>
/// The place that scores highest across the whole song is taken as the downbeat. Works on mono audio at around 11
/// kHz.
/// </summary>
static class DownbeatDetector
{
    const int FRAME = 2048;

    // The harmony's notes, from A2 to A6, and the kick drum's range.
    const double LOWEST_NOTE = 110;
    const double HIGHEST_NOTE = 1760;
    const double KICK_TOP = 150;

    /// <summary>
    /// How long either side of a beat its kick is measured over.
    /// </summary>
    const double KICK_WINDOW_SECONDS = 0.06;

    const double HARMONY_WEIGHT = 1;
    const double SECTION_WEIGHT = 1;
    const double KICK_WEIGHT = 0.5;

    public readonly record struct Result(int Offset, double Confidence);

    /// <summary>
    /// Which beat number, from 0 to 3 (counting as <see cref="BeatGrid.BeatNumberAt"/> does), starts each bar, and
    /// how clearly: the best place's score over the next best's, less one. A song too short to tell gives 0.
    /// </summary>
    public static Result Detect(ReadOnlySpan<float> mono, int sampleRate, BeatGrid grid, CancellationToken cancel = default)
    {
        var times = grid.Beats().ToList();
        if (times.Count < 4 * BeatGrid.BEATS_PER_BAR + 1)
            return new Result(0, 0);

        var beats = times.Count - 1;
        var chroma = new double[beats][];
        var energy = new double[beats];
        var kick = new double[beats];
        var fft = new Fft(FRAME);
        var magnitudes = new float[FRAME / 2 + 1];
        var pitchClass = PitchClasses(sampleRate);
        var bass = LowPassed(mono, sampleRate);
        var kickWindow = Math.Max(1, (int)(KICK_WINDOW_SECONDS * sampleRate));

        for (var i = 0; i < beats; i++)
        {
            if ((i & 63) == 0)
                cancel.ThrowIfCancellationRequested();

            var start = Math.Clamp((int)(times[i] * sampleRate), 0, mono.Length);
            var end = Math.Clamp((int)(times[i + 1] * sampleRate), start, mono.Length);
            var segment = mono[start..end];

            energy[i] = segment.Length == 0 ? 0 : Math.Sqrt(Sum(segment, x => x * x) / segment.Length);
            chroma[i] = Chroma(segment, fft, magnitudes, pitchClass);

            var before = bass.AsSpan(Math.Max(0, start - kickWindow), Math.Min(kickWindow, start));
            var after = bass.AsSpan(start, Math.Min(kickWindow, bass.Length - start));
            kick[i] = Math.Max(0, Rms(after) - Rms(before));
        }

        // Each beat's changes: harmony against the beat before, loudness of the bar ahead against the bar behind.
        var harmony = new double[beats];
        var section = new double[beats];
        for (var i = 1; i < beats; i++)
            harmony[i] = 1 - Cosine(chroma[i], chroma[i - 1]);
        for (var i = BeatGrid.BEATS_PER_BAR; i + BeatGrid.BEATS_PER_BAR <= beats; i++)
        {
            double ahead = 0, behind = 0;
            for (var k = 0; k < BeatGrid.BEATS_PER_BAR; k++)
            {
                ahead += energy[i + k];
                behind += energy[i - 1 - k];
            }
            section[i] = Math.Abs(Math.Log((ahead + 1e-6) / (behind + 1e-6)));
        }

        // Each feature counts relative to its own average, so none swamps the others by its scale.
        double Average(double[] values) => Math.Max(1e-9, values.Average());
        var (harmonyAverage, sectionAverage, kickAverage) = (Average(harmony), Average(section), Average(kick));

        var scores = new double[BeatGrid.BEATS_PER_BAR];
        var counts = new int[BeatGrid.BEATS_PER_BAR];
        for (var i = 1; i < beats; i++)
        {
            var place = (int)(((long)Math.Round(grid.BeatNumberAt(times[i])) % BeatGrid.BEATS_PER_BAR + BeatGrid.BEATS_PER_BAR) % BeatGrid.BEATS_PER_BAR);
            scores[place] +=
                HARMONY_WEIGHT * harmony[i] / harmonyAverage
                + SECTION_WEIGHT * section[i] / sectionAverage
                + KICK_WEIGHT * kick[i] / kickAverage;
            counts[place]++;
        }
        for (var place = 0; place < scores.Length; place++)
            scores[place] /= Math.Max(1, counts[place]);

        var ranked = Enumerable.Range(0, scores.Length).OrderByDescending(place => scores[place]).ToList();
        var confidence = scores[ranked[1]] > 0 ? scores[ranked[0]] / scores[ranked[1]] - 1 : 0;
        return new Result(ranked[0], confidence);
    }

    /// <summary>
    /// A stretch's pitch-class profile, scaled to unit length: the average over its frames of each frame's spectrum
    /// folded into twelve semitones.
    /// </summary>
    static double[] Chroma(ReadOnlySpan<float> segment, Fft fft, float[] magnitudes, int[] pitchClass)
    {
        var chroma = new double[12];
        if (segment.Length < FRAME)
        {
            // Shorter than a frame (a fast song's beat): one frame, padded with silence.
            Span<float> padded = new float[FRAME];
            segment.CopyTo(padded);
            AddFrame(padded, fft, magnitudes, pitchClass, chroma);
        }
        else
        {
            for (var start = 0; start + FRAME <= segment.Length; start += FRAME / 2)
                AddFrame(segment.Slice(start, FRAME), fft, magnitudes, pitchClass, chroma);
        }

        var length = Math.Sqrt(chroma.Sum(c => c * c));
        if (length > 0)
        {
            for (var p = 0; p < 12; p++)
                chroma[p] /= length;
        }
        return chroma;
    }

    static void AddFrame(ReadOnlySpan<float> frame, Fft fft, float[] magnitudes, int[] pitchClass, double[] chroma)
    {
        fft.Magnitudes(frame, magnitudes);
        for (var k = 0; k < magnitudes.Length; k++)
        {
            if (pitchClass[k] >= 0)
                chroma[pitchClass[k]] += magnitudes[k];
        }
    }

    static int[] PitchClasses(int sampleRate)
    {
        var classes = new int[FRAME / 2 + 1];
        for (var k = 0; k < classes.Length; k++)
        {
            var frequency = (double)k * sampleRate / FRAME;
            classes[k] = frequency < LOWEST_NOTE || frequency > HIGHEST_NOTE
                ? -1
                : (int)(((long)Math.Round(69 + 12 * Math.Log2(frequency / 440)) % 12 + 12) % 12);
        }
        return classes;
    }

    static float[] LowPassed(ReadOnlySpan<float> mono, int sampleRate)
    {
        var first = Biquad.LowPass(KICK_TOP, Math.Sqrt(0.5), sampleRate);
        var second = Biquad.LowPass(KICK_TOP, Math.Sqrt(0.5), sampleRate);
        var output = new float[mono.Length];
        for (var i = 0; i < mono.Length; i++)
            output[i] = (float)second.Process(first.Process(mono[i]));
        return output;
    }

    static double Cosine(double[] a, double[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    static double Rms(ReadOnlySpan<float> samples) => samples.Length == 0 ? 0 : Math.Sqrt(Sum(samples, x => x * x) / samples.Length);

    static double Sum(ReadOnlySpan<float> samples, Func<float, double> of)
    {
        double sum = 0;
        foreach (var sample in samples)
            sum += of(sample);
        return sum;
    }
}
