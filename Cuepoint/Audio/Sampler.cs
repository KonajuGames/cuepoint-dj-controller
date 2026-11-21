namespace Cuepoint.Audio;

/// <summary>
/// Eight one-shot samples, an air horn or a drop, say, played straight into the main mix from the start each time
/// one's pad is hit, whatever the decks are doing: past the crossfader, under the master volume and limiter, and in a
/// recording. Each is a deck of its own, out of sight, so a song of any kind a deck plays can be one. Call
/// <see cref="Update"/> once a frame, to take in a load that's finished.
/// </summary>
sealed class Sampler : IDisposable
{
    public const int SLOTS = 8;

    readonly Deck[] _slots;
    readonly string?[] _paths = new string?[SLOTS];
    readonly string?[] _names = new string?[SLOTS];

    /// <summary>
    /// A sample couldn't be loaded: which slot, from 0, and why.
    /// </summary>
    public event Action<int, string>? LoadFailed;

    /// <summary>
    /// Which samples there are changed, by a load or a clear, to keep.
    /// </summary>
    public event Action? Changed;

    public Sampler(AudioEngine engine)
    {
        _slots = Enumerable.Range(0, SLOTS).Select(i => engine.CreateDeck($"Sample {i + 1}")).ToArray();
        for (var i = 0; i < SLOTS; i++)
        {
            var slot = i;
            _slots[i].LoadFailed += (_, _, error) =>
            {
                (_paths[slot], _names[slot]) = (null, null);
                LoadFailed?.Invoke(slot, error.Message);
                Changed?.Invoke();
            };
        }
    }

    /// <summary>
    /// The file in each slot, or null for an empty one, to keep between runs.
    /// </summary>
    public IReadOnlyList<string?> Paths => _paths;

    /// <summary>
    /// What slot <paramref name="slot"/>'s sample is called, for its pad; null if it's empty.
    /// </summary>
    public string? NameOf(int slot) => InRange(slot) ? _names[slot] : null;

    /// <summary>
    /// Whether slot <paramref name="slot"/> has a sample ready to play.
    /// </summary>
    public bool IsReady(int slot) => InRange(slot) && _paths[slot] is not null && _slots[slot].IsLoaded && !_slots[slot].IsLoading;

    public bool IsPlaying(int slot) => InRange(slot) && _paths[slot] is not null && _slots[slot].IsPlaying;

    /// <summary>
    /// Puts the file at <paramref name="path"/> in slot <paramref name="slot"/>, called <paramref name="name"/>,
    /// decoding it in the background.
    /// </summary>
    public void Load(int slot, string path, string name)
    {
        if (!InRange(slot))
            return;
        _slots[slot].Pause();
        (_paths[slot], _names[slot]) = (path, name);
        _slots[slot].BeginLoad(path, name);
        Changed?.Invoke();
    }

    /// <summary>
    /// Empties slot <paramref name="slot"/>, stopping its sample.
    /// </summary>
    public void Clear(int slot)
    {
        if (!InRange(slot) || _paths[slot] is null)
            return;
        _slots[slot].Pause();
        (_paths[slot], _names[slot]) = (null, null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Plays slot <paramref name="slot"/>'s sample from the start, over itself if it's playing already.
    /// </summary>
    public void Trigger(int slot)
    {
        if (!IsReady(slot))
            return;
        var deck = _slots[slot];
        deck.Seek(TimeSpan.Zero);
        deck.Play();
    }

    /// <summary>
    /// Stops every sample playing.
    /// </summary>
    public void StopAll()
    {
        foreach (var deck in _slots)
            deck.Pause();
    }

    public void Update()
    {
        foreach (var deck in _slots)
            deck.Update();
    }

    static bool InRange(int slot) => slot >= 0 && slot < SLOTS;

    public void Dispose()
    {
        foreach (var deck in _slots)
            deck.Dispose();
    }
}
