namespace Cuepoint.Audio;

/// <summary>
/// Everything that colours the sound back to neutral at once, for when something's gone wrong and there's no time to
/// find what: every deck's EQ, filter and trim to the middle, its key back to the song's, its effect and vocal cut off and any loop
/// left, and the master effect off. What's playing keeps playing, and the faders and crossfader stay where they are,
/// as moving them could bring in a deck that's meant to be out. <see cref="Undo"/> puts the settings back.
/// </summary>
sealed class Panic
{
    sealed record DeckWas(Deck Deck, float Low, float Mid, float High, float Filter, float Trim, int KeyShift, bool Effect, bool VocalCut);

    readonly List<DeckWas> _decks;
    readonly DeckEffects _master;
    readonly bool _masterWas;

    Panic(List<DeckWas> decks, DeckEffects master, bool masterWas) => (_decks, _master, _masterWas) = (decks, master, masterWas);

    /// <summary>
    /// Puts <paramref name="decks"/> and <paramref name="master"/> back to neutral, and returns what they were, to
    /// undo it. Says how many things it changed in <see cref="Changed"/>.
    /// </summary>
    public static Panic Reset(IReadOnlyList<Deck> decks, DeckEffects master)
    {
        var was = decks
            .Select(deck => new DeckWas(deck, deck.Equalizer.Low, deck.Equalizer.Mid, deck.Equalizer.High, deck.Equalizer.Filter, deck.Trim, deck.KeyShift, deck.Effects.On, deck.VocalCut.On))
            .ToList();
        var panic = new Panic(was, master, master.On);
        foreach (var deck in decks)
        {
            panic.Changed += deck.Equalizer is { Low: 0, Mid: 0, High: 0, Filter: 0 } ? 0 : 1;
            panic.Changed += (deck.Trim != 0 ? 1 : 0) + (deck.KeyShift != 0 ? 1 : 0) + (deck.Effects.On ? 1 : 0) + (deck.IsLooping ? 1 : 0) + (deck.VocalCut.On ? 1 : 0);
            deck.Equalizer.Low = deck.Equalizer.Mid = deck.Equalizer.High = deck.Equalizer.Filter = 0;
            deck.Trim = 0;
            deck.KeyShift = 0;
            deck.Effects.On = false;
            deck.VocalCut.On = false;
            if (deck.IsLooping)
                deck.ExitLoop();
        }
        panic.Changed += master.On ? 1 : 0;
        master.On = false;
        return panic;
    }

    /// <summary>
    /// How many things it put back to neutral: a deck's EQ, its trim, key, effect and loop, and the master effect.
    /// </summary>
    public int Changed { get; private set; }

    /// <summary>
    /// Puts the settings back as they were, but not a loop it left.
    /// </summary>
    public void Undo()
    {
        foreach (var was in _decks)
        {
            (was.Deck.Equalizer.Low, was.Deck.Equalizer.Mid, was.Deck.Equalizer.High, was.Deck.Equalizer.Filter) = (was.Low, was.Mid, was.High, was.Filter);
            was.Deck.Trim = was.Trim;
            was.Deck.KeyShift = was.KeyShift;
            was.Deck.Effects.On = was.Effect;
            was.Deck.VocalCut.On = was.VocalCut;
        }
        _master.On = _masterWas;
    }
}
