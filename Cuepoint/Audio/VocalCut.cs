using Cuepoint.Audio.Analysis;

namespace Cuepoint.Audio;

/// <summary>
/// A channel's vocal cut, for a quick acapella-less blend: a voice is mixed dead centre, so what's the same in both
/// sides is taken out, all but its bass (below <see cref="BASS"/>, where the kick and bass line are, also centred) and
/// its very top (above <see cref="AIR"/>, the hi-hats), which are kept. It's not stem separation, so it takes the lead
/// vocal down rather than out, and anything else in the middle with it. Set from the UI thread; on the audio thread,
/// <see cref="Process"/> fades it in and out over a fraction of a second, so it doesn't click. Stereo only: a mono
/// song has no sides to keep.
/// </summary>
sealed unsafe class VocalCut
{
    public const double BASS = 200;
    public const double AIR = 8000;

    /// <summary>
    /// How long it takes to come in or go out, in seconds.
    /// </summary>
    const double FADE_SECONDS = 0.05;

    int _on;

    /// <summary>
    /// Whether the vocals are cut. Set from the UI thread.
    /// </summary>
    public bool On
    {
        get => Volatile.Read(ref _on) != 0;
        set => Volatile.Write(ref _on, value ? 1 : 0);
    }

    // Audio thread only, from here.
    int _rate;
    float _mix;
    Biquad _bassA, _bassB, _airA, _airB;

    /// <summary>
    /// Cuts the vocals from <paramref name="count"/> interleaved stereo samples in place. Called on the audio thread.
    /// </summary>
    internal void Process(float* samples, int count, int channels, int rate)
    {
        if (channels != 2 || count <= 0 || rate <= 0)
            return;
        var target = Volatile.Read(ref _on) != 0 ? 1f : 0f;
        if (target == 0 && _mix == 0)
            return;
        // Coming in, the filters start afresh rather than from whatever they last heard.
        if (_mix == 0 || rate != _rate)
            Prepare(rate);
        var step = (float)(1 / (FADE_SECONDS * rate));
        for (var i = 0; i + 1 < count; i += 2)
        {
            _mix = _mix < target ? MathF.Min(target, _mix + step) : MathF.Max(target, _mix - step);
            double left = samples[i], right = samples[i + 1];
            var middle = (left + right) / 2;
            var side = (left - right) / 2;
            // The middle kept below the voice and above it.
            var kept = _bassB.Process(_bassA.Process(middle)) + _airB.Process(_airA.Process(middle));
            samples[i] = (float)(left + (kept + side - left) * _mix);
            samples[i + 1] = (float)(right + (kept - side - right) * _mix);
        }
    }

    void Prepare(int rate)
    {
        _rate = rate;
        var q = Math.Sqrt(0.5);
        (_bassA, _bassB) = (Biquad.LowPass(BASS, q, rate), Biquad.LowPass(BASS, q, rate));
        (_airA, _airB) = (Biquad.HighPass(AIR, q, rate), Biquad.HighPass(AIR, q, rate));
    }
}
