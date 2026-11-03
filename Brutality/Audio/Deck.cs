using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;
using Brutality.Audio.Analysis;

namespace Brutality.Audio;

/// <summary>
/// One player: a song loaded onto an SDL_mixer track, with play/pause, cue, hotcues, loops, a tempo fader, sync and
/// a position. Get one from <see cref="AudioEngine.CreateDeck"/>. Everything except decoding happens on the thread that calls it, which
/// should be the UI thread; call <see cref="Update"/> once a frame to finish background loads.
/// </summary>
/// <remarks>
/// The song's samples stay in memory and SDL_mixer plays straight from them. A loop is played as just its stretch
/// of those samples, set to repeat, so the mixer loops it without a gap; leaving the loop puts the whole song back
/// on at the matching place. Positions on the track are then counted from the loop's start, which
/// <see cref="Position"/> allows for.
/// </remarks>
sealed partial class Deck : IDisposable
{
    /// <summary>
    /// A decoded song, ready to put on a deck: done off the UI thread, so loading doesn't stall the screen.
    /// </summary>
    sealed record DecodedSong(
        string Path,
        PcmAudio Pcm,
        MixAudio Audio,
        string Title,
        string Artist,
        TimeSpan Duration,
        Waveform Waveform,
        DetailWaveform Detail
    );

    public string Name { get; }

    public string? FilePath { get; private set; }

    /// <summary>
    /// From the file's tags, falling back to the file name.
    /// </summary>
    public string Title { get; private set; } = "";

    public string Artist { get; private set; } = "";

    public TimeSpan Duration { get; private set; }

    /// <summary>
    /// The loaded song's overview, for drawing; null when nothing's loaded.
    /// </summary>
    public Waveform? Waveform { get; private set; }

    /// <summary>
    /// The loaded song's waveform in detail, with its frequency content, for drawing scrolling past.
    /// </summary>
    public DetailWaveform? Detail { get; private set; }

    public bool IsLoaded => !_audio.IsNull();

    /// <summary>
    /// A song is decoding in the background; what's loaded now carries on until it's ready.
    /// </summary>
    public bool IsLoading => _pending is not null;

    /// <summary>
    /// The file being loaded, while <see cref="IsLoading"/>.
    /// </summary>
    public string? LoadingPath => _pending?.Path;

    /// <summary>
    /// What to call the song being loaded, while <see cref="IsLoading"/>: the name given to
    /// <see cref="BeginLoad"/>, or the file name.
    /// </summary>
    public string? LoadingName => _pending is { } pending ? pending.Name : null;

    /// <summary>
    /// A background load finished and the song is on the deck. Raised from <see cref="Update"/>.
    /// </summary>
    public event Action<Deck>? Loaded;

    /// <summary>
    /// A background load failed; the deck keeps what it had. Raised from <see cref="Update"/>.
    /// </summary>
    public event Action<Deck, string, Exception>? LoadFailed;

    readonly AudioEngine _engine;
    readonly MixTrack _track;
    MixAudio _audio;

    /// <summary>
    /// The loaded song's samples, pinned, which <see cref="_audio"/> and any loop play straight from.
    /// </summary>
    PcmAudio? _pcm;

    /// <summary>
    /// The loop's stretch of the song, on the track while looping; null otherwise.
    /// </summary>
    MixAudio _loopAudio;

    long _loopStart;
    long _loopEnd;
    double _loopStartBeat;
    double _loopBeats = 4;

    // Whether the loop going on was set by hand, with loop in and out, rather than a number of beats; where loop in
    // was marked; and the last loop there was, for reloop.
    bool _manualLoop;
    TimeSpan? _loopIn;
    (long Start, long End, bool Manual, double StartBeat)? _lastLoop;

    /// <summary>
    /// The background load in progress, if any. A newer load replaces it; the older one's result is thrown away.
    /// </summary>
    (string Path, string Name, Task<DecodedSong> Task)? _pending;

    long _cueFrames;
    bool _previewing;

    /// <summary>
    /// This deck, for SDL_mixer's audio thread to find from its callback.
    /// </summary>
    GCHandle _self;

    internal Deck(AudioEngine engine, string name, CrossfaderSide side)
    {
        Name = name;
        _engine = engine;
        _side = side;
        _track = engine.Mixer.CreateTrack();
        _self = GCHandle.Alloc(this);
        unsafe
        {
            SdlMixer.PInvoke.SetTrackCookedCallback(_track, &ProcessTrack, (void*)GCHandle.ToIntPtr(_self));
        }
        ApplyGain();
    }

    float _volume = 1;
    CrossfaderSide _side;

