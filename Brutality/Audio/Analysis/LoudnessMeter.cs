namespace Brutality.Audio.Analysis;

/// <summary>
/// Measures a song's integrated loudness in LUFS, as ITU-R BS.1770-4 and EBU R128 define it: K-weighted (a high
/// shelf for the head's effect and a high-pass for the ear's insensitivity to deep bass), averaged over 400 ms blocks
/// that overlap by 75%, ignoring blocks quieter than -70 LUFS and then those more than 10 LU below the rest, so
/// silences and quiet intros don't drag it down. Feed it samples as they decode; it also tracks the sample peak.
/// </summary>
sealed class LoudnessMeter
{
    /// <summary>
    /// What silence measures as: the absolute gate, below which nothing counts.
    /// </summary>
    public const double SILENCE = -70;

    const double RELATIVE_GATE = -10;

    readonly int _channels;
    readonly double _weight;
    readonly Biquad[] _shelf;
    readonly Biquad[] _highPass;
    readonly int _subBlockFrames;
    readonly List<double> _subBlocks = [];
    double _subBlockEnergy;
    int _subBlockFilled;
    float _peak;

    public LoudnessMeter(int channels, int sampleRate)
    {
        _channels = channels;
        // A mono file plays through both speakers, so it counts twice, as the same audio in stereo would.
        _weight = channels == 1 ? 2 : 1;
        _subBlockFrames = Math.Max(1, sampleRate / 10);

        // The K-weighting filters' analogue prototypes, matched at any sample rate as libebur128 does.
        var shelf = Shelf(sampleRate);
        var highPass = HighPass(sampleRate);
        _shelf = Enumerable.Repeat(shelf, channels).ToArray();
        _highPass = Enumerable.Repeat(highPass, channels).ToArray();
    }

    /// <summary>
    /// The largest absolute sample so far, on a linear scale where 1 is full scale.
    /// </summary>
    public float Peak => _peak;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        for (var i = 0; i + _channels <= interleaved.Length; i += _channels)
        {
            var energy = 0.0;
            for (var c = 0; c < _channels; c++)
            {
                var x = interleaved[i + c];
                _peak = MathF.Max(_peak, MathF.Abs(x));
                var y = _highPass[c].Process(_shelf[c].Process(x));
                energy += y * y;
            }
            _subBlockEnergy += energy * _weight;

            if (++_subBlockFilled == _subBlockFrames)
            {
                _subBlocks.Add(_subBlockEnergy / _subBlockFrames);
                _subBlockEnergy = 0;
                _subBlockFilled = 0;
            }
        }
    }

    /// <summary>
    /// The song's loudness so far, in LUFS; <see cref="SILENCE"/> if nothing has passed the gates.
    /// </summary>
    public double IntegratedLoudness
    {
        get
        {
            // Each 400 ms block is four 100 ms sub-blocks, starting a sub-block apart.
            var blocks = new List<double>();
            for (var i = 3; i < _subBlocks.Count; i++)
                blocks.Add((_subBlocks[i - 3] + _subBlocks[i - 2] + _subBlocks[i - 1] + _subBlocks[i]) / 4);

            var aboveSilence = blocks.Where(energy => Lufs(energy) > SILENCE).ToList();
            if (aboveSilence.Count == 0)
                return SILENCE;

            var relativeGate = Lufs(aboveSilence.Average()) + RELATIVE_GATE;
            var counted = aboveSilence.Where(energy => Lufs(energy) > relativeGate).ToList();
            return counted.Count == 0 ? SILENCE : Math.Max(SILENCE, Lufs(counted.Average()));
        }
    }

    static double Lufs(double energy) => energy <= 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(energy);

    static Biquad Shelf(int sampleRate)
    {
        const double F0 = 1681.974450955533;
        const double G = 3.999843853973347;
        const double Q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * F0 / sampleRate);
        var vh = Math.Pow(10, G / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / Q + k * k;
        return new Biquad(
            (vh + vb * k / Q + k * k) / a0,
            2 * (k * k - vh) / a0,
            (vh - vb * k / Q + k * k) / a0,
            2 * (k * k - 1) / a0,
            (1 - k / Q + k * k) / a0
        );
    }

    static Biquad HighPass(int sampleRate)
    {
        const double F0 = 38.13547087602444;
        const double Q = 0.5003270373238773;
        var k = Math.Tan(Math.PI * F0 / sampleRate);
        var a0 = 1 + k / Q + k * k;
        return new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / Q + k * k) / a0);
    }
}
