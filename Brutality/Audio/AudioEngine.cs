using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace Brutality.Audio;

/// <summary>
/// SDL's audio subsystem and one SDL_mixer mixer on the default output device. Decks play through it. Only one
/// should exist at a time, since it starts and stops SDL's audio for the whole process.
/// </summary>
sealed unsafe class AudioEngine : IDisposable
{
    const uint DEFAULT_PLAYBACK_DEVICE = 0xFFFFFFFFu; // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK

    internal MixMixer Mixer { get; }

    /// <summary>
    /// The decoders SDL_mixer was built with, such as DRMP3 or WAV; a file in any other format won't load.
    /// </summary>
    public IReadOnlyList<string> Decoders { get; }

    public AudioEngine()
    {
        Sdl.Init.Initialize(SdlInitFlags.Audio);
        SdlMixer.Init();
        try
        {
            // Null takes the device's own format, so SDL doesn't convert on the way out.
            Mixer = SdlMixer.CreateMixerDevice(DEFAULT_PLAYBACK_DEVICE, null);

            var decoders = new string[SdlMixer.GetNumAudioDecoders()];
            for (var i = 0; i < decoders.Length; i++)
                decoders[i] = SdlMixer.GetAudioDecoder(i) ?? "?";
            Decoders = decoders;
        }
        catch
        {
            SdlMixer.Quit();
            Sdl.Init.Quit();
            throw;
        }
    }

    public Deck CreateDeck(string name) => new(this, name);

    public void Dispose()
    {
        Mixer.Dispose();
        SdlMixer.Quit();
        Sdl.Init.Quit();
    }
}
