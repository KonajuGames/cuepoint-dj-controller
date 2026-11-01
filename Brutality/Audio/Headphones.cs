using Brutal.SdlApi;

namespace Brutality.Audio;

/// <summary>
/// Somewhere to send the headphone mix, a buffer at a time, from SDL_mixer's audio thread.
/// </summary>
unsafe interface IHeadphoneOutput : IDisposable
{
    void Write(float* samples, int count);
}

/// <summary>
/// What goes to the headphones: the cued decks, the main mix, or a blend, at the headphone volume.
/// </summary>
static class HeadphoneMix
{
    /// <summary>
    /// Fills <paramref name="output"/> from <paramref name="cue"/> (the decks sent to the headphones) and
    /// <paramref name="master"/> (the main mix): <paramref name="cueMix"/> 0 is the cue alone, 1 the main mix alone.
    /// <paramref name="volume"/> goes through the same taper as the faders.
    /// </summary>
    public static void Mix(ReadOnlySpan<float> cue, ReadOnlySpan<float> master, Span<float> output, float cueMix, float volume)
    {
        var mix = Math.Clamp(cueMix, 0, 1);
        var gain = Deck.VolumeTaper(Math.Clamp(volume, 0, 1));
        for (var i = 0; i < output.Length; i++)
        {
            var c = i < cue.Length ? cue[i] : 0;
            var m = i < master.Length ? master[i] : 0;
            output[i] = (c * (1 - mix) + m * mix) * gain;
        }
    }
}

/// <summary>
/// The headphone mix played on an SDL audio device of its own, such as a second sound card. It's fed from the main
/// device's audio thread, so the two devices' clocks drift apart a little over time; if the queue builds up past
/// <see cref="MAX_QUEUED_SECONDS"/>, it's dropped and starts again close behind.
/// </summary>
sealed unsafe class SdlHeadphones : IHeadphoneOutput
{
    const double MAX_QUEUED_SECONDS = 0.15;

    readonly SdlAudioStream _stream;
    readonly int _maxQueuedBytes;

    public string DeviceName { get; }

    /// <summary>
    /// Opens <paramref name="deviceId"/> to play <paramref name="format"/>, which SDL converts to whatever the device
    /// wants. Throws <see cref="SdlException"/> if it can't.
    /// </summary>
    public SdlHeadphones(uint deviceId, string deviceName, SdlAudioSpec format)
    {
        DeviceName = deviceName;
        _stream = Sdl.PInvoke.OpenAudioDeviceStream(deviceId, &format, null, null);
        if (_stream.IsNull())
            throw new SdlException($"Couldn't open {deviceName} for headphones: {Sdl.PInvoke.GetError()}");
        Sdl.PInvoke.ResumeAudioStreamDevice(_stream);
        _maxQueuedBytes = (int)(MAX_QUEUED_SECONDS * format.freq * format.channels * sizeof(float));
    }

    public void Write(float* samples, int count)
    {
        if (Sdl.PInvoke.GetAudioStreamQueued(_stream) > _maxQueuedBytes)
            Sdl.PInvoke.ClearAudioStream(_stream);
        Sdl.PInvoke.PutAudioStreamData(_stream, samples, count * sizeof(float));
    }

    public void Dispose() => Sdl.PInvoke.DestroyAudioStream(_stream);
}
