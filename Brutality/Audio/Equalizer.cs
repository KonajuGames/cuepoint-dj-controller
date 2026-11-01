using Brutality.Audio.Analysis;

namespace Brutality.Audio;

/// <summary>
/// A mixer channel's EQ and filter, as on a DJ mixer: bass, mids and treble each from fully off (a kill), through
/// unchanged in the middle, to <see cref="BOOST_DB"/> up; and one filter, a low-pass sweeping down to the left of
/// centre and a high-pass sweeping up to the right, off in the middle. Set from the UI thread; the audio is processed
/// on SDL_mixer's audio thread by <see cref="Process"/>, which reads the settings once a buffer and eases gains across
/// it, so moving a control doesn't click.
/// </summary>
/// <remarks>
/// The bands come from a three-way fourth-order Linkwitz-Riley crossover: the bass split off at
/// <see cref="LOW_CROSSOVER"/>, the rest split into mids and treble at <see cref="HIGH_CROSSOVER"/>, and the bass put
/// through the second split's all-pass so all three stay in phase. That's what lets a kill take a band right out.
/// Together, at unity, they come back as the sound shifted in phase, which can't be heard; but switching suddenly
/// between that and the untouched sound would click, so whenever the EQ comes into use or goes out of it, it fades
/// between the two across a buffer. Untouched, it doesn't process at all.
/// </remarks>
sealed unsafe class Equalizer
{
    public const double LOW_CROSSOVER = 250;
    public const double HIGH_CROSSOVER = 2500;
    public const float BOOST_DB = 6;

    /// <summary>
    /// How far the filter control has to be from the middle before the filter comes in.
    /// </summary>
    public const float FILTER_DEAD_ZONE = 0.03f;

    // Where the filter sweeps to at the ends of its control.
    const double LOW_PASS_BOTTOM = 80;
    const double HIGH_PASS_TOP = 8000;
    const double FILTER_Q = 0.9;

    float _low;
    float _mid;
    float _high;
    float _filter;

    /// <summary>
    /// Each band's control, from -1 (killed) through 0 (unchanged) to 1 (<see cref="BOOST_DB"/> up).
    /// </summary>
    public float Low
    {
        get => _low;
        set => Volatile.Write(ref _low, Math.Clamp(value, -1, 1));
    }

    public float Mid
    {
        get => _mid;
        set => Volatile.Write(ref _mid, Math.Clamp(value, -1, 1));
    }

    public float High
    {
        get => _high;
        set => Volatile.Write(ref _high, Math.Clamp(value, -1, 1));
    }

    /// <summary>
    /// The filter's control: below zero a low-pass, closing down as it goes to -1; above zero a high-pass, opening
    /// up as it goes to 1; off in the middle.
    /// </summary>
    public float Filter
    {
        get => _filter;
        set => Volatile.Write(ref _filter, Math.Clamp(value, -1, 1));
    }

    /// <summary>
    /// A band's gain for its control: squared on the way down, so the bottom of the travel is a smooth fade to
    /// nothing, and in dB on the way up.
    /// </summary>
    public static float BandGain(float control) =>
        control < 0 ? (1 + control) * (1 + control) : MathF.Pow(10, BOOST_DB * control / 20);

    /// <summary>
    /// The filter's corner frequency for its control, swept on a log scale; null in the dead zone, where it's off.
    /// </summary>
    public static double? FilterCutoff(float control) =>
        control <= -FILTER_DEAD_ZONE ? 20000 * Math.Pow(LOW_PASS_BOTTOM / 20000, -control)
        : control >= FILTER_DEAD_ZONE ? 20 * Math.Pow(HIGH_PASS_TOP / 20, control)
        : null;

    // Audio thread only, from here.
    int _rate;
    int _channels;

    /// <summary>
    /// One channel's crossover and filter: each split is two Butterworth sections in a row (a Linkwitz-Riley), and
    /// the bass's all-pass is the second split's low and high halves added back together.
    /// </summary>
    struct Strip
    {
        public Biquad LowA, LowB, RestA, RestB;
        public Biquad MidA, MidB, HighA, HighB;
        public Biquad PhaseLowA, PhaseLowB, PhaseHighA, PhaseHighB;
        public Biquad Filter;
    }

    Strip[] _strips = [];
    float _filterTunedFor = float.NaN;
    float _lastLow = 1;
    float _lastMid = 1;
    float _lastHigh = 1;

