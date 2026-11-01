using System.Runtime.InteropServices;
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
/// SDL's audio subsystem and one SDL_mixer mixer, plus the crossfader between its decks, playing out of an output
/// device (the system's default unless chosen). Decks play through it. Only one should exist at a time, since it
/// starts and stops SDL's audio for the whole process.
/// </summary>
/// <remarks>
/// The mixer mixes at a fixed 48 kHz in stereo; the output (see <see cref="AudioOutput"/>) pulls from it as the
/// device needs, and SDL converts to the device's own format.
///
/// Headphones: each mixing cycle, decks sent to the headphones add their audio (after EQ, before their faders) into
/// a cue buffer from their track callbacks; then, once the mix is done, its callback blends the cue with the main mix
/// and sends that to the headphone output: a device of its own, or the output device's channels 3 and 4 if it has
/// them, as a DJ controller's sound card does.
/// </remarks>
sealed unsafe class AudioEngine : IDisposable
{
    const uint DEFAULT_PLAYBACK_DEVICE = 0xFFFFFFFFu; // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK

    internal MixMixer Mixer { get; }

    AudioOutput? _output;

    /// <summary>
    /// The level of the final mix, as it goes to the speakers.
    /// </summary>
    public AudioMeter Meter { get; } = new();

    /// <summary>
    /// This engine, for SDL_mixer's audio thread to find from its callback.
    /// </summary>
    GCHandle _self;

    // The cue buffer, filled by the decks' callbacks and emptied by the mix's, all on the audio thread.
    float[] _cue = [];
    float[] _phones = [];
    int _cueFilled;

