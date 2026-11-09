using Brutality.Audio;

namespace Brutality.Music;

/// <summary>
/// Plays a song from the list to hear what it is without putting it on a deck: on a deck of its own with its fader
/// down and its cue on, so it's in the headphones and nowhere else, and with no analysis, so nothing syncs to it. Only
/// the headphones hear it, as cue is taken before the fader. UI thread only.
/// </summary>
sealed class SongPreview : IDisposable
{
    readonly Deck _deck;
    bool _started;

    /// <summary>
    /// The song being heard, or about to be; null when there isn't one.
    /// </summary>
    public Song? Song { get; private set; }

    /// <summary>
    /// A song couldn't be played: which, and why.
    /// </summary>
    public event Action<Song, string>? Failed;

    public SongPreview(AudioEngine engine)
    {
        _deck = engine.CreateDeck("Preview");
        _deck.Headphones = true;
        _deck.StartWithFaderDown();
        _deck.Loaded += _ =>
        {
            // Stopped while it was loading, it stays quiet.
            if (Song is null)
                return;
            _deck.Play();
            _started = true;
        };
        _deck.LoadFailed += (_, _, error) =>
        {
            if (Song is { } song)
                Failed?.Invoke(song, error.Message);
            Song = null;
        };
    }

    public bool IsPlaying => Song is not null && _deck.IsPlaying;

    bool IsOnDeck => Song is { } song && string.Equals(_deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How far through the song it is, from 0 to 1.
    /// </summary>
    public float Fraction => IsOnDeck && _deck.Duration > TimeSpan.Zero ? (float)(_deck.Position / _deck.Duration) : 0;

    /// <summary>
    /// Starts <paramref name="song"/> from the top, in place of whatever was being heard.
    /// </summary>
    public void Start(Song song)
    {
        _deck.Pause();
        _started = false;
        Song = song;
        _deck.BeginLoad(song.Path, song.Title);
    }

    public void Stop()
    {
        _deck.Pause();
        _started = false;
        Song = null;
    }

    public void Seek(float fraction)
    {
        if (IsOnDeck)
            _deck.Seek(_deck.Duration * Math.Clamp(fraction, 0, 1));
    }

    /// <summary>
    /// Takes in a load that's finished, and notices a song that's played out. Call once a frame.
    /// </summary>
    public void Update()
    {
        _deck.Update();
        if (_started && !_deck.IsPlaying)
            Stop();
    }

    public void Dispose() => _deck.Dispose();
}
