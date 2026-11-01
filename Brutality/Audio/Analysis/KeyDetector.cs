namespace Brutality.Audio.Analysis;

/// <summary>
/// A musical key: a tonic pitch class (0 is C, 1 is C sharp, up to 11 for B) and major or minor.
/// </summary>
readonly record struct MusicalKey(int Tonic, bool IsMinor)
{
    static readonly string[] PitchNames = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    /// <summary>
    /// "A minor", "Eb major".
    /// </summary>
    public string Name => $"{PitchNames[Tonic]} {(IsMinor ? "minor" : "major")}";

    /// <summary>
    /// "Am", "Eb".
    /// </summary>
    public string ShortName => IsMinor ? PitchNames[Tonic] + "m" : PitchNames[Tonic];

    /// <summary>
    /// The key's place on the Camelot wheel DJs mix by, "8A" for A minor: keys a number apart, or the same number
    /// in the other letter, mix in harmony.
    /// </summary>
    public string Camelot => $"{CamelotNumber}{(IsMinor ? 'A' : 'B')}";

    /// <summary>
    /// The number part of <see cref="Camelot"/>, from 1 to 12.
    /// </summary>
    public int CamelotNumber
    {
        get
        {
            // Round the circle of fifths, C major is 8B and A minor 8A; each fifth up adds one.
            var number = (Tonic * 7 + (IsMinor ? 5 : 8)) % 12;
            return number == 0 ? 12 : number;
        }
    }

    /// <summary>
    /// The key <paramref name="semitones"/> higher (or lower, if negative), as playing faster or slower moves it.
    /// </summary>
    public MusicalKey Transposed(int semitones) => this with { Tonic = ((Tonic + semitones) % 12 + 12) % 12 };

    /// <summary>
    /// Whether the two mix in harmony by the Camelot rules: the same key, one step round the wheel either way, or
    /// its relative major or minor.
    /// </summary>
    public bool MixesWith(MusicalKey other)
    {
        var apart = Math.Abs(CamelotNumber - other.CamelotNumber);
        apart = Math.Min(apart, 12 - apart);
        return IsMinor == other.IsMinor ? apart <= 1 : apart == 0;
    }

    public override string ToString() => Name;

    /// <summary>
    /// Reads a key written as <see cref="Name"/> writes it.
    /// </summary>
    public static MusicalKey? Parse(string? name)
    {
        var parts = name?.Split(' ');
        if (parts is not [var pitch, var mode] || mode is not ("major" or "minor"))
            return null;
        var tonic = Array.IndexOf(PitchNames, pitch);
        return tonic < 0 ? null : new MusicalKey(tonic, mode == "minor");
    }
}

/// <summary>
/// Finds a song's key from how much of each of the twelve pitch classes it has (its chroma), compared against how
/// much of each a piece in each key typically has: Temperley's key profiles, drawn from a corpus of real music. They
/// got 10 of 15 well-known pop and dance songs right, against 7 for Krumhansl and Kessler's listening-test profiles,
/// and most misses are the relative major or minor, which shares a Camelot number and mixes just as well. Weighting
/// the bass more, where the root usually is, only made it worse: kick drums swamp it. Works on mono audio at around
/// 11 kHz.
/// </summary>
static class KeyDetector
{
    // Long frames, about 0.7 s, for the frequency resolution to tell semitones apart in the bass.
    const int FRAME = 8192;
    const int HOP = 4096;

    // A1 to A6: below it there's mostly kick drum, above it mostly harmonics and hiss.
    const double LOWEST = 55;
    const double HIGHEST = 1760;

    static readonly double[] Major = [0.748, 0.060, 0.488, 0.082, 0.670, 0.460, 0.096, 0.715, 0.104, 0.366, 0.057, 0.400];
    static readonly double[] Minor = [0.712, 0.084, 0.474, 0.618, 0.049, 0.460, 0.105, 0.747, 0.404, 0.067, 0.133, 0.330];

    public readonly record struct Result(MusicalKey Key, double Confidence);

    public static Result Detect(ReadOnlySpan<float> mono, int sampleRate, CancellationToken cancel = default)
    {
        var chroma = Chroma(mono, sampleRate, cancel);

        var scores = new List<(MusicalKey Key, double Score)>();
        for (var tonic = 0; tonic < 12; tonic++)
        {
            scores.Add((new MusicalKey(tonic, false), Correlation(chroma, Major, tonic)));
            scores.Add((new MusicalKey(tonic, true), Correlation(chroma, Minor, tonic)));
        }
        scores.Sort((a, b) => b.Score.CompareTo(a.Score));

        // How far the winner stands clear of the runner-up.
        return new Result(scores[0].Key, scores[0].Score - scores[1].Score);
    }

    /// <summary>
    /// The song's pitch-class profile: each frame's spectrum folded into twelve semitones and scaled to sum to one,
    /// so loud passages don't outvote quiet ones, then summed.
    /// </summary>
    internal static double[] Chroma(ReadOnlySpan<float> mono, int sampleRate, CancellationToken cancel = default)
    {
        var fft = new Fft(FRAME);
        var magnitudes = new float[FRAME / 2 + 1];

        // Which pitch class each bin belongs to, if it's in range and close enough to a semitone to count.
        var pitchClassOf = new int[magnitudes.Length];
        for (var k = 0; k < pitchClassOf.Length; k++)
        {
            var frequency = (double)k * sampleRate / FRAME;
            pitchClassOf[k] = -1;
            if (frequency < LOWEST || frequency > HIGHEST)
                continue;
            var midi = 69 + 12 * Math.Log2(frequency / 440);
            var nearest = Math.Round(midi);
            if (Math.Abs(midi - nearest) <= 0.4)
                pitchClassOf[k] = (int)(((long)nearest % 12 + 12) % 12);
        }

        var total = new double[12];
        var frame = new double[12];
        for (var start = 0; start + FRAME <= mono.Length; start += HOP)
        {
            cancel.ThrowIfCancellationRequested();
            fft.Magnitudes(mono.Slice(start, FRAME), magnitudes);
            Array.Clear(frame);
            for (var k = 0; k < magnitudes.Length; k++)
            {
                if (pitchClassOf[k] >= 0)
                    frame[pitchClassOf[k]] += magnitudes[k];
            }

            var sum = frame.Sum();
            if (sum <= 1e-6)
                continue;
            for (var p = 0; p < 12; p++)
                total[p] += frame[p] / sum;
        }
        return total;
    }

    /// <summary>
    /// Pearson correlation of the chroma with a key profile rotated to start on <paramref name="tonic"/>.
    /// </summary>
    static double Correlation(double[] chroma, double[] profile, int tonic)
    {
        var chromaMean = chroma.Average();
        var profileMean = profile.Average();
        double covariance = 0, chromaVariance = 0, profileVariance = 0;
        for (var p = 0; p < 12; p++)
        {
            var c = chroma[(p + tonic) % 12] - chromaMean;
            var k = profile[p] - profileMean;
            covariance += c * k;
            chromaVariance += c * c;
            profileVariance += k * k;
        }
        return chromaVariance <= 0 ? 0 : covariance / Math.Sqrt(chromaVariance * profileVariance);
    }
}