    /// <summary>
    /// The deck's volume fader, from 0 (silent) to 1 (full).
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            ApplyGain();
        }
    }

    public CrossfaderSide Side
    {
        get => _side;
        set
        {
            _side = value;
            ApplyGain();
        }
    }

    float _trim;

    /// <summary>
    /// The channel's trim, from -1 to 1 (<see cref="TRIM_DB"/> down to as far up), 0 leaving it as it is: the level
    /// set by hand coming into the channel, on top of the automatic gain, so it's heard ahead of the EQ, the meter and
    /// the headphones.
    /// </summary>
    public float Trim
    {
        get => _trim;
        set
        {
            _trim = Math.Clamp(value, -1, 1);
            ApplyGain();
        }
    }

    /// <summary>
    /// How far <see cref="Trim"/> turns the level either way, in decibels.
    /// </summary>
    public const float TRIM_DB = 12;

    float TrimFactor => MathF.Pow(10, _trim * TRIM_DB / 20);

    TrackAnalysis? _analysis;

    /// <summary>
    /// The loaded song's beat grid, loudness and key, once known; set by whoever analyses songs, and cleared when
    /// another song loads. Its <see cref="TrackAnalysis.AutoGain"/> brings the song to the same loudness as the rest.
    /// </summary>
    public TrackAnalysis? Analysis
    {
        get => _analysis;
        set
        {
            _analysis = value;
            ApplyGain();
        }
    }

    /// <summary>
    /// What the deck plays at in the main mix: the song's automatic gain and the trim, then its fader through an
    /// audio taper, times what the crossfader lets through.
    /// </summary>
    public float Gain => InputGain * ChannelGain;

    float AutoGainFactor => _engine.AutoGain ? _analysis?.AutoGain ?? 1 : 1;

    // The level coming into the channel: the automatic gain and the trim.
    float InputGain => AutoGainFactor * TrimFactor;

    /// <summary>
    /// The fader and crossfader together: what's applied after the EQ, the meter and the headphones have had the
    /// sound, as on a DJ mixer.
    /// </summary>
    float ChannelGain => VolumeTaper(_volume) * _engine.CrossfaderGain(_side);

    /// <summary>
    /// The deck's level as it comes into its mixer channel: after the automatic gain and the EQ, but before the fader
    /// and crossfader, as a DJ mixer's channel meters show it, so a deck being cued shows its level too.
    /// </summary>
    public AudioMeter Meter { get; } = new();

    bool _headphones;

    /// <summary>
    /// Whether the deck goes to the headphones (its PFL is on): after its EQ but before its fader, so it can be heard
    /// there with the fader down.
    /// </summary>
    public bool Headphones
    {
        get => Volatile.Read(ref _headphones);
        set => Volatile.Write(ref _headphones, value);
    }

    // The fader and crossfader's gain: what the UI last set, and what the audio thread last applied, which it eases
    // towards across each buffer so moving them doesn't click.
    float _channelGain = 1;
    float _appliedChannelGain = 1;

    /// <summary>
    /// The deck's mixer channel EQ and filter.
    /// </summary>
    public Equalizer Equalizer { get; } = new();

    /// <summary>
    /// The deck's echo, reverb or flanger, after its EQ, timed by its tempo.
    /// </summary>
    public DeckEffects Effects { get; } = new();

    readonly KeyLock _keyLock = new();

    /// <summary>
    /// The deck's speed for the audio thread, as the tempo fader last set it.
    /// </summary>
    float _speed = 1;

    /// <summary>
    /// Whether the deck keeps the song's key whatever its tempo fader does, rather than the key moving with the
    /// speed as on a turntable. It costs a delay of a few hundredths of a second, which <see cref="Position"/> allows
    /// for, so what's shown and synced is what's heard.
    /// </summary>
    public bool KeyLocked
    {
        get => _keyLock.Enabled;
        set => _keyLock.Enabled = value;
    }

    bool _metronome;

    /// <summary>
    /// Whether the deck clicks on its grid's beats as it plays (higher on each bar's first beat), to hear the grid
    /// against the music while fixing it. The clicks go to the headphones, or the main mix if there are none, and
    /// aren't touched by the fader.
    /// </summary>
    public bool Metronome
    {
        get => Volatile.Read(ref _metronome);
        set => Volatile.Write(ref _metronome, value);
    }

    const double CLICK_SECONDS = 0.025;
    const double CLICK_DECAY_SECONDS = 0.006;
    const double CLICK_HZ = 1600;
    const double BAR_CLICK_HZ = 2400;
    const float CLICK_LEVEL = 0.35f;

    // The clicks for one block, on the audio thread.
    float[] _clicks = [];

    /// <summary>
    /// SDL_mixer's callback for the deck's audio, after its gain and just before it's mixed, on the audio thread:
    /// the EQ changes it there, then the meter measures it.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static unsafe void ProcessTrack(void* userData, MixTrack track, SdlAudioSpec* spec, float* samples, int count)
    {
        try
        {
            var deck = (Deck)GCHandle.FromIntPtr((nint)userData).Target!;
            var channels = spec->channels;
            // A scratched or spinning record's sound is rendered by the platter, its pitch following the record, so
            // key lock is passed over meanwhile.
            if (!deck.RenderPlatter(samples, count, channels, spec->freq))
                deck._keyLock.Process(samples, count, channels, spec->freq, Volatile.Read(ref deck._speed));
            deck.Equalizer.Process(samples, count, channels, spec->freq);
            deck.Effects.Process(samples, count, channels, spec->freq);
            deck.Meter.Measure(samples, count, channels);
            if (deck.Headphones)
                deck._engine.AddCue(samples, count);
            deck.ApplyChannelGain(samples, count, channels);
            if (deck.Metronome)
                deck.Click(samples, count, channels, spec->freq);
        }
        catch
        {
            // Nothing can be done about it on the audio thread, and an exception mustn't escape into SDL.
        }
    }

    /// <summary>
    /// The metronome's clicks for this block of output, on the grid's beats: to the headphones, or into the block
    /// itself (after the fader) if there are none. The block's last sample is at the deck's position now; each sample
    /// before it is earlier in the song by the deck's speed, so the clicks follow the tempo fader and key lock.
    /// </summary>
    unsafe void Click(float* samples, int count, int channels, int rate)
    {
        if (_analysis?.Grid is not { } grid || channels <= 0 || rate <= 0)
            return;
        var frames = count / channels;
        var speed = (double)Volatile.Read(ref _speed);
        var end = Position.TotalSeconds;
        var secondsPerFrame = speed / rate;
        var start = end - frames * secondsPerFrame;

        if (_clicks.Length < count)
            _clicks = new float[count];
        var clicks = _clicks.AsSpan(0, count);
        clicks.Clear();
        var any = false;

        // Every beat whose click sounds in this block: from one a click's length (in song time) before it.
        var first = (long)Math.Ceiling(grid.BeatNumberAt(start - CLICK_SECONDS * speed) - 1e-9);
        var last = (long)Math.Floor(grid.BeatNumberAt(end));
        for (var beat = first; beat <= last; beat++)
        {
            var at = grid.TimeOfBeat(beat);
            var hz = grid.IsBarStart(beat) ? BAR_CLICK_HZ : CLICK_HZ;
            for (var f = 0; f < frames; f++)
            {
                // Output time since the beat, in seconds as heard.
                var since = (start + f * secondsPerFrame - at) / speed;
                if (since < 0 || since >= CLICK_SECONDS)
                    continue;
                var value = (float)(CLICK_LEVEL * Math.Sin(2 * Math.PI * hz * since) * Math.Exp(-since / CLICK_DECAY_SECONDS));
                for (var c = 0; c < channels; c++)
                    clicks[f * channels + c] += value;
                any = true;
            }
        }
        if (!any)
            return;

        if (_engine.HasHeadphones)
        {
            fixed (float* click = clicks)
                _engine.AddCue(click, count);
        }
        else
        {
            for (var i = 0; i < count; i++)
                samples[i] += clicks[i];
        }
    }

    /// <summary>
    /// Fader position to gain. Squared, so the fader's travel feels even to the ear: halfway is about -12 dB rather
    /// than the barely-quieter -6 dB a straight line gives, and the bottom of the travel isn't wasted on near-silence.
    /// </summary>
    public static float VolumeTaper(float position) => position * position;

    /// <summary>
    /// The automatic gain and trim go to SDL_mixer as the track's own gain, ahead of everything; the fader and
    /// crossfader are left for the track's callback, after the headphones have had the sound.
    /// </summary>
    internal void ApplyGain()
    {
        _track.SetGain(InputGain);
        Volatile.Write(ref _inputGain, InputGain);
        Volatile.Write(ref _channelGain, ChannelGain);
    }

    /// <summary>
    /// The tempo fader's ranges, as fractions either way: ±8% for fine matching, then ±16% and ±50% for bigger jumps.
    /// </summary>
    public static readonly IReadOnlyList<double> TempoRanges = [0.08, 0.16, 0.5];

    double _tempo;
    double _tempoRange = TempoRanges[0];

    /// <summary>
    /// The tempo fader: how much faster (or, below zero, slower) than recorded the deck plays, as a fraction, within
    /// <see cref="TempoRange"/>. Like a turntable it changes the key with the speed. It stays where it is when
    /// another song loads, as a fader would.
    /// </summary>
    public double Tempo
    {
        get => _tempo;
        set
        {
            _tempo = Math.Clamp(value, -_tempoRange, _tempoRange);
            ApplySpeed();
        }
    }

    double _bend;

    /// <summary>
    /// A momentary push or drag on the speed, as a fraction on top of the tempo, from a jog wheel nudged to bring
    /// the beats in line by ear: 0.05 plays 5% faster than the tempo says while it lasts. It doesn't move the tempo
    /// fader or count against sync, and key lock holds the key through it.
    /// </summary>
    public double Bend
    {
        get => _bend;
        set
        {
            _bend = Math.Clamp(value, -MAX_BEND, MAX_BEND);
            ApplySpeed();
        }
    }

    public const double MAX_BEND = 0.5;

    void ApplySpeed()
    {
        var speed = (float)(TempoRatio * (1 + _bend));
        _track.SetFrequencyRatio(speed);
        Volatile.Write(ref _speed, speed);
    }

    /// <summary>
    /// How far the tempo fader reaches either way, as a fraction: one of <see cref="TempoRanges"/>. Narrowing it
    /// brings the tempo within it.
    /// </summary>
    public double TempoRange
    {
        get => _tempoRange;
        set
        {
            _tempoRange = Math.Clamp(value, TempoRanges[0], TempoRanges[^1]);
            Tempo = _tempo;
        }
    }

    /// <summary>
    /// How fast the deck plays against the recording: 1.02 at +2%.
    /// </summary>
    public double TempoRatio => 1 + _tempo;

    /// <summary>
    /// The speed SDL_mixer has for the track, read back, for checking that <see cref="Tempo"/> reached it.
    /// </summary>
    internal float AppliedTempoRatio => _track.GetFrequencyRatio();

    /// <summary>
    /// The beats per minute coming out of the deck: the song's tempo where it's playing, sped up or slowed down by
    /// the tempo fader. Null until the song is analysed.
    /// </summary>
    public double? Bpm => _analysis?.Grid.BpmAt(Position.TotalSeconds) * TempoRatio;

    /// <summary>
    /// How far the key's been moved up (or, below zero, down) by hand, in semitones, from -<see cref="MAX_KEY_SHIFT"/>
    /// to <see cref="MAX_KEY_SHIFT"/>, without changing the tempo: to bring a song into a key that mixes with the
    /// other deck's. It's on top of what the tempo fader does to the key, so with <see cref="KeyLocked"/> on it's the
    /// song's own key moved by just this. Back to 0 when another song loads.
    /// </summary>
    public int KeyShift
    {
        get => _keyShift;
        set
        {
            _keyShift = Math.Clamp(value, -MAX_KEY_SHIFT, MAX_KEY_SHIFT);
            _keyLock.Shift = MathF.Pow(2, _keyShift / 12f);
        }
    }

    int _keyShift;

    public const int MAX_KEY_SHIFT = 12;

    /// <summary>
    /// How many semitones the key coming out has moved from the song's, to the nearest: the tempo fader's doing (a
    /// semitone takes about 6% faster; none with <see cref="KeyLocked"/> on) and <see cref="KeyShift"/>.
    /// </summary>
    public int KeyChange => (KeyLocked ? 0 : (int)Math.Round(12 * Math.Log2(TempoRatio))) + _keyShift;

    /// <summary>
    /// The key coming out of the deck: the song's, moved by the tempo fader and the key shift. Null until the song is
    /// analysed.
    /// </summary>
    public MusicalKey? Key => _analysis?.Key.Transposed(KeyChange);

    /// <summary>
    /// How close two decks' tempos have to be to count as in sync, in BPM.
    /// </summary>
    public const double SYNC_TOLERANCE_BPM = 0.05;

    /// <summary>
    /// The deck <see cref="Sync"/> would follow: another with an analysed song, a playing one first.
    /// </summary>
    public Deck? SyncMaster => _engine.SyncMasterFor(this);

    /// <summary>
    /// Whether <see cref="Sync"/> has something to do: this deck's song and another's are both analysed.
    /// </summary>
    public bool CanSync => IsLoaded && Bpm is not null && SyncMaster is not null;

    /// <summary>
    /// Whether this deck's tempo as heard matches its sync master's, allowing for half and double time.
    /// </summary>
    public bool IsInSync =>
        Bpm is { } bpm && SyncMaster?.Bpm is { } master && Math.Abs(bpm * MasterBeatsPerBeat(bpm, master) - master) <= SYNC_TOLERANCE_BPM;

    /// <summary>
    /// How many of the master's beats go in one of this deck's, for the two to line up: 1, or 2 for a song in half
    /// time against the master (85 against 170), or ½ for one in double time.
    /// </summary>
    static double MasterBeatsPerBeat(double bpm, double masterBpm) =>
        new[] { 1.0, 2.0, 0.5 }.MinBy(k => Math.Abs(Math.Log(bpm * k / masterBpm)));

    /// <summary>
    /// Matches this deck to its <see cref="SyncMaster"/>: sets the tempo fader so the two play at the same tempo,
    /// in half or double time if that's closer, widening the fader's range if it has to; and if both are playing,
    /// moves this deck by less than a beat so its beats land on the master's. Returns false, changing nothing, if
    /// there's no master or the tempo is out of the fader's reach.
    /// </summary>
    public bool Sync()
    {
        if (!CanSync || SyncMaster is not { } master || master.Bpm is not { } masterBpm || _analysis?.Grid is not { } grid)
            return false;

        var position = Position.TotalSeconds;
        var songBpm = grid.BpmAt(position);
        var masterBeats = MasterBeatsPerBeat(songBpm, masterBpm);
        var tempo = masterBpm / (songBpm * masterBeats) - 1;
        if (Math.Abs(tempo) > TempoRanges[^1])
            return false;

        if (Math.Abs(tempo) > _tempoRange)
            _tempoRange = TempoRanges.First(range => range >= Math.Abs(tempo));
        Tempo = tempo;

        if (IsPlaying && master.IsPlaying && master._analysis?.Grid is { } masterGrid)
        {
            // Phases are compared in the shorter of the two decks' beats, counted as so many to each of this deck's,
            // so in half or double time every beat of the slower deck lands on one of the faster's.
            // Both positions from the same moment of the mix, so the comparison is fair.
            double masterPosition;
            _engine.Mixer.Lock();
            try
            {
                position = Position.TotalSeconds;
                masterPosition = master.Position.TotalSeconds;
            }
            finally
            {
                _engine.Mixer.Unlock();
            }

            var units = Math.Max(masterBeats, 1);
            var phase = Fraction(grid.BeatNumberAt(position) * units);
            var masterPhase = Fraction(masterGrid.BeatNumberAt(masterPosition) * units / masterBeats);
            var shift = masterPhase - phase;
            if (shift > 0.5)
                shift -= 1;
            else if (shift < -0.5)
                shift += 1;
            Seek(TimeSpan.FromSeconds(position + shift / units * 60 / songBpm));
        }
        return true;
    }

    static double Fraction(double value) => value - Math.Floor(value);

    /// <summary>
    /// The gain the deck plays at in the main mix, read back from SDL_mixer and the callback's setting, for checking
    /// that <see cref="Gain"/> reached them.
    /// </summary>
    internal float AppliedGain => _track.GetGain() * Volatile.Read(ref _channelGain);

    /// <summary>
    /// Applies the fader and crossfader, easing from the last buffer's gain to this one's. On the audio thread.
    /// </summary>
    unsafe void ApplyChannelGain(float* samples, int count, int channels)
    {
        var target = Volatile.Read(ref _channelGain);
        var from = _appliedChannelGain;
        if (from == 1 && target == 1)
            return;
        var frames = Math.Max(1, count / Math.Max(1, channels));
        for (var i = 0; i < count; i++)
            samples[i] *= from + (target - from) * ((float)(i / channels + 1) / frames);
        _appliedChannelGain = target;
    }

    /// <summary>
    /// Where playback is, as heard, from SDL_mixer's audio thread, so it moves on between calls while playing. With
    /// <see cref="KeyLocked"/> on or the key shifted, that's a little behind the track, by the key lock's delay.
    /// </summary>
    public TimeSpan Position =>
        !IsLoaded || _pcm is null ? TimeSpan.Zero
        : IsPlatterActive ? TimeSpan.FromSeconds(Volatile.Read(ref _platterHead) / SampleRate)
        : TimeSpan.FromTicks(Math.Max(0, (ToTime(SongFrame) - TimeSpan.FromSeconds(_keyLock.LatencySeconds)).Ticks));

    /// <summary>
    /// Where playback is in the song, in frames: the track's position, plus the loop's start while it's playing the
    /// loop.
    /// </summary>
    long SongFrame => (IsLooping ? _loopStart : 0) + Math.Max(0, _track.GetPlaybackPosition());

    int SampleRate => _pcm?.SampleRate ?? 44100;

    TimeSpan ToTime(long frames) => TimeSpan.FromSeconds((double)frames / SampleRate);

    long ToFrames(TimeSpan time) => (long)Math.Round(time.TotalSeconds * SampleRate);

    /// <summary>
    /// Whether the deck's playing: while the platter has the sound (scratched, or spinning), whether it'll play on
    /// once it lets go.
    /// </summary>
    public bool IsPlaying => IsLoaded && (IsPlatterActive ? _motor : _track.Playing() && !_track.Paused());

    /// <summary>
    /// Decodes <paramref name="path"/> and puts it on the deck, paused at the start, before returning. Decoding up
    /// front costs a moment (more for .m4a) and some memory, but makes seeking and cueing instant. Throws if neither
    /// SDL_mixer nor (on Windows) Media Foundation can decode the file; the deck keeps whatever it had. See
    /// <see cref="BeginLoad"/> to do the decoding in the background.
    /// </summary>
    public void Load(string path) => Apply(Decode(_engine, path));

    /// <summary>
    /// Starts decoding <paramref name="path"/> on a worker thread. Whatever is on the deck keeps playing until the
    /// new song is ready, when <see cref="Update"/> swaps it in and raises <see cref="Loaded"/> (or
    /// <see cref="LoadFailed"/>). Starting another load before then replaces this one. <paramref name="name"/> is
    /// what to show meanwhile, such as the library's title for it.
    /// </summary>
    public void BeginLoad(string path, string? name = null)
    {
        if (_pending is { } older)
            DiscardWhenDone(older.Task);

        var engine = _engine;
        _pending = (path, name ?? System.IO.Path.GetFileNameWithoutExtension(path), Task.Run(() => Decode(engine, path)));
    }

    /// <summary>
    /// Finishes a background load that's done. Call once a frame from the UI thread.
    /// </summary>
    public void Update()
    {
        UpdatePlatter();
        UpdateRoll();
        // The effects keep time with the beat coming out, or 120 BPM without one.
        Effects.BeatSeconds = 60 / (Bpm is > 0 and var bpm ? bpm : 120);
        if (IsSlipping)
            AdvanceSlip();
        if (_pending is not { Task.IsCompleted: true } done)
            return;
        _pending = null;

        if (done.Task.IsCompletedSuccessfully)
        {
            Apply(done.Task.Result);
            Loaded?.Invoke(this);
        }
        else
            LoadFailed?.Invoke(this, done.Path, done.Task.Exception?.GetBaseException() ?? new Exception("Load failed"));
    }

    /// <summary>
    /// Frees the audio from a load whose result is no longer wanted, whenever it finishes.
    /// </summary>
    static void DiscardWhenDone(Task<DecodedSong> task) =>
        task.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Audio.Dispose();
            },
            TaskScheduler.Default
        );

    /// <summary>
    /// Decodes to samples, builds the waveform from them, then hands them to SDL_mixer; reads tags too. Safe on any
    /// thread, since it touches nothing on the deck.
    /// </summary>
    static DecodedSong Decode(AudioEngine engine, string path)
    {
        var (decoded, title, artist) = AudioDecoder.Decode(path);
        var pcm = decoded.Pinned();

        // Media Foundation decodes but brings no tags, and SDL_mixer can't read .m4a tags, so ask Windows.
        if (string.IsNullOrEmpty(title) && OperatingSystem.IsWindows())
        {
            var tags = WindowsTags.Read(path);
            (title, artist) = (tags.Title, tags.Artist);
        }

        var waveform = Waveform.From(pcm);
        var detail = DetailWaveform.From(pcm);
        return new DecodedSong(
            path,
            pcm,
            pcm.Play(engine.Mixer, 0, pcm.Frames),
            string.IsNullOrEmpty(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title,
            artist ?? "",
            pcm.Duration,
            waveform,
            detail
        );
    }

    /// <summary>
    /// Puts a decoded song on the deck, paused at the start with the cue point there.
    /// </summary>
    void Apply(DecodedSong song)
    {
        _rolls.Clear();
        Effects.Silence();
        _slipCauses = SlipCause.None;
        KeyShift = 0;
        (_platterMode, _touched) = ((int)PlatterMode.Off, false);
        _track.Stop(0);
        _track.SetAudio(song.Audio);
        _audio.Dispose();
        _loopAudio.Dispose();
        _loopAudio = default;
        _audio = song.Audio;
        _pcm = song.Pcm;

        FilePath = song.Path;
        Title = song.Title;
        Artist = song.Artist;
        Duration = song.Duration;
        Waveform = song.Waveform;
        Detail = song.Detail;
        Analysis = null;

        // Started then paused, so the track has its input and play/pause is just resume/pause from here on.
        _track.Play(0);
        _track.Pause();
        _cueFrames = 0;
        _previewing = false;
        Array.Clear(_hotcues);
        _memoryCues.Clear();
        (_manualLoop, _loopIn, _lastLoop) = (false, null, null);
    }

    public void Play()
    {
        // Play during a cue preview keeps it playing once Cue is let go, as on a CDJ.
        _previewing = false;
        if (IsSlipping)
        {
            AdvanceSlip();
            _slipMotor = IsLoaded;
        }
        if (IsPlatterActive)
        {
            // Under a scratch, it plays on once the record's let go.
            _motor = true;
            return;
        }
        if (!IsLoaded || IsPlaying)
            return;

        // Paused resumes where it was. A track that ran to the end has stopped rather than paused (and Playing() is
        // false either way, so it can't tell them apart), and playing it again starts from the top.
        if (_track.Paused())
            _track.Resume();
        else
            _track.Play(0);
    }

    public void Pause()
    {
        _previewing = false;
        if (IsSlipping)
        {
            AdvanceSlip();
            _slipMotor = false;
        }
        if (IsPlatterActive)
        {
            _motor = false;
            return;
        }
        if (IsPlaying)
        {
            _track.Pause();
            // An echo or reverb ringing on would otherwise wait, and carry on when it plays again.
            Effects.Silence();
        }
    }

    public void TogglePlay()
    {
        if (IsPlaying && !_previewing)
            Pause();
        else
            Play();
    }

    /// <summary>
    /// The deck's cue point: where Cue returns to, and where a cue preview starts. 0:00 when a song loads.
    /// </summary>
    public TimeSpan CuePoint => IsLoaded ? TimeSpan.FromMilliseconds(_track.FramesToMS(_cueFrames)) : TimeSpan.Zero;

    /// <summary>
    /// Cue is held down on a paused deck, so it's playing from the cue point until <see cref="CueUp"/>.
    /// </summary>
    public bool IsCuePreviewing => _previewing;

    /// <summary>
    /// The Cue button went down. While playing, it jumps back to the cue point and pauses. While paused, it sets the
    /// cue point where the deck is and plays from there until <see cref="CueUp"/>, so you can hear where you've
    /// cued; pressing Play meanwhile keeps it playing.
    /// </summary>
    public void CueDown()
    {
        if (!IsLoaded || _previewing)
            return;
        // Cueing goes where it's told, not back to where a slip would have been.
        _slipCauses = SlipCause.None;
        ReleasePlatter();

        // Cueing works on the whole song, so it leaves a loop first.
        LeaveLoop();

        if (IsPlaying)
        {
            PauseAt(_cueFrames);
            return;
        }

        // A song that played to the end has stopped and has no position to cue from, so it goes back to the start.
        if (!_track.Paused())
            PauseAt(0);

        // Quantised, the cue point goes on the nearest beat.
        var frames = Math.Max(0, _track.GetPlaybackPosition());
        if (QuantizeGrid is { } grid && _pcm is { } pcm)
        {
            frames = Math.Clamp(ToFrames(TimeSpan.FromSeconds(Quantizing.Snap(grid, ToTime(frames).TotalSeconds))), 0, pcm.Frames - 1);
            _track.SetPlaybackPosition(frames);
        }
        _cueFrames = frames;
        _track.Resume();
        _previewing = true;
    }

    /// <summary>
    /// The Cue button came up: a preview ends back at the cue point, paused. Does nothing if Play took over.
    /// </summary>
    public void CueUp()
    {
        if (!_previewing)
            return;
        _previewing = false;
        PauseAt(_cueFrames);
    }

    /// <summary>
    /// How many hotcues a deck has.
    /// </summary>
    public const int HOTCUE_COUNT = 8;

    readonly TimeSpan?[] _hotcues = new TimeSpan?[HOTCUE_COUNT];

    /// <summary>
    /// The song's hotcues: places to jump straight to, each set or not. Cleared when another song loads.
    /// </summary>
    public IReadOnlyList<TimeSpan?> Hotcues => _hotcues;

    /// <summary>
    /// A hotcue was set or deleted by <see cref="HotcueDown"/> or <see cref="DeleteHotcue"/>, so whoever keeps them
    /// for the song can save them. Not raised by <see cref="SetHotcues"/>.
    /// </summary>
    public event Action<Deck>? HotcuesChanged;

    /// <summary>
    /// Puts back the hotcues saved for the loaded song; any past its end, or beyond <see cref="HOTCUE_COUNT"/>, are
    /// left out.
    /// </summary>
    public void SetHotcues(IReadOnlyList<TimeSpan?> hotcues)
    {
        for (var i = 0; i < HOTCUE_COUNT; i++)
            _hotcues[i] = i < hotcues.Count && hotcues[i] is { } at && IsLoaded && at >= TimeSpan.Zero && at <= Duration ? at : null;
    }

    readonly List<TimeSpan> _memoryCues = [];

    /// <summary>
    /// The song's memory cues, in order through it: places marked to come back to, as Rekordbox has them. The first is
    /// where the cue point starts. Cleared when another song loads.
    /// </summary>
    public IReadOnlyList<TimeSpan> MemoryCues => _memoryCues;

    /// <summary>
    /// Puts back the memory cues saved for the loaded song (any past its end are left out), the first as the cue
    /// point; and if the deck's paused at the very start, as a song is once it's loaded, it goes there, ready to play.
    /// </summary>
    public void SetMemoryCues(IReadOnlyList<TimeSpan> cues)
    {
        _memoryCues.Clear();
        if (!IsLoaded || _pcm is not { } pcm)
            return;
        _memoryCues.AddRange(cues.Where(at => at >= TimeSpan.Zero && at <= Duration).Distinct().Order());
        if (_memoryCues.Count == 0)
            return;
        var waitingAtStart = !IsPlaying && !_previewing && !IsLooping && !IsPlatterActive && _track.GetPlaybackPosition() <= 0;
        _cueFrames = Math.Clamp(ToFrames(_memoryCues[0]), 0, pcm.Frames - 1);
        if (waitingAtStart)
            PauseAt(_cueFrames);
    }

    /// <summary>
    /// Whether hotcues and the cue point go on the nearest beat as they're set, and a hotcue jumped to while playing
    /// lands at the same point in the beat as the music was, so it stays in time. Only for a song with a beat grid.
    /// </summary>
    public bool Quantize { get; set; } = true;

    BeatGrid? QuantizeGrid => Quantize ? _analysis?.Grid : null;

    /// <summary>
    /// Hotcue <paramref name="index"/>'s pad was pressed: an empty one is set where the deck is (on the nearest beat,
    /// with <see cref="Quantize"/>); a set one jumps there, playing on if it was playing, or staying paused if it was
    /// paused. With <see cref="Slip"/> on, a jump while playing lasts as long as the pad's held
    /// (<see cref="HotcueUp"/>).
    /// </summary>
    public void HotcueDown(int index)
    {
        if (!IsLoaded || index < 0 || index >= HOTCUE_COUNT)
            return;

        if (_hotcues[index] is { } at)
        {
            if (IsPlaying && QuantizeGrid is { } grid)
                at = TimeSpan.FromSeconds(Quantizing.InPhase(grid, ToTime(SongFrame).TotalSeconds, at.TotalSeconds));
            if (BeginSlip(SlipCause.Hotcue))
                _slipHotcue = index;
            Seek(at);
            return;
        }
        _hotcues[index] = QuantizeGrid is { } snapTo
            ? TimeSpan.FromSeconds(Math.Clamp(Quantizing.Snap(snapTo, Position.TotalSeconds), 0, Duration.TotalSeconds))
            : Position;
        HotcuesChanged?.Invoke(this);
    }

    /// <summary>
    /// Jumps <paramref name="beats"/> beats on through the song (back, if negative), at the same point in the beat, so
    /// a jump while playing stays in time. A loop goes with it, the same length. Needs a beat grid, and returns false,
    /// doing nothing, without one, during a roll, or for a jump past either end of the song.
    /// </summary>
    public bool BeatJump(double beats)
    {
        if (!IsLoaded || IsRolling || _analysis?.Grid is not { } grid)
            return false;

        // From where the track is, rather than what's heard (key lock holds that back a little), so jumps don't drift.
        var target = Quantizing.Jump(grid, ToTime(SongFrame).TotalSeconds, beats);
        if (target < 0 || target >= Duration.TotalSeconds)
            return false;
        if (IsLooping && _manualLoop && _pcm is { } pcm)
        {
            // A loop set by hand moves by as much as the music does.
            var by = ToFrames(TimeSpan.FromSeconds(target)) - SongFrame;
            if (_loopStart + by < 0 || _loopEnd + by > pcm.Frames)
                return false;
            PlaceLoopFrames(_loopStart + by, _loopEnd + by, SongFrame + by, manual: true, _loopStartBeat + beats);
        }
        else if (IsLooping)
            PlaceLoop(_loopStartBeat + beats, ToFrames(TimeSpan.FromSeconds(target)));
        else
            Seek(TimeSpan.FromSeconds(target));
        return true;
    }

    /// <summary>
    /// Hotcue <paramref name="index"/>'s pad was let go: with <see cref="Slip"/> on, back to where the song would
    /// have got to had it not jumped. Otherwise nothing.
    /// </summary>
    public void HotcueUp(int index)
    {
        if (_slipCauses.HasFlag(SlipCause.Hotcue) && _slipHotcue == index)
            EndSlip(SlipCause.Hotcue);
    }

    public void DeleteHotcue(int index)
    {
        if (index < 0 || index >= HOTCUE_COUNT || _hotcues[index] is null)
            return;
        _hotcues[index] = null;
        HotcuesChanged?.Invoke(this);
    }

    /// <summary>
    /// Jumps to <paramref name="position"/> in the song, which keeps playing or stays paused. A cue preview carries on
    /// from the new place. The cue point doesn't move.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded)
            return;

        // A jump takes the sound back from a scratch or spin first.
        ReleasePlatter();
        var clamped = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));
        var frames = ToFrames(clamped);
        if (IsRolling)
        {
            EndRollHere();
            if (IsLooping)
                LeaveLoop();
        }

        // Inside the loop it stays looping; anywhere else, it leaves the loop to get there.
        if (IsLooping)
        {
            if (frames >= _loopStart && frames < _loopEnd)
            {
                _track.SetPlaybackPosition(frames - _loopStart);
                return;
            }
            LeaveLoop();
        }

        // A song that played to the end has stopped and needs restarting (paused) before it has a position to set.
        if (!_track.Playing() && !_track.Paused())
            PauseAt(frames);
        else
            _track.SetPlaybackPosition(frames);
    }

    /// <summary>
    /// Pauses at <paramref name="frames"/> into the song, from any state, including stopped at the end.
    /// </summary>
    void PauseAt(long frames)
    {
        if (!_track.Playing() && !_track.Paused())
            _track.Play(0);
        _track.Pause();
        _track.SetPlaybackPosition(frames);
    }

    /// <summary>
    /// The shortest and longest loops, in beats.
    /// </summary>
    public const double MIN_LOOP_BEATS = 1.0 / 16;

    public const double MAX_LOOP_BEATS = 32;

    public bool IsLooping => !_loopAudio.IsNull();

    /// <summary>
    /// Where the loop starts and ends, while looping.
    /// </summary>
    public (TimeSpan Start, TimeSpan End)? Loop => IsLooping ? (ToTime(_loopStart), ToTime(_loopEnd)) : null;

    /// <summary>
    /// How long a loop is, in beats, whether looping or not: a power of two from <see cref="MIN_LOOP_BEATS"/> to
    /// <see cref="MAX_LOOP_BEATS"/>. Changing it while looping changes the loop, keeping its start.
    /// </summary>
    public double LoopBeats
    {
        get => _loopBeats;
        set
        {
            var beats = Math.Pow(2, Math.Round(Math.Log2(Math.Clamp(value, MIN_LOOP_BEATS, MAX_LOOP_BEATS))));
            if (beats == _loopBeats)
                return;
            _loopBeats = beats;
            if (IsLooping)
                PlaceLoop(_loopStartBeat);
        }
    }

    /// <summary>
    /// Whether a loop can be started: there's a song with a beat grid to fit it to.
    /// </summary>
    public bool CanLoop => IsLoaded && _pcm is not null && _analysis is not null;

    /// <summary>
    /// Starts a loop of <see cref="LoopBeats"/> beats from the last beat played (or, for a loop shorter than a beat,
    /// the last fraction of one), so the music carries straight on into it; or leaves the loop if looping.
    /// </summary>
    public void ToggleLoop()
    {
        if (IsLooping)
        {
            ExitLoop();
            return;
        }
        if (!CanLoop || _analysis!.Grid is not { } grid)
            return;

        var beat = grid.BeatNumberAt(Position.TotalSeconds);
        var startBeat = _loopBeats >= 1 ? Math.Floor(beat) : Math.Floor(beat / _loopBeats) * _loopBeats;
        PlaceLoop(startBeat);
    }

    /// <summary>
    /// Halves the loop: one set by hand keeps its start and loses its second half; otherwise it's
    /// <see cref="LoopBeats"/> halved, looping or not.
    /// </summary>
    public void HalveLoop()
    {
        if (IsLooping && _manualLoop)
            ResizeManualLoop(0.5);
        else
            LoopBeats = _loopBeats / 2;
    }

    /// <summary>
    /// Doubles the loop: one set by hand keeps its start and runs on twice as far, as far as the end of the song;
    /// otherwise it's <see cref="LoopBeats"/> doubled, looping or not.
    /// </summary>
    public void DoubleLoop()
    {
        if (IsLooping && _manualLoop)
            ResizeManualLoop(2);
        else
            LoopBeats = _loopBeats * 2;
    }

    /// <summary>
    /// The shortest loop that can be set by hand.
    /// </summary>
    static readonly TimeSpan MinManualLoop = TimeSpan.FromMilliseconds(10);

    void ResizeManualLoop(double factor)
    {
        if (_pcm is not { } pcm)
            return;
        var length = (long)Math.Round((_loopEnd - _loopStart) * factor);
        if (length < ToFrames(MinManualLoop) || _loopStart + length > pcm.Frames)
            return;
        PlaceLoopFrames(_loopStart, _loopStart + length, SongFrame, manual: true, _loopStartBeat);
    }

    /// <summary>
    /// Where loop in was marked, for <see cref="LoopOut"/> to loop back to; null if it hasn't been since the song
    /// loaded.
    /// </summary>
    public TimeSpan? LoopInPoint => _loopIn;

    /// <summary>
    /// Whether the loop going on was set by hand, with <see cref="LoopIn"/> and <see cref="LoopOut"/>.
    /// </summary>
    public bool IsManualLoop => IsLooping && _manualLoop;

    /// <summary>
    /// How long the loop going on is, in beats; null if there's no loop, or no beat grid to count them by.
    /// </summary>
    public double? LoopLengthBeats =>
        IsLooping && _analysis?.Grid is { } grid
            ? grid.BeatNumberAt(ToTime(_loopEnd).TotalSeconds) - grid.BeatNumberAt(ToTime(_loopStart).TotalSeconds)
            : null;

    /// <summary>
    /// Whether there's a loop to go back to with <see cref="Reloop"/>.
    /// </summary>
    public bool CanReloop => IsLooping || _lastLoop is not null;

    /// <summary>
    /// Marks where a loop set by hand starts: where the deck is (on the nearest beat, with <see cref="Quantize"/>).
    /// Nothing loops until <see cref="LoopOut"/>. Works without a beat grid.
    /// </summary>
    public void LoopIn()
    {
        if (!IsLoaded)
            return;
        var at = Position.TotalSeconds;
        if (QuantizeGrid is { } grid)
            at = Quantizing.Snap(grid, at);
        _loopIn = TimeSpan.FromSeconds(Math.Clamp(at, 0, Duration.TotalSeconds));
    }

    /// <summary>
    /// Loops from loop in to where the deck is now (on the nearest beat, with <see cref="Quantize"/>), going straight
    /// back to loop in. While a loop set by hand goes, its end comes to here instead. Returns false, doing nothing,
    /// with no loop in before here, or during a roll.
    /// </summary>
    public bool LoopOut()
    {
        if (!IsLoaded || IsRolling || _pcm is not { } pcm)
            return false;

        long start;
        if (IsLooping && _manualLoop)
            start = _loopStart;
        else if (_loopIn is { } loopIn)
            start = ToFrames(loopIn);
        else
            return false;

        var at = Position.TotalSeconds;
        var grid = QuantizeGrid;
        if (grid is not null)
        {
            at = Quantizing.Snap(grid, at);
            // A beat at least, rather than nothing, when the nearest beat is loop in's own.
            if (ToFrames(TimeSpan.FromSeconds(at)) <= start)
                at = Quantizing.Jump(grid, ToTime(start).TotalSeconds, 1);
        }
        var end = Math.Min(ToFrames(TimeSpan.FromSeconds(at)), pcm.Frames);
        if (end - start < ToFrames(MinManualLoop))
            return false;

        var startBeat = _analysis?.Grid is { } beats ? beats.BeatNumberAt(ToTime(start).TotalSeconds) : 0;
        PlaceLoopFrames(start, end, SongFrame, manual: true, startBeat);
        return true;
    }

    /// <summary>
    /// Back to the start of the loop going on, or else the last loop there was, looping it again from its start.
    /// Playing or paused stays as it was.
    /// </summary>
    public void Reloop()
    {
        if (IsRolling)
            return;
        if (IsLooping)
        {
            _track.SetPlaybackPosition(0);
            return;
        }
        if (_lastLoop is { } last)
            PlaceLoopFrames(last.Start, last.End, last.Start, last.Manual, last.StartBeat);
    }

    /// <summary>
    /// Puts the loop from <paramref name="startBeat"/> for <see cref="LoopBeats"/> on the track, carrying on from the
    /// same place in the music: the same moment if it's inside the loop, or as far into the loop as the music is past
    /// its start. Playing or paused stays as it was.
    /// </summary>
    void PlaceLoop(double startBeat, long? from = null)
    {
        if (_pcm is not { } pcm || _analysis?.Grid is not { } grid)
            return;

        var start = Math.Clamp(ToFrames(TimeSpan.FromSeconds(grid.TimeOfBeat(startBeat))), 0, pcm.Frames - 1);
        var end = Math.Clamp(ToFrames(TimeSpan.FromSeconds(grid.TimeOfBeat(startBeat + _loopBeats))), start + 1, pcm.Frames);
        PlaceLoopFrames(start, end, from, manual: false, startBeat);
    }

    /// <summary>
    /// Puts the loop from frame <paramref name="start"/> to <paramref name="end"/> on the track, as
    /// <see cref="PlaceLoop"/> does, set by hand or by beats, and remembers it for <see cref="Reloop"/>.
    /// </summary>
    void PlaceLoopFrames(long start, long end, long? from, bool manual, double startBeat)
    {
        if (_pcm is not { } pcm)
            return;

        _engine.Mixer.Lock();
        try
        {
            var length = end - start;
            var into = (((from ?? SongFrame) - start) % length + length) % length;
            var playing = IsPlaying;
            // A loop that's not a roll's (which keeps its own place), started under slip, lasts until it's left.
            if (!IsLooping && !IsRolling)
                BeginSlip(SlipCause.Loop);
            var loop = pcm.Play(_engine.Mixer, start, length);

            _track.SetAudio(loop);
            _track.Play(0);
            _track.SetLoops(-1);
            _track.SetPlaybackPosition(into);
            if (!playing)
                _track.Pause();

            _loopAudio.Dispose();
            (_loopAudio, _loopStart, _loopEnd, _loopStartBeat, _manualLoop) = (loop, start, end, startBeat, manual);
            // A roll's loop is only borrowed while its pad's held, so it's not one to go back to.
            if (!IsRolling)
                _lastLoop = (start, end, manual, startBeat);
        }
        finally
        {
            _engine.Mixer.Unlock();
        }
    }

    /// <summary>
    /// Leaves the loop, carrying on through the song from wherever it was in the loop; or, with <see cref="Slip"/>
    /// on when it started, from where the song would have got to without it. Playing or paused stays as it was. Does
    /// nothing if not looping.
    /// </summary>
    public void ExitLoop()
    {
        // Back from the slip first, which leaves the loop; or, with something else still holding it, from in the loop.
        if (IsLooping && _slipCauses.HasFlag(SlipCause.Loop))
            EndSlip(SlipCause.Loop);
        LeaveLoop();
    }

    /// <summary>
    /// Leaves the loop from wherever it was in it, as on the way to somewhere else: a slip the loop started is
    /// forgotten (another's, such as a held hotcue's, carries on).
    /// </summary>
    void LeaveLoop()
    {
        if (IsRolling)
            EndRollHere();
        if (!IsLooping)
            return;
        _slipCauses &= ~SlipCause.Loop;

        _engine.Mixer.Lock();
        try
        {
            var at = SongFrame;
            var playing = IsPlaying;
            _track.SetAudio(_audio);
            _track.Play(0);
            _track.SetPlaybackPosition(at);
            if (!playing)
                _track.Pause();

            _loopAudio.Dispose();
            _loopAudio = default;
        }
        finally
        {
            _engine.Mixer.Unlock();
        }
    }

    // The roll sizes held, latest last: the one rolling is the last, and letting go of it goes back to the one before.
    readonly List<double> _rolls = [];

    // Where the music would be, in seconds, had it not been rolling, and when that was last worked out.
    double _rollShadow;
    long _rollClock;

    // The loop's size before the roll, for the loop button, which the roll borrows.
    double _loopBeatsBeforeRoll;

    /// <summary>
    /// The size of the loop roll going on, in beats; null if none.
    /// </summary>
    public double? RollBeats => _rolls.Count > 0 ? _rolls[^1] : null;

    public bool IsRolling => _rolls.Count > 0;

    /// <summary>
    /// A loop roll of <paramref name="beats"/> held down: the music loops over that much, from the beat (or fraction
    /// of one) just played, while the song carries on underneath unheard. Letting go (<see cref="RollUp"/>) drops back
    /// in where the song has got to, so the mix stays on the beat. Another size held meanwhile takes over, from the
    /// same place, and letting go of it goes back to the one before. Only while playing; a roll over a loop ends the
    /// loop.
    /// </summary>
    public void RollDown(double beats)
    {
        if (!CanLoop || !IsPlaying || IsPlatterActive || _analysis?.Grid is null)
            return;
        beats = Math.Clamp(beats, MIN_LOOP_BEATS, MAX_LOOP_BEATS);
        if (_rolls.Count == 0)
        {
            var song = ToTime(SongFrame).TotalSeconds;
            if (IsLooping)
                LeaveLoop();
            (_rollShadow, _rollClock, _loopBeatsBeforeRoll) = (song, Stopwatch.GetTimestamp(), _loopBeats);
        }
        _rolls.Remove(beats);
        _rolls.Add(beats);
        PlaceRoll(beats);
    }

    /// <summary>
    /// A loop roll of <paramref name="beats"/> let go: back to the size held before it, if any, or else on through the
    /// song from where it would have been.
    /// </summary>
    public void RollUp(double beats)
    {
        beats = Math.Clamp(beats, MIN_LOOP_BEATS, MAX_LOOP_BEATS);
        var wasRolling = RollBeats == beats;
        if (!_rolls.Remove(beats))
            return;
        if (_rolls.Count > 0)
        {
            if (wasRolling)
                PlaceRoll(_rolls[^1]);
            return;
        }

        AdvanceShadow();
        _loopBeats = _loopBeatsBeforeRoll;
        if (!IsLooping || _pcm is null)
            return;
        _engine.Mixer.Lock();
        try
        {
            var playing = IsPlaying;
            _track.SetAudio(_audio);
            _track.Play(0);
            _track.SetPlaybackPosition(Math.Clamp(ToFrames(TimeSpan.FromSeconds(_rollShadow)), 0, _pcm.Frames - 1));
            if (!playing)
                _track.Pause();
            _loopAudio.Dispose();
            _loopAudio = default;
        }
        finally
        {
            _engine.Mixer.Unlock();
        }
    }

    /// <summary>
    /// The roll's loop, <paramref name="beats"/> long from the beat (or fraction) the song's reached, playing on from
    /// where the song is in it.
    /// </summary>
    void PlaceRoll(double beats)
    {
        AdvanceShadow();
        var beat = _analysis!.Grid.BeatNumberAt(_rollShadow);
        var startBeat = beats >= 1 ? Math.Floor(beat) : Math.Floor(beat / beats) * beats;
        _loopBeats = beats;
        PlaceLoop(startBeat, ToFrames(TimeSpan.FromSeconds(_rollShadow)));
    }

    /// <summary>
    /// Moves where the song would be on by the time since it was last worked out, at the speed it's playing.
    /// </summary>
    void AdvanceShadow() => _rollShadow += Elapsed(ref _rollClock, IsPlaying);

    /// <summary>
    /// How far, in seconds of the song, the deck's played since <paramref name="clock"/> if it's been
    /// <paramref name="moving"/>, at the speed it's going; and the clock moved on to now.
    /// </summary>
    double Elapsed(ref long clock, bool moving)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = moving ? Stopwatch.GetElapsedTime(clock, now).TotalSeconds * Volatile.Read(ref _speed) : 0;
        clock = now;
        return seconds;
    }

    /// <summary>
    /// What's going on that the deck will come back from, under slip.
    /// </summary>
    [Flags]
    enum SlipCause
    {
        None = 0,
        Platter = 1,
        Hotcue = 2,
        Loop = 4,
    }

    bool _slip;
    SlipCause _slipCauses;

    // Where the song would be, in seconds, had it not been scratched, jumped or looped; when that was last worked out;
    // whether it's playing on underneath; and the hotcue whose pad's held.
    double _slipShadow;
    long _slipClock;
    bool _slipMotor;
    int _slipHotcue;

    /// <summary>
    /// Slip mode: a scratch, a hotcue held down, or a loop only lasts as long as it's held or looping, while the song
    /// carries on underneath unheard; then the deck drops back in where the song would have got to, so the mix keeps
    /// going as if nothing had happened. Turning it off midway stays where the deck is.
    /// </summary>
    public bool Slip
    {
        get => _slip;
        set
        {
            _slip = value;
            if (!value)
                _slipCauses = SlipCause.None;
        }
    }

    /// <summary>
    /// Whether the deck's away from where the song's carrying on underneath, under slip.
    /// </summary>
    public bool IsSlipping => _slipCauses != SlipCause.None;

    /// <summary>
    /// Where the song's got to underneath while slipping, to show; null otherwise.
    /// </summary>
    public TimeSpan? SlipPosition
    {
        get
        {
            if (!IsSlipping)
                return null;
            var clock = _slipClock;
            return TimeSpan.FromSeconds(Math.Clamp(_slipShadow + Elapsed(ref clock, _slipMotor), 0, Duration.TotalSeconds));
        }
    }

    /// <summary>
    /// Starts a slip for <paramref name="cause"/>, from where the deck is, if slip's on and it's playing; one already
    /// going keeps its place. Returns whether there's a slip for it.
    /// </summary>
    bool BeginSlip(SlipCause cause)
    {
        if (!_slip || !IsLoaded || _pcm is null)
            return false;
        if (!IsSlipping)
        {
            if (!IsPlaying)
                return false;
            (_slipShadow, _slipClock, _slipMotor) = (ToTime(SongFrame).TotalSeconds, Stopwatch.GetTimestamp(), true);
        }
        _slipCauses |= cause;
        return true;
    }

    void AdvanceSlip() => _slipShadow += Elapsed(ref _slipClock, _slipMotor);

    /// <summary>
    /// <paramref name="cause"/> is over: once nothing else is holding the slip, back to where the song would have got
    /// to, playing or paused as it is underneath.
    /// </summary>
    void EndSlip(SlipCause cause)
    {
        if (!_slipCauses.HasFlag(cause))
            return;
        _slipCauses &= ~cause;
        if (IsSlipping || _pcm is not { } pcm)
            return;

        AdvanceSlip();
        var frames = Math.Clamp(ToFrames(TimeSpan.FromSeconds(_slipShadow)), 0, pcm.Frames - 1);
        if (IsPlatterActive)
            Volatile.Write(ref _platterMode, (int)PlatterMode.Off);
        LeaveLoop();
        _previewing = false;
        _engine.Mixer.Lock();
        try
        {
            if (!_track.Playing() && !_track.Paused())
                _track.Play(0);
            _track.SetPlaybackPosition(frames);
            if (_slipMotor)
                _track.Resume();
            else
                _track.Pause();
        }
        finally
        {
            _engine.Mixer.Unlock();
        }
    }

    /// <summary>
    /// Keeps a roll's place in the song, and ends it, staying where it is, if the deck stops playing.
    /// </summary>
    void UpdateRoll()
    {
        if (!IsRolling)
            return;
        AdvanceShadow();
        if (!IsPlaying)
            EndRollHere();
    }

    /// <summary>
    /// Forgets the roll, leaving the loop it was playing for whoever ends it.
    /// </summary>
    void EndRollHere()
    {
        _rolls.Clear();
        _loopBeats = _loopBeatsBeforeRoll;
    }

    public void Dispose()
    {
        // A load still decoding would hand back audio from a mixer that's about to go, so wait for it and free it.
        if (_pending is { } pending)
        {
            try
            {
                pending.Task.Wait();
                pending.Task.Result.Audio.Dispose();
            }
            catch (AggregateException)
            {
                // It failed; there's nothing to free.
            }
            _pending = null;
        }

        _engine.Remove(this);

        // The track first, so nothing is still reading the audio or measuring it when they're freed.
        _track.Dispose();
        _loopAudio.Dispose();
        _audio.Dispose();
        if (_self.IsAllocated)
            _self.Free();
    }
}
