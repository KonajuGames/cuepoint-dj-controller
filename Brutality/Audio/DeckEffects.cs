namespace Brutality.Audio;

/// <summary>
/// The effects a deck has, one at a time.
/// </summary>
enum DeckEffect
{
    /// <summary>
    /// Repeats of the sound, a number of beats apart, each quieter.
    /// </summary>
    Echo,

    /// <summary>
    /// The sound in a large room.
    /// </summary>
    Reverb,

    /// <summary>
    /// A sweeping, jet-like comb, as a very short delay moving to and fro: once each number of beats.
    /// </summary>
    Flanger,
}

/// <summary>
/// A deck's effect unit, as on a DJ mixer: one effect at a time (<see cref="Kind"/>), switched <see cref="On"/>, at a
/// <see cref="Level"/>, and for echo and flanger a length in beats (<see cref="Beats"/>) timed by the deck's tempo
/// (<see cref="BeatSeconds"/>, set once a frame). Echo and reverb ring on when switched off, as their repeats and
/// tail fade out naturally; the flanger fades out at once. Set from the UI thread; the audio is processed on SDL_mixer's
/// audio thread by <see cref="Process"/>, which eases every change in, so nothing clicks.
/// </summary>
/// <remarks>
/// The echo is a delay line fed back on itself; changing its length glides there, bending the repeats' pitch as a
/// tape echo would. The reverb is Freeverb (Jezar at Dreampoint's public domain design): eight damped comb filters in
/// parallel, then four all-passes in series, for each channel, the right's a little longer than the left's for width.
/// The flanger reads a delay of a millisecond or so to a few, swept by a slow sine and fed back, mixed with the sound.
/// </remarks>
sealed unsafe class DeckEffects
{
    /// <summary>
    /// The lengths in beats there are to choose from, for echo and flanger.
    /// </summary>
    public static readonly IReadOnlyList<double> BeatLengths = [1.0 / 8, 1.0 / 4, 1.0 / 2, 3.0 / 4, 1, 2, 4, 8, 16];

    /// <summary>
    /// The longest echo, in seconds, whatever the beats and tempo ask for.
    /// </summary>
    public const double MAX_ECHO_SECONDS = 4;

    const float ECHO_FEEDBACK = 0.55f;
    const float FLANGER_FEEDBACK = 0.55f;
    const double FLANGER_MIN_MS = 0.6;
    const double FLANGER_MAX_MS = 4.5;

    // How long switching on or off, or a change of level, takes to ease in; and how long an echo takes to glide to
    // a new length.
    const double RAMP_SECONDS = 0.01;
    const double GLIDE_SECONDS = 0.08;

    // Freeverb's tunings, in samples at 44.1 kHz, and its settings: a large room, lightly damped.
    static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    static readonly int[] AllpassTunings = [556, 441, 341, 225];
    const int STEREO_SPREAD = 23;
    const float ROOM_FEEDBACK = 0.84f;
    const float DAMP = 0.2f;
    const float REVERB_INPUT = 0.015f;
    const float REVERB_WET = 3;

    /// <summary>
    /// How long a switched-off reverb's tail is let ring, in seconds, before it's let go.
    /// </summary>
    const double REVERB_TAIL_SECONDS = 6;

    int _kind;
    int _on;
    float _level = 0.6f;
    float _period = 0.25f;
    int _reset;
    double _beatSeconds = 0.5;
    readonly double[] _beats = [1.0 / 2, 1, 8];

    /// <summary>
    /// Which effect it is. Changing it starts the new one afresh. Set from the UI thread.
    /// </summary>
    public DeckEffect Kind
    {
        get => (DeckEffect)Volatile.Read(ref _kind);
        set
        {
            Volatile.Write(ref _kind, (int)value);
            UpdatePeriod();
        }
    }

