namespace Brutality.Audio;

/// <summary>
/// Keeps a deck's key while its tempo fader changes its speed. SDL_mixer plays the song faster or slower by
/// resampling it, which moves the key like a record; this moves it back, on the audio thread, by reading the sound
/// through a short delay line at the speed that cancels the change. The same reading moves the key on purpose, by
/// <see cref="Shift"/>, without touching the tempo.
/// </summary>
/// <remarks>
/// Reading slower than the sound arrives (to bring a sped-up song back down) lets the delay grow; reading faster
/// shrinks it. When it reaches <see cref="MAX_DELAY_SECONDS"/> or <see cref="MIN_DELAY_SECONDS"/>, the read jumps
/// back to the middle of the range, to the point there where the waveform best matches what it's playing (found by
/// cross-correlation), and crossfades from one to the other over <see cref="CROSSFADE_SECONDS"/>: the
/// synchronous-overlap-add idea that time-stretchers such as SoundTouch are built on. Sound arrives in the delay line
/// whether it's on or not, so switching it on or off is a short crossfade too.
/// </remarks>
sealed unsafe class KeyLock
{
    public const double MIN_DELAY_SECONDS = 0.02;
    public const double MAX_DELAY_SECONDS = 0.08;
    public const double CROSSFADE_SECONDS = 0.02;

    /// <summary>
    /// How far either side of the middle of the range a jump looks for its best match.
    /// </summary>
    const double SEARCH_SECONDS = 0.01;

    /// <summary>
    /// How much of the waveform is compared when looking for a match.
    /// </summary>
    const double MATCH_SECONDS = 0.01;

    // A power of two, comfortably longer than the longest delay plus a search and a crossfade at 192 kHz.
    const int RING_FRAMES = 1 << 15;
    const int MASK = RING_FRAMES - 1;

    /// <summary>
    /// How many times its buffers have been made, for tests.
    /// </summary>
    internal int PrepareCount { get; private set; }

    bool _enabled;
    float _shift = 1;
    float _latency;

