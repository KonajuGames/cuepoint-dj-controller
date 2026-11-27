using Brutal.GlfwApi;

namespace Cuepoint;

/// <summary>
/// One line of the shortcuts: what it does, and the keys for it on each deck, or for both if <see cref="DeckB"/> is
/// null.
/// </summary>
sealed record Shortcut(string Action, string DeckA, string? DeckB = null);

/// <summary>
/// Every keyboard shortcut, in words, for the shortcuts overlay: the decks' from <see cref="KeyboardControls.Keys"/>
/// and <see cref="HotcueKeys"/>, so they read as the keys really are, then the rest.
/// </summary>
static class KeyboardShortcuts
{
    /// <summary>
    /// The key that opens and closes the overlay, with Shift: "?".
    /// </summary>
    public const GlfwKey SHOW = GlfwKey.Slash;

    /// <summary>
    /// Each deck's shortcuts, deck A's keys then deck B's.
    /// </summary>
    public static IReadOnlyList<Shortcut> Decks
    {
        get
        {
            var (a, b) = (KeyboardControls.Keys[0], KeyboardControls.Keys[1]);
            Shortcut Row(string action, Func<DeckKeys, int, string> keys) => new(action, keys(a, 0), keys(b, 1));
            static string L(GlfwKey key) => DeckKeys.Label(key);

            return
            [
                Row("Hotcues 1 to 8", (_, deck) => string.Join(" ", HotcueKeys.Labels(deck))),
                Row("Clear a hotcue", (_, _) => "Shift + its key"),
                Row("Cue, play, sync, loop", (k, _) => $"{L(k.Cue)} {L(k.Play)} {L(k.Sync)} {L(k.Loop)}"),
                Row("Halve / double the loop", (k, _) => $"{L(k.HalveLoop)} / {L(k.DoubleLoop)}"),
                Row("Loop in / out", (k, _) => $"Shift + {L(k.HalveLoop)} / {L(k.DoubleLoop)}"),
                Row("Move the loop back / on a beat", (k, _) => $"{GlfwUiInput.CtrlName} + {L(k.HalveLoop)} / {L(k.DoubleLoop)}"),
                Row("Move the loop back / on a bar", (k, _) => $"{GlfwUiInput.CtrlName} + Shift + {L(k.HalveLoop)} / {L(k.DoubleLoop)}"),
                Row("Reloop", (k, _) => $"Shift + {L(k.Loop)}"),
                Row("Censor: backwards while held", (k, _) => $"{GlfwUiInput.CtrlName} + {L(k.Play)}"),
                Row("Tempo down / up (hold); both resets it", (k, _) => $"{L(k.TempoDown)} / {L(k.TempoUp)}"),
                Row("Volume down / up (hold)", (k, _) => $"Shift + {L(k.TempoDown)} / {L(k.TempoUp)}"),
                Row("Nudge slower / faster (hold)", (k, _) => $"{GlfwUiInput.CtrlName} + Shift + {L(k.TempoDown)} / {L(k.TempoUp)}"),
                Row("Key down / up a semitone", (k, _) => $"Alt + {L(k.TempoDown)} / {L(k.TempoUp)}"),
                Row("Put the key back", (k, _) => $"Alt + {L(k.KeyLock)}"),
                Row("Key match", (k, _) => $"{GlfwUiInput.CtrlName} + {L(k.KeyLock)}"),
                Row("Key lock", (k, _) => L(k.KeyLock)),
                Row("Slip", (k, _) => $"Shift + {L(k.KeyLock)}"),
                Row("Headphone cue", (k, _) => L(k.Pfl)),
                Row("Memory cue: previous, add or remove, next", (k, _) => $"Alt + {L(k.Cue)} {L(k.Play)} {L(k.Sync)}"),
                Row("Kill low / mid / high", (k, _) => $"{L(k.KillLow)} {L(k.KillMid)} {L(k.KillHigh)}"),
                Row("Reset the EQ and filter", (k, _) => L(k.ResetEq)),
                Row("Filter to low-pass / high-pass (hold)", (k, _) => $"{L(k.LowPass)} / {L(k.HighPass)}"),
            ];
        }
    }

    /// <summary>
    /// The mixer's, the lists' and the rest.
    /// </summary>
    public static IReadOnlyList<Shortcut> Everywhere =>
    [
        new(
            "Crossfader left / right (hold); centre",
            $"{DeckKeys.Label(KeyboardControls.CrossfaderLeft)} / {DeckKeys.Label(KeyboardControls.CrossfaderRight)}, {DeckKeys.Label(KeyboardControls.CrossfaderCentre)}"
        ),
        new("Move a fader a quarter as fast", $"{GlfwUiInput.CtrlName} with its key"),
        new("Load the selected song onto deck A / B", "1 / 2"),
        new("Load it onto a free deck", "Enter"),
        new("Load the next song in Up next onto deck A / B", "Shift + 1 / 2"),
        new("Unload deck A / B, leaving it empty", $"{GlfwUiInput.CtrlName} + 1 / 2"),
        new("Move through the songs", "Up / Down, Page Up / Down, Home / End"),
        new("Move through the folders; open one out", "Shift + Up / Down, Shift + Left / Right"),
        new("Search", $"{GlfwUiInput.CtrlName} + F; then Enter for the songs, Esc to clear"),
        new("Preview the selected song in the headphones", $"{GlfwUiInput.CtrlName} + P"),
        new("Undo a cue, grid or library list change", $"{GlfwUiInput.CtrlName} + Z"),
        new("Panic: EQs, trims, keys, effects and loops back to neutral", $"{GlfwUiInput.CtrlName} + Backspace"),
        new("In GRID, ROLL or BEAT JUMP mode", "Hotcue keys work the pads"),
        new("Next control / press it / leave a box", "Tab / Enter / Esc"),
        new("These shortcuts", "?"),
    ];
}
