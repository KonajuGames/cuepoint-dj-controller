using Brutal.SdlApi;

namespace Cuepoint.Audio;

/// <summary>
/// A microphone on an SDL recording device, read a buffer at a time on the main device's audio thread. The two
/// devices' clocks drift apart a little over time, so if more than <see cref="MAX_QUEUED_SECONDS"/> builds up, what's
/// oldest is let go, keeping the voice close behind the music.
/// </summary>
sealed unsafe class Microphone : IDisposable
{
    const double MAX_QUEUED_SECONDS = 0.05;

    readonly SdlAudioStream _stream;
    readonly int _maxQueuedBytes;
    float[] _dropped = [];

    public string DeviceName { get; }

    /// <summary>
    /// Opens <paramref name="deviceId"/> to record in <paramref name="format"/>, which SDL converts it to. Throws
    /// <see cref="SdlException"/> if it can't.
    /// </summary>
    public Microphone(uint deviceId, string deviceName, SdlAudioSpec format)
    {
        DeviceName = deviceName;
        _stream = Sdl.PInvoke.OpenAudioDeviceStream(deviceId, &format, null, null);
        if (_stream.IsNull())
            throw new SdlException($"Couldn't open {deviceName} for the microphone: {Sdl.PInvoke.GetError()}");
        Sdl.PInvoke.ResumeAudioStreamDevice(_stream);
        _maxQueuedBytes = (int)(MAX_QUEUED_SECONDS * format.freq * format.channels * sizeof(float));
    }

    /// <summary>
    /// Fills <paramref name="count"/> samples at <paramref name="into"/> with what's come in, and silence for any that
    /// hasn't yet.
    /// </summary>
    public void Read(float* into, int count)
    {
        var bytes = count * sizeof(float);
        var excess = Sdl.PInvoke.GetAudioStreamAvailable(_stream) - bytes - _maxQueuedBytes;
        if (excess > 0)
        {
            if (_dropped.Length * sizeof(float) < excess)
                Array.Resize(ref _dropped, excess / sizeof(float) + 1);
            fixed (float* dropped = _dropped)
                Sdl.PInvoke.GetAudioStreamData(_stream, dropped, excess);
        }
        var got = Math.Max(0, Sdl.PInvoke.GetAudioStreamData(_stream, into, bytes));
        new Span<float>(into, count)[(got / sizeof(float))..].Clear();
    }

    public void Dispose() => Sdl.PInvoke.DestroyAudioStream(_stream);
}

/// <summary>
/// The microphone put into the main mix: at its level, and with talkover, the music turned down while it's spoken
/// into and back up once it's quiet.
/// </summary>
sealed class MicMix
{
    /// <summary>
    /// How loud the voice has to be for talkover to turn the music down: about -36 dB.
    /// </summary>
    const float TALKOVER_THRESHOLD = 0.016f;

    /// <summary>
    /// How far talkover turns the music down: to a quarter, about -12 dB.
    /// </summary>
    public const float TALKOVER_GAIN = 0.25f;

    // How quickly the music goes down as the voice starts, and comes back once it stops; and how long the voice has
    // to be quiet first, so it doesn't pump between words.
    const double DUCK_SECONDS = 0.03;
    const double RETURN_SECONDS = 0.6;
    const double HOLD_SECONDS = 0.35;

    float _duck = 1;
    long _quietFrames = long.MaxValue;

    /// <summary>
    /// How far the music's turned down now, from 1 (not at all) to <see cref="TALKOVER_GAIN"/>.
    /// </summary>
    public float Duck => Volatile.Read(ref _duck);

    /// <summary>
    /// Puts <paramref name="mic"/> into <paramref name="mix"/> (both <paramref name="channels"/>-channel interleaved)
    /// at <paramref name="gain"/>, turning the music down first with <paramref name="talkover"/>. On the audio thread.
    /// </summary>
    public void Mix(Span<float> mix, ReadOnlySpan<float> mic, int channels, int rate, float gain, bool talkover)
    {
        var down = (float)(1 / (DUCK_SECONDS * rate));
        var up = (float)(1 / (RETURN_SECONDS * rate));
        var hold = (long)(HOLD_SECONDS * rate);
        var duck = _duck;
        for (var at = 0; at + channels <= mix.Length; at += channels)
        {
            var loudest = 0f;
            for (var c = 0; c < channels; c++)
                loudest = MathF.Max(loudest, MathF.Abs(mic[at + c] * gain));
            _quietFrames = loudest > TALKOVER_THRESHOLD ? 0 : _quietFrames == long.MaxValue ? long.MaxValue : _quietFrames + 1;
            var target = talkover && _quietFrames < hold ? TALKOVER_GAIN : 1f;
            duck = duck > target ? MathF.Max(target, duck - down) : MathF.Min(target, duck + up);
            for (var c = 0; c < channels; c++)
                mix[at + c] = mix[at + c] * duck + mic[at + c] * gain;
        }
        Volatile.Write(ref _duck, duck);
    }

    /// <summary>
    /// The music back at full, as when the microphone's switched off.
    /// </summary>
    public void Reset() => (_duck, _quietFrames) = (1, long.MaxValue);
}
