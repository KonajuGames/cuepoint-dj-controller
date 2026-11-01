using Brutal.GlfwApi;
using Brutality.Audio;
using Brutality.UI;

namespace Brutality;

/// <summary>
/// One deck's keys, besides its hotcues (see <see cref="HotcueKeys"/>). Shift with the tempo keys moves the volume
/// fader instead.
/// </summary>
sealed record DeckKeys(
    GlfwKey Cue,
    GlfwKey Play,
    GlfwKey Sync,
    GlfwKey Loop,
    GlfwKey HalveLoop,
    GlfwKey DoubleLoop,
    GlfwKey TempoDown,
    GlfwKey TempoUp,
    GlfwKey Pfl,
    GlfwKey KeyLock,
    GlfwKey KillLow,
    GlfwKey KillMid,
    GlfwKey KillHigh,
    GlfwKey ResetEq,
    GlfwKey LowPass,
    GlfwKey HighPass
)
{
    public DeckKeyHints Hints =>
        new(Label(Cue), Label(Play), Label(Sync), Label(HalveLoop), Label(Loop), Label(DoubleLoop), Label(KeyLock), Label(Pfl));

    /// <summary>
    /// What's printed on <paramref name="key"/>.
    /// </summary>
    public static string Label(GlfwKey key) =>
        key switch
        {
            >= GlfwKey.Number0 and <= GlfwKey.Number9 => ((char)('0' + (key - GlfwKey.Number0))).ToString(),
            GlfwKey.Comma => ",",
            GlfwKey.Period => ".",
            GlfwKey.Semicolon => ";",
            GlfwKey.LeftBracket => "[",
            GlfwKey.RightBracket => "]",
            _ => key.ToString(),
        };
}

/// <summary>
/// The decks' and mixer's keyboard shortcuts. The left hand works deck A and the right hand deck B, each around its
/// own hotcue keys: cue, play, sync and loop along the bottom row; loop length, tempo and volume along the numbers;
/// PFL and key lock in the middle of the keyboard; and the EQ on the function keys, F1 to F8 for A and F9 to F12 for
/// B (whose filter shares F7 and F8). The crossfader slides on [ and ], and B centres it.
///
/// Toggles and one-shots act on the press. The faders move while their keys are held, a full sweep in a second, or a
/// quarter as fast with Ctrl, so call <see cref="Update"/> every frame.
/// </summary>
sealed class KeyboardControls
{
    public static readonly DeckKeys[] Keys =
    [
        new(
            Cue: GlfwKey.Z,
            Play: GlfwKey.X,
            Sync: GlfwKey.C,
            Loop: GlfwKey.V,
            HalveLoop: GlfwKey.Number3,
            DoubleLoop: GlfwKey.Number4,
            TempoDown: GlfwKey.Number5,
            TempoUp: GlfwKey.Number6,
            Pfl: GlfwKey.T,
            KeyLock: GlfwKey.G,
            KillLow: GlfwKey.F1,
            KillMid: GlfwKey.F2,
            KillHigh: GlfwKey.F3,
            ResetEq: GlfwKey.F4,
            LowPass: GlfwKey.F5,
            HighPass: GlfwKey.F6
        ),
        new(
            Cue: GlfwKey.N,
            Play: GlfwKey.M,
            Sync: GlfwKey.Comma,
            Loop: GlfwKey.Period,
            HalveLoop: GlfwKey.Number9,
            DoubleLoop: GlfwKey.Number0,
            TempoDown: GlfwKey.Number7,
            TempoUp: GlfwKey.Number8,
            Pfl: GlfwKey.Y,
            KeyLock: GlfwKey.H,
            KillLow: GlfwKey.F9,
            KillMid: GlfwKey.F10,
            KillHigh: GlfwKey.F11,
            ResetEq: GlfwKey.F12,
            LowPass: GlfwKey.F7,
            HighPass: GlfwKey.F8
        ),
    ];

    public const GlfwKey CROSSFADER_LEFT = GlfwKey.LeftBracket;
    public const GlfwKey CROSSFADER_RIGHT = GlfwKey.RightBracket;
    public const GlfwKey CROSSFADER_CENTRE = GlfwKey.B;

    /// <summary>
    /// How much of a fader's travel a held key covers in a second, and with Ctrl.
    /// </summary>
    const float SWEEP_PER_SECOND = 1;
    const float FINE_SWEEP_PER_SECOND = 0.25f;

    enum Band { Low, Mid, High }

    readonly IReadOnlyList<Deck> _decks;
    readonly AudioEngine _engine;

    // The keys held down for sliding, with the modifiers they went down with.
    readonly Dictionary<GlfwKey, GlfwModifier> _held = [];

    // Where each killed band was before its kill key, to go back to on the next press.
    readonly Dictionary<(Deck, Band), float> _killedFrom = [];

    // Decks whose tempo keys were pressed together to reset, which don't slide until both come up.
    readonly HashSet<Deck> _tempoReset = [];

    public KeyboardControls(IReadOnlyList<Deck> decks, AudioEngine engine)
    {
        _decks = decks;
        _engine = engine;
    }

    public static DeckKeyHints? HintsFor(int deck) => deck >= 0 && deck < Keys.Length ? Keys[deck].Hints : null;

