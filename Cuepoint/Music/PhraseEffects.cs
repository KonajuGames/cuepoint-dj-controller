using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Music;

/// <summary>
/// An effect at a press that builds to the next phrase and finishes there, as a transition would, for a deck playing
/// a song with a beat grid. Phrases are <see cref="PHRASE_BARS"/> bars from the song's first downbeat, as Auto-Mix
/// counts them; one less than a bar away is too close to build to, so it goes to the one after.
/// </summary>
enum PhraseEffect
{
    /// <summary>
    /// Echoes from the press; on the phrase the song's sound is cut, so only the repeats ring on for
    /// <see cref="PhraseEffects.RING_BARS"/> bars, then the deck stops and its EQ and effect are as they were.
    /// </summary>
    EchoOut,

    /// <summary>
    /// The high-pass filter rising from the press to the phrase, and dropping back to where it was on it: a build.
    /// </summary>
    FilterSweep,
}

/// <summary>
/// A deck's <see cref="PhraseEffect"/> going, if one is: started with <see cref="Start"/>, moved on with
/// <see cref="Update"/> once a frame. Pressed again, or the deck stopped by hand, it stops, putting the deck's EQ,
/// filter and effect back as they were. UI thread only.
/// </summary>
sealed class PhraseEffects(Deck deck)
{
    public const int PHRASE_BARS = 8;
    public const int RING_BARS = 2;

    /// <summary>
    /// How far the filter rises by the phrase: most of the way to a thin high-pass.
    /// </summary>
    const float SWEEP_TOP = 0.85f;

    sealed record Going(PhraseEffect Kind, double From, double Phrase, double Until, Kept Was);

    sealed record Kept(float Low, float Mid, float High, float Filter, DeckEffect Kind, bool On, double EchoBeats);

    Going? _going;

    /// <summary>
    /// The effect going, if one is.
    /// </summary>
    public PhraseEffect? Kind => _going?.Kind;

    /// <summary>
    /// When the phrase it's building to comes, in seconds into the song, while one's going.
    /// </summary>
    public double? Phrase => _going?.Phrase;

    /// <summary>
    /// The start of the next phrase after <paramref name="seconds"/>, at least a bar on; null without a grid.
    /// </summary>
    public static double? NextPhrase(BeatGrid grid, double seconds)
    {
        var first = Math.Ceiling(grid.BeatNumberAt(0));
        while (!grid.IsBarStart(first))
            first++;
        const int beatsPerPhrase = PHRASE_BARS * BeatGrid.BEATS_PER_BAR;
        var now = grid.BeatNumberAt(seconds);
        var phrase = first + Math.Ceiling((now - first) / beatsPerPhrase) * beatsPerPhrase;
        if (phrase - now < BeatGrid.BEATS_PER_BAR)
            phrase += beatsPerPhrase;
        var time = grid.TimeOfBeat(phrase);
        return time < grid.Duration ? time : null;
    }

    /// <summary>
    /// Starts <paramref name="kind"/>, or stops it if it's the one going. Returns what to say: why it can't, or what
    /// it's doing.
    /// </summary>
    public string Start(PhraseEffect kind)
    {
        if (_going is { } going)
        {
            Stop();
            if (going.Kind == kind)
                return $"Deck {deck.Name}: {Name(kind)} stopped";
        }
        if (!deck.IsPlaying || deck.Analysis?.Grid is not { } grid)
            return $"Deck {deck.Name}: {Name(kind)} needs the deck playing a song with a beat grid";
        var now = deck.Position.TotalSeconds;
        if (NextPhrase(grid, now) is not { } phrase)
            return $"Deck {deck.Name}: no phrase left in the song for {Name(kind)}";
        var effects = deck.Effects;
        var was = new Kept(deck.Equalizer.Low, deck.Equalizer.Mid, deck.Equalizer.High, deck.Equalizer.Filter, effects.Kind, effects.On, effects.BeatsOf(DeckEffect.Echo));
        var until = kind == PhraseEffect.EchoOut ? grid.TimeOfBeat(grid.BeatNumberAt(phrase) + RING_BARS * BeatGrid.BEATS_PER_BAR) : phrase;
        _going = new Going(kind, now, phrase, until, was);
        if (kind == PhraseEffect.EchoOut)
        {
            effects.Kind = DeckEffect.Echo;
            effects.SetBeats(DeckEffect.Echo, 0.5);
            effects.On = true;
        }
        var bars = (grid.BeatNumberAt(phrase) - grid.BeatNumberAt(now)) / BeatGrid.BEATS_PER_BAR;
        return $"Deck {deck.Name}: {Name(kind)} to the next phrase, {bars:0} {(Math.Round(bars) == 1 ? "bar" : "bars")} away";
    }

    static string Name(PhraseEffect kind) => kind == PhraseEffect.EchoOut ? "echo out" : "filter sweep";

    /// <summary>
    /// Moves the effect on to where the deck is. Returns what to say once it's finished, or null.
    /// </summary>
    public string? Update()
    {
        if (_going is not { } going)
            return null;
        // Stopped by hand, or moved away (a hotcue, say): put back as it was.
        var now = deck.Position.TotalSeconds;
        if (!deck.IsPlaying || now < going.From - 0.05)
        {
            Stop();
            return null;
        }
        switch (going.Kind)
        {
            case PhraseEffect.FilterSweep when now < going.Phrase:
                var share = (float)((now - going.From) / (going.Phrase - going.From));
                deck.Equalizer.Filter = going.Was.Filter + (SWEEP_TOP - going.Was.Filter) * share * share;
                return null;
            case PhraseEffect.FilterSweep:
                Stop();
                return null;
            case PhraseEffect.EchoOut when now >= going.Phrase && now < going.Until:
                // Only the repeats ring on.
                deck.Equalizer.Low = deck.Equalizer.Mid = deck.Equalizer.High = -1;
                return null;
            case PhraseEffect.EchoOut when now >= going.Until:
                deck.Pause();
                Stop();
                return $"Deck {deck.Name}: echoed out";
            default:
                return null;
        }
    }

    /// <summary>
    /// Stops what's going, putting the deck's EQ, filter and effect back as they were.
    /// </summary>
    public void Stop()
    {
        if (_going is not { Was: var was })
            return;
        _going = null;
        (deck.Equalizer.Low, deck.Equalizer.Mid, deck.Equalizer.High, deck.Equalizer.Filter) = (was.Low, was.Mid, was.High, was.Filter);
        var effects = deck.Effects;
        effects.Kind = was.Kind;
        effects.SetBeats(DeckEffect.Echo, was.EchoBeats);
        effects.On = was.On;
    }
}
