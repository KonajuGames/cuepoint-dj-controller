using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;
using Brutality.Audio.Analysis;

namespace Brutality.Audio;

/// <summary>
/// One player: a song loaded onto an SDL_mixer track, with play/pause, cue and a position. Get one from
/// <see cref="AudioEngine.CreateDeck"/>. Everything except decoding happens on the thread that calls it, which
/// should be the UI thread; call <see cref="Update"/> once a frame to finish background loads.
/// </summary>
sealed class Deck : IDisposable
{
    /// <summary>
    /// A decoded song, ready to put on a deck: done off the UI thread, so loading doesn't stall the screen.
    /// </summary>
    sealed record DecodedSong(
        string Path,
        MixAudio Audio,
        string Title,
        string Artist,
        TimeSpan Duration,
        Waveform Waveform
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
    /// The background load in progress, if any. A newer load replaces it; the older one's result is thrown away.
    /// </summary>
    (string Path, string Name, Task<DecodedSong> Task)? _pending;

    long _cueFrames;
    bool _previewing;

    internal Deck(AudioEngine engine, string name, CrossfaderSide side)
    {
        Name = name;
        _engine = engine;
        _side = side;
        _track = engine.Mixer.CreateTrack();
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
    /// What the deck plays at: the song's automatic gain, then its fader through an audio taper, times what the
    /// crossfader lets through.
    /// </summary>
    public float Gain => (_analysis?.AutoGain ?? 1) * VolumeTaper(_volume) * _engine.CrossfaderGain(_side);

    /// <summary>
    /// Fader position to gain. Squared, so the fader's travel feels even to the ear: halfway is about -12 dB rather
    /// than the barely-quieter -6 dB a straight line gives, and the bottom of the travel isn't wasted on near-silence.
    /// </summary>
    public static float VolumeTaper(float position) => position * position;

    internal void ApplyGain() => _track.SetGain(Gain);

    /// <summary>
    /// The gain SDL_mixer has for the track, read back, for checking that <see cref="Gain"/> reached it.
    /// </summary>
    internal float AppliedGain => _track.GetGain();

    /// <summary>
    /// Where playback is, from SDL_mixer's audio thread, so it moves on between calls while playing.
    /// </summary>
    public TimeSpan Position =>
        IsLoaded ? TimeSpan.FromMilliseconds(_track.FramesToMS(Math.Max(0, _track.GetPlaybackPosition()))) : TimeSpan.Zero;

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
        var (pcm, title, artist) = AudioDecoder.Decode(path);

        // Media Foundation decodes but brings no tags, and SDL_mixer can't read .m4a tags, so ask Windows.
        if (string.IsNullOrEmpty(title) && OperatingSystem.IsWindows())
        {
            var tags = WindowsTags.Read(path);
            (title, artist) = (tags.Title, tags.Artist);
        }

        var waveform = Waveform.From(pcm);
        return new DecodedSong(
            path,
            pcm.ToMixAudio(engine.Mixer),
            string.IsNullOrEmpty(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title,
            artist ?? "",
            pcm.Duration,
            waveform
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
        _audio = song.Audio;

        FilePath = song.Path;
        Title = song.Title;
        Artist = song.Artist;
        Duration = song.Duration;
        Waveform = song.Waveform;
        Analysis = null;

        // Started then paused, so the track has its input and play/pause is just resume/pause from here on.
        _track.Play(0);
        _track.Pause();
        _cueFrames = 0;
        _previewing = false;
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
    /// Jumps to <paramref name="position"/> in the song, which keeps playing or stays paused. A cue preview carries on
    /// from the new place. The cue point doesn't move.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded)
            return;

        var clamped = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));
        var frames = _track.MSToFrames((long)clamped.TotalMilliseconds);

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

        // The track first, so nothing is still reading the audio when it's freed.
        _track.Dispose();
        _audio.Dispose();
    }
}