    /// <summary>
    /// How much of the output is the processed sound rather than the untouched, at the end of the last buffer.
    /// </summary>
    float _lastWet;

    /// <summary>
    /// EQs and filters <paramref name="count"/> interleaved samples in place. Called on the audio thread.
    /// </summary>
    internal void Process(float* samples, int count, int channels, int rate)
    {
        if (count <= 0 || channels <= 0 || rate <= 0)
            return;

        var low = BandGain(Volatile.Read(ref _low));
        var mid = BandGain(Volatile.Read(ref _mid));
        var high = BandGain(Volatile.Read(ref _high));
        var filter = Volatile.Read(ref _filter);
        var cutoff = FilterCutoff(filter);
        var wet = low == 1 && mid == 1 && high == 1 && cutoff is null ? 0f : 1f;

        // Untouched, and faded out of any use, it leaves the audio exactly as it is.
        if (wet == 0 && _lastWet == 0)
            return;

        // Coming into use, the filters start afresh rather than from whatever they last heard.
        if (_lastWet == 0 || rate != _rate || channels != _channels)
            Prepare(rate, channels);

        if (cutoff is { } corner && filter != _filterTunedFor)
        {
            var nyquistSafe = Math.Min(corner, 0.45 * rate);
            var tuning = filter < 0 ? Biquad.LowPass(nyquistSafe, FILTER_Q, rate) : Biquad.HighPass(nyquistSafe, FILTER_Q, rate);
            for (var c = 0; c < channels; c++)
                _strips[c].Filter = _strips[c].Filter.Retuned(tuning);
            _filterTunedFor = filter;
        }

        var frames = count / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            // Gains, and the mix of processed against untouched, move from the last buffer's to this one's across it.
            var t = (float)(frame + 1) / frames;
            var gainLow = _lastLow + (low - _lastLow) * t;
            var gainMid = _lastMid + (mid - _lastMid) * t;
            var gainHigh = _lastHigh + (high - _lastHigh) * t;
            var mix = _lastWet + (wet - _lastWet) * t;

            for (var c = 0; c < channels; c++)
            {
                ref var strip = ref _strips[c];
                var i = frame * channels + c;
                double x = samples[i];

                var bass = strip.LowB.Process(strip.LowA.Process(x));
                var rest = strip.RestB.Process(strip.RestA.Process(x));
                var mids = strip.MidB.Process(strip.MidA.Process(rest));
                var treble = strip.HighB.Process(strip.HighA.Process(rest));
                bass = strip.PhaseLowB.Process(strip.PhaseLowA.Process(bass)) + strip.PhaseHighB.Process(strip.PhaseHighA.Process(bass));

                var y = gainLow * bass + gainMid * mids + gainHigh * treble;
                if (cutoff is not null)
                    y = strip.Filter.Process(y);
                samples[i] = (float)(x + (y - x) * mix);
            }
        }
        (_lastLow, _lastMid, _lastHigh, _lastWet) = (low, mid, high, wet);
    }

    void Prepare(int rate, int channels)
    {
        (_rate, _channels) = (rate, channels);
        var q = Math.Sqrt(0.5);
        var strip = new Strip
        {
            LowA = Biquad.LowPass(LOW_CROSSOVER, q, rate),
            LowB = Biquad.LowPass(LOW_CROSSOVER, q, rate),
            RestA = Biquad.HighPass(LOW_CROSSOVER, q, rate),
            RestB = Biquad.HighPass(LOW_CROSSOVER, q, rate),
            MidA = Biquad.LowPass(HIGH_CROSSOVER, q, rate),
            MidB = Biquad.LowPass(HIGH_CROSSOVER, q, rate),
            HighA = Biquad.HighPass(HIGH_CROSSOVER, q, rate),
            HighB = Biquad.HighPass(HIGH_CROSSOVER, q, rate),
            PhaseLowA = Biquad.LowPass(HIGH_CROSSOVER, q, rate),
            PhaseLowB = Biquad.LowPass(HIGH_CROSSOVER, q, rate),
            PhaseHighA = Biquad.HighPass(HIGH_CROSSOVER, q, rate),
            PhaseHighB = Biquad.HighPass(HIGH_CROSSOVER, q, rate),
            Filter = Biquad.LowPass(0.45 * rate, FILTER_Q, rate),
        };
        _strips = Enumerable.Repeat(strip, channels).ToArray();
        _filterTunedFor = float.NaN;
        (_lastLow, _lastMid, _lastHigh) = (1, 1, 1);
    }
}
