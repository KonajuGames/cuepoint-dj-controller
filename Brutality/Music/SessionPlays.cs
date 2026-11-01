using Brutality.Audio;

namespace Brutality.Music;

/// <summary>
/// Which songs have been played this session: played out on a deck for <see cref="PLAYED_SECONDS"/> in all, counted
/// in real time while the deck plays. Holding Cue to listen from the cue point doesn't count, and nor does time
/// before a song is loaded again: each load starts from nothing. Not saved; a new session starts afresh.
/// </summary>
sealed class SessionPlays
{
    public const double PLAYED_SECONDS = 10;

    readonly HashSet<string> _played = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Deck, (string Path, double Seconds)> _playing = [];

    /// <summary>
    /// A song has just reached <see cref="PLAYED_SECONDS"/>, by its path.
    /// </summary>
    public event Action<string>? Played;

    public int Count => _played.Count;

    public bool Contains(string path) => _played.Contains(path);

    /// <summary>
    /// Counts <paramref name="seconds"/> of the time since the last call for each deck playing. Call every frame.
    /// </summary>
    public void Update(IEnumerable<Deck> decks, double seconds)
    {
        foreach (var deck in decks)
        {
            if (deck.FilePath is not { } path)
            {
                _playing.Remove(deck);
                continue;
            }

            // Another song on the deck starts its count again.
            if (!_playing.TryGetValue(deck, out var state) || !string.Equals(state.Path, path, StringComparison.OrdinalIgnoreCase))
                state = (path, 0);

            if (deck.IsPlaying && !deck.IsCuePreviewing)
            {
                var before = state.Seconds;
                state.Seconds += Math.Max(0, seconds);
                if (before < PLAYED_SECONDS && state.Seconds >= PLAYED_SECONDS && _played.Add(path))
                    Played?.Invoke(path);
            }
            _playing[deck] = state;
        }
    }

    /// <summary>
    /// Forgets how long a deck has played, so its song counts from nothing: for when the same song is loaded again.
    /// </summary>
    public void Restart(Deck deck) => _playing.Remove(deck);

    /// <summary>
    /// Marks songs as not played. One playing now counts its ten seconds again from here. Returns how many had been
    /// played.
    /// </summary>
    public int Forget(IEnumerable<string> paths)
    {
        var forgotten = 0;
        foreach (var path in paths)
        {
            if (_played.Remove(path))
                forgotten++;
            foreach (var deck in _playing.Keys.ToList())
            {
                if (string.Equals(_playing[deck].Path, path, StringComparison.OrdinalIgnoreCase))
                    _playing[deck] = (path, 0);
            }
        }
        return forgotten;
    }
}
