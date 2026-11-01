namespace Brutality.UI;

/// <summary>
/// What's printed on the keys for a deck's buttons, to label them with.
/// </summary>
sealed record DeckKeyHints(
    string Cue,
    string Play,
    string Sync,
    string HalveLoop,
    string Loop,
    string DoubleLoop,
    string KeyLock,
    string Pfl
);
