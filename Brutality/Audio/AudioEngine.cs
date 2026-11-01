using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace Brutality.Audio;

/// <summary>
/// Which end of the crossfader a deck is on. A deck on neither side plays regardless of the crossfader.
/// </summary>
enum CrossfaderSide
{
    None,
    Left,
    Right,
}

/// <summary>
/// SDL's audio subsystem and one SDL_mixer mixer on the default output device, plus the crossfader between its decks.
/// Decks play through it. Only one should exist at a time, since it starts and stops SDL's audio for the whole
/// process.
/// </summary>
sealed unsafe class AudioEngine : IDisposable
{
    const uint DEFAULT_PLAYBACK_DEVICE = 0xFFFFFFFFu; // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK

    internal MixMixer Mixer { get; }

    /// <summary>
    /// The level of the final mix, as it goes to the speakers.
    /// </summary>
    public AudioMeter Meter { get; } = new();

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
            SdlMixer.PInvoke.SetPostMixCallback(Mixer, &AudioMeter.MeasureMix, Meter.UserData);

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

    readonly List<Deck> _decks = [];
    float _crossfader = 0.5f;
    bool _autoGain = true;

    /// <summary>
    /// Whether decks play each song at its analysed automatic gain, so every song comes out about as loud.
    /// </summary>
    public bool AutoGain
    {
        get => _autoGain;
        set
        {
            _autoGain = value;
            foreach (var deck in _decks)
                deck.ApplyGain();
        }
    }

    public Deck CreateDeck(string name, CrossfaderSide side = CrossfaderSide.None)
    {
        var deck = new Deck(this, name, side);
        _decks.Add(deck);
        return deck;
    }

    internal void Remove(Deck deck) => _decks.Remove(deck);

    /// <summary>
    /// The deck <paramref name="deck"/> should sync to: another deck with an analysed song, a playing one if there
    /// is one, or null if there's none.
    /// </summary>
    internal Deck? SyncMasterFor(Deck deck)
    {
        var candidates = _decks.Where(other => other != deck && other.IsLoaded && other.Bpm is not null).ToList();
        return candidates.FirstOrDefault(other => other.IsPlaying) ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// The crossfader: 0 is all the way to the left-hand decks, 1 all the way right, 0.5 the middle.
    /// </summary>
    public float Crossfader
    {
        get => _crossfader;
        set
        {
            _crossfader = Math.Clamp(value, 0, 1);
            foreach (var deck in _decks)
                deck.ApplyGain();
        }
    }

    /// <summary>
    /// How loud the crossfader lets a deck on <paramref name="side"/> through, from 0 to 1. A constant-power curve:
    /// in the middle each side is at about 0.707 (-3 dB), so the two together sound as loud as either alone and a
    /// blend doesn't swell as it passes the centre.
    /// </summary>
    public float CrossfaderGain(CrossfaderSide side) =>
        side switch
        {
            CrossfaderSide.Left => MathF.Cos(_crossfader * MathF.PI / 2),
            CrossfaderSide.Right => MathF.Sin(_crossfader * MathF.PI / 2),
            _ => 1,
        };

    public void Dispose()
    {
        // The mixer first, which stops its callback, so nothing is still measuring when the meter goes.
        Mixer.Dispose();
        Meter.Dispose();
        SdlMixer.Quit();
        Sdl.Init.Quit();
    }
}
