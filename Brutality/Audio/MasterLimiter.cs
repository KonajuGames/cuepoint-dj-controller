namespace Brutality.Audio;

/// <summary>
/// Keeps the main mix under <see cref="CEILING"/>, however loud the decks get together, without the harshness of
/// clipping. It holds the mix back by <see cref="LOOKAHEAD"/> frames so it sees each peak coming and turns the level
/// down smoothly just in time, then lets it back up over <see cref="RELEASE_SECONDS"/>. On mastered music below the
/// ceiling it does nothing but that short delay. Off (see <see cref="Enabled"/>), the delay stays, so nothing jumps
/// as it's turned on or off. Used on the audio thread only, but for <see cref="Enabled"/> and
/// <see cref="TakeReduction"/>.
/// </summary>
/// <remarks>
/// For each frame it works out the gain that would bring that frame down to the ceiling, takes the least of those
/// over the next <see cref="LOOKAHEAD"/> frames, lets that rise back slowly, and averages it over the lookahead
/// again for a smooth fade down. Every gain in that average already allows for the frame coming out, so the average
/// does too, and the ceiling holds exactly. A hard clamp at full scale after it catches anything else, such as a
/// stray NaN.
/// </remarks>
sealed class MasterLimiter
{
    /// <summary>
    /// The most the mix comes out at: 1 dB under full scale, leaving room for a sound system's own processing.
    /// </summary>
    public const float CEILING = 0.891f;

    /// <summary>
    /// How far ahead it looks, in frames: 2 ms at the mixer's 48 kHz.
    /// </summary>
    public const int LOOKAHEAD = AudioOutput.RATE / 500;

    public const double RELEASE_SECONDS = 0.12;

    const int CHANNELS = AudioOutput.CHANNELS;

    // The mix held back, and the cue held back to match it, each LOOKAHEAD frames long.
    readonly float[] _delay = new float[LOOKAHEAD * CHANNELS];
    readonly float[] _cueDelay = new float[LOOKAHEAD * CHANNELS];
    int _delayAt;
    int _cueDelayAt;

    // The gain each of the last frames needs, for the least over the lookahead; and that least, released, for the
    // average over it.
    readonly float[] _needed = new float[LOOKAHEAD + 1];
    readonly float[] _released = new float[LOOKAHEAD];
    int _neededAt;
    int _releasedAt;
    double _releasedSum = LOOKAHEAD;
    float _release = 1;

    readonly float _releaseRate = (float)(1 - Math.Exp(-1 / (RELEASE_SECONDS * AudioOutput.RATE)));

    float _leastGain = 1;
    volatile bool _enabled = true;

    public MasterLimiter()
    {
        Array.Fill(_needed, 1f);
        Array.Fill(_released, 1f);
    }

    /// <summary>
    /// Whether it limits; off, the mix only goes through its delay, clamped at full scale.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// The most it's turned the mix down since the last call, as a gain from 0 to 1 (1 for not at all), and starts
    /// again. For a light to show it working.
    /// </summary>
    public float TakeReduction() => Interlocked.Exchange(ref _leastGain, 1);

    /// <summary>
    /// Limits <paramref name="samples"/>, interleaved stereo, in place, and holds them back by the lookahead.
    /// </summary>
    public void Process(Span<float> samples)
    {
        var enabled = _enabled;
        var least = 1f;
        for (var i = 0; i + CHANNELS <= samples.Length; i += CHANNELS)
        {
            float left = samples[i], right = samples[i + 1];

            // The gain this frame needs, and the least needed by any frame from it back to the one coming out.
            var peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
            _needed[_neededAt] = enabled && peak > CEILING ? CEILING / peak : 1;
            _neededAt = (_neededAt + 1) % _needed.Length;
            var needed = 1f;
            foreach (var gain in _needed)
                needed = MathF.Min(needed, gain);

            // Straight down to it, but only slowly back up; then averaged over the lookahead, so it fades down.
            _release = needed < _release ? needed : _release + (needed - _release) * _releaseRate;
            _releasedSum += _release - _released[_releasedAt];
            _released[_releasedAt] = _release;
            _releasedAt = (_releasedAt + 1) % _released.Length;
            var applied = (float)Math.Min(1, _releasedSum / LOOKAHEAD);
            least = MathF.Min(least, applied);

            // The frame from a lookahead ago comes out, turned down; this one goes in its place.
            var at = _delayAt * CHANNELS;
            samples[i] = Clamp(_delay[at] * applied);
            samples[i + 1] = Clamp(_delay[at + 1] * applied);
            _delay[at] = left;
            _delay[at + 1] = right;
            _delayAt = (_delayAt + 1) % LOOKAHEAD;
        }

        // Rounding over a long run can leave the sum a hair off, so it's worked out afresh each buffer.
        _releasedSum = 0;
        foreach (var gain in _released)
            _releasedSum += gain;

        if (least < Volatile.Read(ref _leastGain))
            Volatile.Write(ref _leastGain, least);
    }

    /// <summary>
    /// Holds <paramref name="cue"/>, interleaved stereo, back by the same lookahead as the mix, so headphones
    /// blending the two hear them together rather than a little apart.
    /// </summary>
    public void DelayCue(Span<float> cue)
    {
        for (var i = 0; i + CHANNELS <= cue.Length; i += CHANNELS)
        {
            var at = _cueDelayAt * CHANNELS;
            (cue[i], _cueDelay[at]) = (_cueDelay[at], cue[i]);
            (cue[i + 1], _cueDelay[at + 1]) = (_cueDelay[at + 1], cue[i + 1]);
            _cueDelayAt = (_cueDelayAt + 1) % LOOKAHEAD;
        }
    }

    // Full scale is the last word, whatever comes; and nothing that isn't a number gets through.
    static float Clamp(float sample) => float.IsNaN(sample) ? 0 : Math.Clamp(sample, -1f, 1f);
}
