using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace Brutality.Audio;

/// <summary>
/// One player: a song loaded onto an SDL_mixer track, with play/pause and a position. Get one from
/// <see cref="AudioEngine.CreateDeck"/>.
/// </summary>
sealed class Deck : IDisposable
{
    // SDL_mixer's metadata property names. The bindings only mention them in doc comments, so they're spelled out.
    const string TITLE_PROPERTY = "SDL_mixer.metadata.title";
    const string ARTIST_PROPERTY = "SDL_mixer.metadata.artist";

    public string Name { get; }

    public string? FilePath { get; private set; }

    /// <summary>
    /// From the file's tags, falling back to the file name.
    /// </summary>
    public string Title { get; private set; } = "";

    public string Artist { get; private set; } = "";

    public TimeSpan Duration { get; private set; }

    public bool IsLoaded => !_audio.IsNull();

    readonly AudioEngine _engine;
    readonly MixTrack _track;
    MixAudio _audio;

    internal Deck(AudioEngine engine, string name)
    {
        Name = name;
        _track = engine.Mixer.CreateTrack();
        _engine = engine;
    }

    /// <summary>
    /// Where playback is, from SDL_mixer's audio thread, so it moves on between calls while playing.
    /// </summary>
    public TimeSpan Position =>
        IsLoaded ? TimeSpan.FromMilliseconds(_track.FramesToMS(Math.Max(0, _track.GetPlaybackPosition()))) : TimeSpan.Zero;

    public bool IsPlaying => IsLoaded && _track.Playing() && !_track.Paused();

    /// <summary>
    /// Decodes <paramref name="path"/> into memory and leaves the deck paused at the start. Decoding up front costs
    /// a moment and some memory, but makes seeking and cueing instant. Throws if neither SDL_mixer nor (on Windows)
    /// Media Foundation can decode the file; the deck keeps whatever it had.
    /// </summary>
    public void Load(string path)
    {
        var audio = Decode(path);

        _track.Stop(0);
        _track.SetAudio(audio);
        _audio.Dispose();
        _audio = audio;

        FilePath = path;
        var properties = audio.GetProperties();
        var title = Sdl.PInvoke.GetStringProperty(properties, TITLE_PROPERTY, "");
        var artist = Sdl.PInvoke.GetStringProperty(properties, ARTIST_PROPERTY, "");

        // Media Foundation decodes but brings no tags, and SDL_mixer can't read .m4a tags, so ask Windows.
        if (string.IsNullOrEmpty(title) && OperatingSystem.IsWindows())
            (title, artist) = WindowsTags.Read(path);

        Title = string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(path) : title;
        Artist = artist ?? "";
        Duration = TimeSpan.FromMilliseconds(audio.FramesToMS(audio.GetDuration()));

        // Started then paused, so the track has its input and play/pause is just resume/pause from here on.
        _track.Play(0);
        _track.Pause();
        _cueFrames = 0;
        _previewing = false;
    }

    /// <summary>
    /// SDL_mixer decodes MP3, WAV, FLAC and OGG itself. Anything it can't (AAC in .m4a, chiefly) goes to Windows'
    /// Media Foundation instead, when on Windows.
    /// </summary>
    MixAudio Decode(string path)
    {
        try
        {
            return _engine.Mixer.LoadAudio(path, true);
        }
        catch (SdlException sdlError) when (OperatingSystem.IsWindows() && File.Exists(path))
        {
            try
            {
                return MediaFoundationDecoder.Load(_engine.Mixer, path);
            }
            catch (Exception mfError)
            {
                throw new InvalidOperationException(
                    $"Couldn't decode {Path.GetFileName(path)}. SDL_mixer: {sdlError.Message} Media Foundation: {mfError.Message}",
                    mfError
                );
            }
        }
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

    long _cueFrames;
    bool _previewing;

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
        // The track first, so nothing is still reading the audio when it's freed.
        _track.Dispose();
        _audio.Dispose();
    }
}