    IHeadphoneOutput? _headphones;
    float _cueMix;
    float _headphoneVolume = 0.8f;

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
            // Not on a device: the output pulls from it, so it can go out of any device, in any layout.
            var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = AudioOutput.CHANNELS, freq = AudioOutput.RATE };
            Mixer = SdlMixer.CreateMixer(&spec);
            _self = GCHandle.Alloc(this);
            SdlMixer.PInvoke.SetPostMixCallback(Mixer, &MixDone, (void*)GCHandle.ToIntPtr(_self));
            _output = new AudioOutput(this, DEFAULT_PLAYBACK_DEVICE, null, null);

            var decoders = new string[SdlMixer.GetNumAudioDecoders()];
            for (var i = 0; i < decoders.Length; i++)
                decoders[i] = SdlMixer.GetAudioDecoder(i) ?? "?";
            Decoders = decoders;
        }
        catch
        {
            if (_self.IsAllocated)
                _self.Free();
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

    /// <summary>
    /// What the headphones hear, from 0 (only the decks sent to them) to 1 (only the main mix).
    /// </summary>
    public float CueMix
    {
        get => _cueMix;
        set => Volatile.Write(ref _cueMix, Math.Clamp(value, 0, 1));
    }

    /// <summary>
    /// The headphone volume, from 0 to 1, through the same taper as the faders.
    /// </summary>
    public float HeadphoneVolume
    {
        get => _headphoneVolume;
        set => Volatile.Write(ref _headphoneVolume, Math.Clamp(value, 0, 1));
    }

    /// <summary>
    /// Where the headphone mix goes, if anywhere. Setting it closes the one before.
    /// </summary>
    public IHeadphoneOutput? Headphones
    {
        get => _headphones;
        set
        {
            if (value == _headphones)
                return;
            IHeadphoneOutput? old;
            // Swapped with the mix held, so the audio thread never writes to one that's going.
            Mixer.Lock();
            try
            {
                old = _headphones;
                _headphones = value;
            }
            finally
            {
                Mixer.Unlock();
            }
            old?.Dispose();
        }
    }

    /// <summary>
    /// The name of the device the headphones are on, if they're on one: a device of their own, or the output device
    /// when they're on its channels 3 and 4.
    /// </summary>
    public string? HeadphoneDevice =>
        _headphones switch
        {
            SdlHeadphones own => own.DeviceName,
            ChannelHeadphones => _output?.DeviceName,
            _ => null,
        };

    /// <summary>
    /// Whether the headphones are on the output device's channels 3 and 4.
    /// </summary>
    public bool HeadphonesOnOutput => _headphones is ChannelHeadphones;

    /// <summary>
    /// The output device's name, or null for the system's default.
    /// </summary>
    public string? OutputDevice => _output?.DeviceName;

    /// <summary>
    /// Whether the output device (one chosen, not the default) can carry the headphones too, on channels 3 and 4.
    /// </summary>
    public bool OutputCanCarryHeadphones => _output is { DeviceName: not null, DeviceChannels: >= 4 };

    /// <summary>
    /// Plays the main mix out of the playback device called <paramref name="deviceName"/>, or the system's default
    /// with null. Headphones on the old output's channels 3 and 4 go with it if the new one has them, and come off if
    /// not. Returns false, keeping the output as it was, if there's no such device or it can't be opened.
    /// </summary>
    public bool UseOutputDevice(string? deviceName)
    {
        if (Find(deviceName) is not { } id)
            return false;
        var split = _headphones is ChannelHeadphones && deviceName is not null && AudioOutput.ChannelsOf(id) >= 4;
        var (oldName, oldSplit) = (_output?.DeviceName, _output?.SplitHeadphones is not null);
        if (!Reopen(id, deviceName, split))
            return false;
        if (oldSplit && !split)
            Headphones = null;
        return true;
    }

    /// <summary>
    /// A device's id by name, or the default's for null; null if there's no such device. Ids change as devices come
    /// and go, so they're looked up each time.
    /// </summary>
    static uint? Find(string? deviceName) =>
        deviceName is null ? DEFAULT_PLAYBACK_DEVICE
        : PlaybackDevices().FirstOrDefault(device => device.Name == deviceName) is { Name: not null } found ? found.Id
        : null;

    /// <summary>
    /// Opens the output on a device, carrying the headphones on channels 3 and 4 or not. If that won't open, it goes
    /// back to how it was and returns false.
    /// </summary>
    bool Reopen(uint id, string? deviceName, bool splitHeadphones)
    {
        var (oldName, oldSplit) = (_output?.DeviceName, _output?.SplitHeadphones);

        // Closed first, so the device is free when it's the same one in a new layout.
        _output?.Dispose();
        _output = null;
        try
        {
            _output = new AudioOutput(this, id, deviceName, splitHeadphones ? _headphones as ChannelHeadphones ?? new ChannelHeadphones() : null);
            return true;
        }
        catch (SdlException)
        {
            try
            {
                if (Find(oldName) is { } oldId)
                    _output = new AudioOutput(this, oldId, oldName, oldSplit);
            }
            catch (SdlException)
            {
                // Nothing to play out of; another choice in the options may do.
            }
            return false;
        }
    }

    /// <summary>
    /// Mixes the next <paramref name="samples"/> (interleaved stereo) into <paramref name="buffer"/>, running the
    /// decks' and the mix's callbacks. Called by the output, on its audio thread.
    /// </summary>
    internal void Generate(float* buffer, int samples)
    {
        var bytes = samples * sizeof(float);
        var made = SdlMixer.Generate(Mixer, buffer, bytes);
        // Whatever it didn't make (or all of it, on an error) is silence.
        var start = Math.Clamp(made, 0, bytes) / sizeof(float);
        new Span<float>(buffer + start, samples - start).Clear();
    }

    /// <summary>
    /// The playback devices there are, such as speakers and second sound cards, by name.
    /// </summary>
    public static IReadOnlyList<(uint Id, string Name)> PlaybackDevices()
    {
        int count;
        var ids = Sdl.PInvoke.GetAudioPlaybackDevices(&count);
        if (ids is null)
            return [];
        try
        {
            var devices = new List<(uint, string)>(count);
            for (var i = 0; i < count; i++)
                devices.Add((ids[i], Sdl.PInvoke.GetAudioDeviceName(ids[i]) ?? $"Device {ids[i]}"));
            return devices;
        }
        finally
        {
            Sdl.PInvoke.Free(ids);
        }
    }

    /// <summary>
    /// Puts the headphones on the playback device called <paramref name="deviceName"/>, or takes them off with null.
    /// The output device itself means its channels 3 and 4, if it has them. Returns false, with the headphones off, if
    /// there's no such device or it can't be opened.
    /// </summary>
    public bool UseHeadphoneDevice(string? deviceName)
    {
        // Off the output's channels 3 and 4, if they were there, before going anywhere else.
        if (_headphones is ChannelHeadphones && deviceName != _output?.DeviceName)
        {
            Headphones = null;
            if (Find(_output?.DeviceName) is { } outputId)
                Reopen(outputId, _output?.DeviceName, false);
        }

        if (deviceName is null)
        {
            Headphones = null;
            return true;
        }
        if (PlaybackDevices().FirstOrDefault(device => device.Name == deviceName) is not { Name: not null } found)
        {
            Headphones = null;
            return false;
        }

        // The output device: its channels 3 and 4, which only a device with four or more has.
        if (deviceName == _output?.DeviceName)
        {
            if (_headphones is ChannelHeadphones)
                return true;
            if (!OutputCanCarryHeadphones)
            {
                Headphones = null;
                return false;
            }
            Headphones = new ChannelHeadphones();
            if (Reopen(found.Id, deviceName, true))
                return true;
            Headphones = null;
            return false;
        }

        SdlAudioSpec format;
        Mixer.GetFormat(&format);
        try
        {
            Headphones = new SdlHeadphones(found.Id, found.Name, format);
            return true;
        }
        catch (SdlException)
        {
            Headphones = null;
            return false;
        }
    }

    /// <summary>
    /// A deck sent to the headphones adds its audio for this mixing cycle. Called on the audio thread.
    /// </summary>
    internal void AddCue(float* samples, int count)
    {
        if (_cue.Length < count)
            Array.Resize(ref _cue, count);
        for (var i = 0; i < count; i++)
            _cue[i] += samples[i];
        _cueFilled = Math.Max(_cueFilled, count);
    }

    /// <summary>
    /// SDL_mixer's callback once the mix is done, on the audio thread: measures it, and sends the headphones their
    /// mix of it and the cue.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void MixDone(void* userData, MixMixer mixer, SdlAudioSpec* spec, float* samples, int count)
    {
        try
        {
            var engine = (AudioEngine)GCHandle.FromIntPtr((nint)userData).Target!;
            engine.Meter.Measure(samples, count, spec->channels);
            engine.FeedHeadphones(samples, count);
        }
        catch
        {
            // Nothing can be done about it on the audio thread, and an exception mustn't escape into SDL.
        }
    }

    void FeedHeadphones(float* master, int count)
    {
        if (_headphones is { } headphones)
        {
            if (_phones.Length < count)
                Array.Resize(ref _phones, count);
            var cue = _cue.AsSpan(0, Math.Min(_cueFilled, count));
            HeadphoneMix.Mix(cue, new ReadOnlySpan<float>(master, count), _phones.AsSpan(0, count), Volatile.Read(ref _cueMix), Volatile.Read(ref _headphoneVolume));
            fixed (float* phones = _phones)
                headphones.Write(phones, count);
        }
        _cue.AsSpan(0, _cueFilled).Clear();
        _cueFilled = 0;
    }

    public void Dispose()
    {
        // The output first, which stops it pulling from the mixer, then the mixer, so nothing is still measuring or
        // feeding the headphones when they go.
        _output?.Dispose();
        _output = null;
        Mixer.Dispose();
        _headphones?.Dispose();
        _headphones = null;
        if (_self.IsAllocated)
            _self.Free();
        SdlMixer.Quit();
        Sdl.Init.Quit();
    }
}