    /// <summary>
    /// Whether it's correcting the key. Set from the UI thread.
    /// </summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set => Volatile.Write(ref _enabled, value);
    }

    /// <summary>
    /// How much higher to play than the key it's keeping (or, without <see cref="Enabled"/>, than the speed puts it),
    /// as a ratio of frequencies: 2^(1/12) a semitone up. 1 leaves the key alone. Set from the UI thread.
    /// </summary>
    public float Shift
    {
        get => Volatile.Read(ref _shift);
        set => Volatile.Write(ref _shift, value);
    }

    /// <summary>
    /// How far behind the track what's heard is, in seconds of the song, while it's on: the delay line's length.
    /// </summary>
    public double LatencySeconds => Volatile.Read(ref _latency);

    // Audio thread only, from here.
    float[] _ring = [];
    int _channels;
    int _rate;
    long _write;
    double _read;
    double _fadeRead;
    int _fadeLeft;
    int _fadeLength;
    float _lastWet;
    int _minDelay;
    int _maxDelay;
    int _search;
    int _match;

    /// <summary>
    /// Corrects <paramref name="count"/> interleaved samples in place for a song playing at <paramref name="speed"/>
    /// times its recorded speed. Called on the audio thread.
    /// </summary>
    internal void Process(float* samples, int count, int channels, int rate, double speed)
    {
        if (count <= 0 || channels <= 0 || rate <= 0 || speed <= 0)
            return;
        if (channels != _channels || rate != _rate)
            Prepare(channels, rate);

        // Reading at the shift alone moves the key from where the speed puts it; dividing by the speed too keeps it.
        var shift = Shift;
        var wet = Enabled || shift != 1 ? 1f : 0f;
        var pitch = (Enabled ? 1 / speed : 1) * shift;
        var frames = count / channels;
        var middle = (_minDelay + _maxDelay) / 2.0;

        for (var frame = 0; frame < frames; frame++)
        {
            var at = frame * channels;
            for (var c = 0; c < channels; c++)
                _ring[(int)(_write & MASK) * channels + c] = samples[at + c];
            _write++;

            // Off, the read keeps its place in the middle of the range, so switching on starts there.
            if (wet == 0 && _lastWet == 0)
            {
                _read = _write - middle;
                _fadeLeft = 0;
                continue;
            }

            var mix = _lastWet + (wet - _lastWet) * (frame + 1f) / frames;
            var fade = _fadeLeft > 0 ? 0.5f - 0.5f * MathF.Cos(MathF.PI * (_fadeLength - _fadeLeft) / _fadeLength) : 1f;
            for (var c = 0; c < channels; c++)
            {
                var corrected = Read(_read, c);
                if (_fadeLeft > 0)
                    corrected = corrected * fade + Read(_fadeRead, c) * (1 - fade);
                var dry = samples[at + c];
                samples[at + c] = dry + (corrected - dry) * mix;
            }

            _read += pitch;
            _fadeRead += pitch;
            if (_fadeLeft > 0)
                _fadeLeft--;

            var delay = _write - _read;
            if (_fadeLeft == 0 && (delay > _maxDelay || delay < _minDelay))
                Splice(middle);
        }

        _lastWet = wet;
        Volatile.Write(ref _latency, wet > 0 ? (float)((_write - _read) / rate * speed) : 0);
    }

    /// <summary>
    /// Jumps the read back to the middle of the delay range, at the nearby point that best matches what it's
    /// playing now, and starts the crossfade there from where it was.
    /// </summary>
    void Splice(double middle)
    {
        var target = _write - middle;
        var bestScore = double.MinValue;
        var best = target;
        for (var offset = -_search; offset <= _search; offset++)
        {
            var candidate = target + offset;
            double dot = 0, energy = 0;
            for (var k = 0; k < _match; k++)
            {
                var here = Mono(_read + k);
                var there = Mono(candidate + k);
                dot += here * there;
                energy += there * there;
            }
            var score = energy > 1e-12 ? dot / Math.Sqrt(energy) : 0;
            if (score > bestScore)
                (bestScore, best) = (score, candidate);
        }

        _fadeRead = _read;
        _read = best;
        _fadeLeft = _fadeLength;
    }

    /// <summary>
    /// The sound on channel <paramref name="channel"/> at a fractional place in the ring, by cubic (Hermite)
    /// interpolation between the four frames around it.
    /// </summary>
    float Read(double position, int channel)
    {
        var whole = (long)Math.Floor(position);
        var t = (float)(position - whole);
        var y0 = Sample(whole - 1, channel);
        var y1 = Sample(whole, channel);
        var y2 = Sample(whole + 1, channel);
        var y3 = Sample(whole + 2, channel);
        var c1 = 0.5f * (y2 - y0);
        var c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        var c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * t + c2) * t + c1) * t + y1;
    }

    float Sample(long frame, int channel) => _ring[(int)(frame & MASK) * _channels + channel];

    float Mono(double position)
    {
        var frame = (long)position;
        var sum = 0f;
        for (var c = 0; c < _channels; c++)
            sum += Sample(frame, c);
        return sum;
    }

    /// <summary>
    /// Makes its buffers for sound of this shape. Called before the audio thread starts on it, so that thread needn't;
    /// it does only if the shape changes, as with another sound card.
    /// </summary>
    internal void Prepare(int channels, int rate)
    {
        PrepareCount++;
        (_channels, _rate) = (channels, rate);
        _ring = new float[RING_FRAMES * channels];
        _minDelay = (int)(MIN_DELAY_SECONDS * rate);
        _maxDelay = (int)(MAX_DELAY_SECONDS * rate);
        _fadeLength = Math.Max(1, (int)(CROSSFADE_SECONDS * rate));
        _search = (int)(SEARCH_SECONDS * rate);
        _match = Math.Max(1, (int)(MATCH_SECONDS * rate));
        _write = 0;
        _read = -(_minDelay + _maxDelay) / 2.0;
        _fadeLeft = 0;
    }
}
