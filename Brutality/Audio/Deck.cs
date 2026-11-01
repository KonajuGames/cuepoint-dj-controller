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
sealed class Deck : IDisposable
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
    /// What the deck plays at in the main mix: the song's automatic gain, then its fader through an audio taper,
    /// times what the crossfader lets through.
    /// </summary>
    public float Gain => AutoGainFactor * ChannelGain;

    float AutoGainFactor => _engine.AutoGain ? _analysis?.AutoGain ?? 1 : 1;

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
            deck.Equalizer.Process(samples, count, channels, spec->freq);
            deck.Meter.Measure(samples, count, channels);
            if (deck.Headphones)
                deck._engine.AddCue(samples, count);
            deck.ApplyChannelGain(samples, count, channels);
        }
        catch
        {
            // Nothing can be done about it on the audio thread, and an exception mustn't escape into SDL.
        }
    }

    /// <summary>
    /// Fader position to gain. Squared, so the fader's travel feels even to the ear: halfway is about -12 dB rather
    /// than the barely-quieter -6 dB a straight line gives, and the bottom of the travel isn't wasted on near-silence.
    /// </summary>
    public static float VolumeTaper(float position) => position * position;

    /// <summary>
    /// The automatic gain goes to SDL_mixer as the track's own gain, ahead of everything; the fader and crossfader
    /// are left for the track's callback, after the headphones have had the sound.
    /// </summary>
    internal void ApplyGain()
    {
        _track.SetGain(AutoGainFactor);
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
            _track.SetFrequencyRatio((float)TempoRatio);
        }
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
    /// How many semitones the tempo fader has moved the key, to the nearest: a semitone takes about 6% faster.
    /// </summary>
    public int KeyShift => (int)Math.Round(12 * Math.Log2(TempoRatio));

    /// <summary>
    /// The key coming out of the deck: the song's, moved by the tempo fader. Null until the song is analysed.
    /// </summary>
    public MusicalKey? Key => _analysis?.Key.Transposed(KeyShift);

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
    /// Where playback is, from SDL_mixer's audio thread, so it moves on between calls while playing.
    /// </summary>
    public TimeSpan Position => IsLoaded && _pcm is not null ? ToTime(SongFrame) : TimeSpan.Zero;

    /// <summary>
    /// Where playback is in the song, in frames: the track's position, plus the loop's start while it's playing the
    /// loop.
    /// </summary>
    long SongFrame => (IsLooping ? _loopStart : 0) + Math.Max(0, _track.GetPlaybackPosition());

    int SampleRate => _pcm?.SampleRate ?? 44100;

    TimeSpan ToTime(long frames) => TimeSpan.FromSeconds((double)frames / SampleRate);

    long ToFrames(TimeSpan time) => (long)Math.Round(time.TotalSeconds * SampleRate);

    public bool IsPlaying => IsLoaded && _track.Playing() && !_track.Paused();

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
    }

    public void Play()
    {
        // Play during a cue preview keeps it playing once Cue is let go, as on a CDJ.
        _previewing = false;
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
        if (IsPlaying)
            _track.Pause();
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

        // Cueing works on the whole song, so it leaves a loop first.
        ExitLoop();

        if (IsPlaying)
        {
            PauseAt(_cueFrames);
            return;
        }

        // A song that played to the end has stopped and has no position to cue from, so it goes back to the start.
        if (!_track.Paused())
            PauseAt(0);

        _cueFrames = Math.Max(0, _track.GetPlaybackPosition());
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

    /// <summary>
    /// Hotcue <paramref name="index"/>'s pad was pressed: an empty one is set where the deck is; a set one jumps
    /// there, playing on if it was playing, or staying paused if it was paused.
    /// </summary>
    public void HotcueDown(int index)
    {
        if (!IsLoaded || index < 0 || index >= HOTCUE_COUNT)
            return;

        if (_hotcues[index] is { } at)
        {
            Seek(at);
            return;
        }
        _hotcues[index] = Position;
        HotcuesChanged?.Invoke(this);
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

        var clamped = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));
        var frames = ToFrames(clamped);

        // Inside the loop it stays looping; anywhere else, it leaves the loop to get there.
        if (IsLooping)
        {
            if (frames >= _loopStart && frames < _loopEnd)
            {
                _track.SetPlaybackPosition(frames - _loopStart);
                return;
            }
            ExitLoop();
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

    public void HalveLoop() => LoopBeats = _loopBeats / 2;

    public void DoubleLoop() => LoopBeats = _loopBeats * 2;

    /// <summary>
    /// Puts the loop from <paramref name="startBeat"/> for <see cref="LoopBeats"/> on the track, carrying on from the
    /// same place in the music: the same moment if it's inside the loop, or as far into the loop as the music is past
    /// its start. Playing or paused stays as it was.
    /// </summary>
    void PlaceLoop(double startBeat)
    {
        if (_pcm is not { } pcm || _analysis?.Grid is not { } grid)
            return;

        var start = Math.Clamp(ToFrames(TimeSpan.FromSeconds(grid.TimeOfBeat(startBeat))), 0, pcm.Frames - 1);
        var end = Math.Clamp(ToFrames(TimeSpan.FromSeconds(grid.TimeOfBeat(startBeat + _loopBeats))), start + 1, pcm.Frames);

        _engine.Mixer.Lock();
        try
        {
            var length = end - start;
            var into = ((SongFrame - start) % length + length) % length;
            var playing = IsPlaying;
            var loop = pcm.Play(_engine.Mixer, start, length);

            _track.SetAudio(loop);
            _track.Play(0);
            _track.SetLoops(-1);
            _track.SetPlaybackPosition(into);
            if (!playing)
                _track.Pause();

            _loopAudio.Dispose();
            (_loopAudio, _loopStart, _loopEnd, _loopStartBeat) = (loop, start, end, startBeat);
        }
        finally
        {
            _engine.Mixer.Unlock();
        }
    }

    /// <summary>
    /// Leaves the loop, carrying on through the song from wherever it was in the loop. Playing or paused stays as it
    /// was. Does nothing if not looping.
    /// </summary>
    public void ExitLoop()
    {
        if (!IsLooping)
            return;

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