    /// <summary>
    /// Whether the effect's on. Set from the UI thread.
    /// </summary>
    public bool On
    {
        get => Volatile.Read(ref _on) != 0;
        set => Volatile.Write(ref _on, value ? 1 : 0);
    }

    /// <summary>
    /// How much of the effect there is, from 0 to 1: the echoes' and reverb's level, and how deep the flanger is.
    /// </summary>
    public float Level
    {
        get => Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, Math.Clamp(value, 0, 1));
    }

    /// <summary>
    /// How many beats apart the echoes are, or how many beats the flanger takes to sweep and back, for the effect
    /// that's chosen: one of <see cref="BeatLengths"/>, each effect keeping its own. The reverb has none. An echo is
    /// at most <see cref="MAX_ECHO_SECONDS"/> long whatever this says.
    /// </summary>
    public double Beats
    {
        get => _beats[(int)Kind];
        set
        {
            _beats[(int)Kind] = BeatLengths.MinBy(length => Math.Abs(Math.Log(length / Math.Max(value, 1e-6))));
            UpdatePeriod();
        }
    }

    /// <summary>
    /// Whether the chosen effect is timed in beats.
    /// </summary>
    public bool HasBeats => Kind != DeckEffect.Reverb;

    /// <summary>
    /// Moves <see cref="Beats"/> along <see cref="BeatLengths"/> by <paramref name="steps"/>, stopping at the ends.
    /// </summary>
    public void StepBeats(int steps)
    {
        var at = BeatLengths.ToList().IndexOf(Beats);
        Beats = BeatLengths[Math.Clamp(at + steps, 0, BeatLengths.Count - 1)];
    }

    /// <summary>
    /// How long a beat is coming out of the deck, in seconds, for timing the effect. Set once a frame from the UI
    /// thread, from the deck's tempo.
    /// </summary>
    public double BeatSeconds
    {
        get => _beatSeconds;
        set
        {
            if (value > 0 && double.IsFinite(value))
            {
                _beatSeconds = value;
                UpdatePeriod();
            }
        }
    }

    void UpdatePeriod()
    {
        var seconds = Beats * _beatSeconds;
        if (Kind == DeckEffect.Echo)
            seconds = Math.Min(seconds, MAX_ECHO_SECONDS);
        Volatile.Write(ref _period, (float)seconds);
    }

    /// <summary>
    /// Stops whatever's ringing on, at once: when the deck stops, say. Asked from the UI thread; done on the audio
    /// thread at the next block.
    /// </summary>
    public void Silence() => Volatile.Write(ref _reset, 1);

    // Audio thread only, from here.
    int _channels;
    int _rate;
    int _lastKind = -1;
    float _feed;
    float _mix;
    long _idleFrames = long.MaxValue;

    float[] _echo = [];
    int _echoFrames;
    int _echoWrite;
    double _echoDelay;

    float[] _flange = [];
    int _flangeWrite;
    double _phase;

    Comb[][] _combs = [];
    Allpass[][] _allpasses = [];

    struct Comb
    {
        public float[] Buffer;
        public int Index;
        public float Store;
    }

    struct Allpass
    {
        public float[] Buffer;
        public int Index;
    }

    // A power of two longer than the flanger's longest delay at 192 kHz, with room to read either side.
    const int FLANGE_FRAMES = 2048;
    const int FLANGE_MASK = FLANGE_FRAMES - 1;

    /// <summary>
    /// Puts the effect on <paramref name="count"/> interleaved samples in place. Called on the audio thread.
    /// </summary>
    internal void Process(float* samples, int count, int channels, int rate)
    {
        if (count <= 0 || channels <= 0 || rate <= 0)
            return;
        if (channels != _channels || rate != _rate)
            Prepare(channels, rate);

        var kind = Volatile.Read(ref _kind);
        if (kind != _lastKind || Interlocked.Exchange(ref _reset, 0) != 0)
        {
            Clear();
            _lastKind = kind;
        }

        var on = Volatile.Read(ref _on) != 0;
        var level = Volatile.Read(ref _level);
        var period = Math.Max(1e-3, Volatile.Read(ref _period));
        var frames = count / channels;

        // Off, and nothing left ringing: nothing to do.
        if (!on && _feed == 0 && _idleFrames > IdleLimit((DeckEffect)kind, period))
            return;

        var step = (float)(1 / (RAMP_SECONDS * rate));
        var targetFeed = on ? 1f : 0f;
        var targetMix = (DeckEffect)kind == DeckEffect.Flanger ? (on ? level : 0) : level;
        switch ((DeckEffect)kind)
        {
            case DeckEffect.Echo:
                Echo(samples, frames, channels, period * rate, targetFeed, targetMix, step);
                break;
            case DeckEffect.Reverb:
                Reverb(samples, frames, channels, targetFeed, targetMix, step);
                break;
            default:
                Flanger(samples, frames, channels, period, targetMix, step);
                break;
        }
        // How long it's been fed nothing, for the echoes or tail to die away; the flanger's gone once it's faded out.
        if ((DeckEffect)kind == DeckEffect.Flanger)
            _idleFrames = _mix == 0 ? long.MaxValue : 0;
        else
            _idleFrames = _feed == 0 ? (_idleFrames > long.MaxValue - frames ? long.MaxValue : _idleFrames + frames) : 0;
    }

    /// <summary>
    /// How long a switched-off effect has to have been fed nothing before what's left of it is too quiet to hear.
    /// </summary>
    long IdleLimit(DeckEffect kind, double period) =>
        kind switch
        {
            // Twenty repeats at the feedback's 0.55 each is below -100 dB.
            DeckEffect.Echo => (long)(Math.Min(period, MAX_ECHO_SECONDS) * _rate * 20),
            DeckEffect.Reverb => (long)(REVERB_TAIL_SECONDS * _rate),
            _ => 0,
        };

    static float Toward(float value, float target, float step) =>
        value < target ? MathF.Min(target, value + step) : MathF.Max(target, value - step);

    void Echo(float* samples, int frames, int channels, double targetDelay, float targetFeed, float targetMix, float step)
    {
        targetDelay = Math.Clamp(targetDelay, 1, _echoFrames - 2);
        if (_echoDelay <= 0)
            _echoDelay = targetDelay;
        var glide = 1 / (GLIDE_SECONDS * _rate);
        for (var frame = 0; frame < frames; frame++)
        {
            _feed = Toward(_feed, targetFeed, step);
            _mix = Toward(_mix, targetMix, step);
            _echoDelay += (targetDelay - _echoDelay) * glide;
            var read = _echoWrite - _echoDelay;
            if (read < 0)
                read += _echoFrames;
            var whole = (int)read;
            var t = (float)(read - whole);
            var next = whole + 1 == _echoFrames ? 0 : whole + 1;
            var at = frame * channels;
            for (var c = 0; c < channels; c++)
            {
                var delayed = _echo[whole * channels + c] * (1 - t) + _echo[next * channels + c] * t;
                var x = samples[at + c];
                _echo[_echoWrite * channels + c] = x * _feed + delayed * ECHO_FEEDBACK;
                samples[at + c] = x + delayed * _mix;
            }
            if (++_echoWrite == _echoFrames)
                _echoWrite = 0;
        }
    }

    void Reverb(float* samples, int frames, int channels, float targetFeed, float targetMix, float step)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            _feed = Toward(_feed, targetFeed, step);
            _mix = Toward(_mix, targetMix, step);
            var at = frame * channels;
            for (var c = 0; c < channels; c++)
            {
                var x = samples[at + c];
                var input = x * _feed * REVERB_INPUT;
                var combs = _combs[c];
                var output = 0f;
                for (var i = 0; i < combs.Length; i++)
                {
                    ref var comb = ref combs[i];
                    var y = comb.Buffer[comb.Index];
                    comb.Store = y * (1 - DAMP) + comb.Store * DAMP;
                    comb.Buffer[comb.Index] = input + comb.Store * ROOM_FEEDBACK;
                    if (++comb.Index == comb.Buffer.Length)
                        comb.Index = 0;
                    output += y;
                }
                var allpasses = _allpasses[c];
                for (var i = 0; i < allpasses.Length; i++)
                {
                    ref var allpass = ref allpasses[i];
                    var buffered = allpass.Buffer[allpass.Index];
                    allpass.Buffer[allpass.Index] = output + buffered * 0.5f;
                    output = buffered - output;
                    if (++allpass.Index == allpass.Buffer.Length)
                        allpass.Index = 0;
                }
                samples[at + c] = x + output * REVERB_WET * _mix;
            }
        }
    }

    void Flanger(float* samples, int frames, int channels, double period, float targetMix, float step)
    {
        var phaseStep = 1 / (period * _rate);
        var min = FLANGER_MIN_MS / 1000 * _rate;
        var depth = (FLANGER_MAX_MS - FLANGER_MIN_MS) / 1000 * _rate;
        for (var frame = 0; frame < frames; frame++)
        {
            _mix = Toward(_mix, targetMix, step);
            _phase += phaseStep;
            if (_phase >= 1)
                _phase -= 1;
            var delay = min + depth * (0.5 - 0.5 * Math.Cos(2 * Math.PI * _phase));
            var read = _flangeWrite - delay;
            var whole = (int)Math.Floor(read);
            var t = (float)(read - whole);
            var at = frame * channels;
            for (var c = 0; c < channels; c++)
            {
                var a = _flange[(whole & FLANGE_MASK) * channels + c];
                var b = _flange[((whole + 1) & FLANGE_MASK) * channels + c];
                var delayed = a + (b - a) * t;
                var x = samples[at + c];
                _flange[(_flangeWrite & FLANGE_MASK) * channels + c] = x + delayed * FLANGER_FEEDBACK;
                // Half and half at full depth: the classic comb.
                samples[at + c] = x + (delayed * (1 - FLANGER_FEEDBACK) - x) * 0.5f * _mix;
            }
            _flangeWrite = (_flangeWrite + 1) & FLANGE_MASK;
        }
    }

    void Prepare(int channels, int rate)
    {
        (_channels, _rate) = (channels, rate);
        _echoFrames = (int)(MAX_ECHO_SECONDS * rate) + 4;
        _echo = new float[_echoFrames * channels];
        _flange = new float[FLANGE_FRAMES * channels];
        var scale = rate / 44100.0;
        _combs = new Comb[channels][];
        _allpasses = new Allpass[channels][];
        for (var c = 0; c < channels; c++)
        {
            var spread = c % 2 == 1 ? STEREO_SPREAD : 0;
            _combs[c] = CombTunings.Select(tuning => new Comb { Buffer = new float[Math.Max(1, (int)((tuning + spread) * scale))] }).ToArray();
            _allpasses[c] = AllpassTunings.Select(tuning => new Allpass { Buffer = new float[Math.Max(1, (int)((tuning + spread) * scale))] }).ToArray();
        }
        Clear();
    }

    void Clear()
    {
        Array.Clear(_echo);
        Array.Clear(_flange);
        foreach (var combs in _combs)
        {
            for (var i = 0; i < combs.Length; i++)
            {
                Array.Clear(combs[i].Buffer);
                combs[i].Store = 0;
            }
        }
        foreach (var allpasses in _allpasses)
        {
            foreach (var allpass in allpasses)
                Array.Clear(allpass.Buffer);
        }
        (_echoWrite, _echoDelay, _flangeWrite, _phase) = (0, 0, 0, 0);
        (_feed, _mix) = (0, 0);
        _idleFrames = long.MaxValue;
    }
}