    /// <summary>
    /// Acts on a key the UI didn't use, if it's one of these. Auto-repeats are ignored.
    /// </summary>
    public void Key(GlfwKey key, GlfwKeyAction action, GlfwModifier mods)
    {
        if (action == GlfwKeyAction.Repeat)
            return;
        var pressed = action == GlfwKeyAction.Press;
        if (pressed)
            _held[key] = mods;
        else
            _held.Remove(key);

        if (key == CROSSFADER_CENTRE && pressed)
            _engine.Crossfader = 0.5f;
        for (var i = 0; i < Keys.Length && i < _decks.Count; i++)
            DeckKey(_decks[i], Keys[i], key, pressed, mods);
    }

    void DeckKey(Deck deck, DeckKeys keys, GlfwKey key, bool pressed, GlfwModifier mods)
    {
        // Cue previews while held, so it acts on the release too.
        if (key == keys.Cue)
        {
            if (pressed)
                deck.CueDown();
            else
                deck.CueUp();
            return;
        }

        if (key == keys.TempoDown || key == keys.TempoUp)
        {
            // Both together put the tempo back to 0%.
            var other = key == keys.TempoDown ? keys.TempoUp : keys.TempoDown;
            if (pressed && (mods & GlfwModifier.Shift) == 0 && _held.TryGetValue(other, out var otherMods) && (otherMods & GlfwModifier.Shift) == 0)
            {
                deck.Tempo = 0;
                _tempoReset.Add(deck);
            }
            else if (!pressed && !_held.ContainsKey(keys.TempoDown) && !_held.ContainsKey(keys.TempoUp))
                _tempoReset.Remove(deck);
            return;
        }

        // The rest act once, on the press. The filter keys only slide, in Update.
        if (!pressed)
            return;
        if (key == keys.Play)
            deck.TogglePlay();
        else if (key == keys.Sync)
            deck.Sync();
        else if (key == keys.Loop)
            deck.ToggleLoop();
        else if (key == keys.HalveLoop)
            deck.HalveLoop();
        else if (key == keys.DoubleLoop)
            deck.DoubleLoop();
        else if (key == keys.Pfl)
            deck.Headphones = !deck.Headphones;
        else if (key == keys.KeyLock)
            deck.KeyLocked = !deck.KeyLocked;
        else if (key == keys.KillLow)
            ToggleKill(deck, Band.Low);
        else if (key == keys.KillMid)
            ToggleKill(deck, Band.Mid);
        else if (key == keys.KillHigh)
            ToggleKill(deck, Band.High);
        else if (key == keys.ResetEq)
            ResetEq(deck);
    }

    /// <summary>
    /// Kills a band, or brings back a killed one to where it was. A band turned all the way down by hand counts as
    /// killed, and comes back to the middle.
    /// </summary>
    void ToggleKill(Deck deck, Band band)
    {
        var eq = deck.Equalizer;
        var value = band switch { Band.Low => eq.Low, Band.Mid => eq.Mid, _ => eq.High };
        float next;
        if (value <= -1)
        {
            next = _killedFrom.Remove((deck, band), out var before) ? before : 0;
        }
        else
        {
            _killedFrom[(deck, band)] = value;
            next = -1;
        }

        switch (band)
        {
            case Band.Low:
                eq.Low = next;
                break;
            case Band.Mid:
                eq.Mid = next;
                break;
            default:
                eq.High = next;
                break;
        }
    }

    void ResetEq(Deck deck)
    {
        deck.Equalizer.Low = deck.Equalizer.Mid = deck.Equalizer.High = deck.Equalizer.Filter = 0;
        foreach (var band in Enum.GetValues<Band>())
            _killedFrom.Remove((deck, band));
    }

    /// <summary>
    /// Moves the faders whose keys are held, by however far <paramref name="seconds"/> takes them.
    /// </summary>
    public void Update(double seconds)
    {
        if (_held.Count == 0)
            return;
        var dt = (float)Math.Min(seconds, 0.1);

        _engine.Crossfader += Direction(CROSSFADER_LEFT, CROSSFADER_RIGHT, shift: false) * dt;

        for (var i = 0; i < Keys.Length && i < _decks.Count; i++)
        {
            var (deck, keys) = (_decks[i], Keys[i]);
            deck.Volume += Direction(keys.TempoDown, keys.TempoUp, shift: true) * dt;
            deck.Equalizer.Filter += Direction(keys.LowPass, keys.HighPass, shift: false) * 2 * dt;
            if (!_tempoReset.Contains(deck))
            {
                var tempo = Direction(keys.TempoDown, keys.TempoUp, shift: false);
                if (tempo != 0)
                    deck.Tempo += tempo * 2 * deck.TempoRange * dt;
            }
        }
    }

    /// <summary>
    /// Which way a pair of held keys pushes a fader, at its speed: negative for <paramref name="down"/>, positive for
    /// <paramref name="up"/>, nothing for both or neither. Only keys that went down with Shift if
    /// <paramref name="shift"/>, or without it if not.
    /// </summary>
    float Direction(GlfwKey down, GlfwKey up, bool shift)
    {
        float Speed(GlfwKey key) =>
            _held.TryGetValue(key, out var mods) && ((mods & GlfwModifier.Shift) != 0) == shift
                ? (mods & GlfwModifier.Control) != 0 ? FINE_SWEEP_PER_SECOND : SWEEP_PER_SECOND
                : 0;
        return Speed(up) - Speed(down);
    }
}
