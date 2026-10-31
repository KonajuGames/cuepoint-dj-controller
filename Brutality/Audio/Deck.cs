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
        if (IsPlaying)
            _track.Pause();
    }

    public void TogglePlay()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    public void Dispose()
    {
        // The track first, so nothing is still reading the audio when it's freed.
        _track.Dispose();
        _audio.Dispose();
    }
}
