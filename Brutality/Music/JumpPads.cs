using Brutality.Audio;

namespace Brutality.Music;

/// <summary>
/// A deck's pads in JUMP mode: each jumps through the song by a number of beats, back on the first four and on
/// through the last four, staying in time (see <see cref="Deck.BeatJump"/>). Thirty-two and sixteen beats are eight
/// and four bars, a phrase or half of one, for skipping an intro or lining up a drop.
/// </summary>
sealed class JumpPads(Deck deck, GridEditor? grid = null, RollPads? roll = null)
{
    bool _active;

    /// <summary>
    /// Each pad's jump, in beats: back for negative.
    /// </summary>
    public static readonly IReadOnlyList<double> Sizes = [-32, -16, -4, -1, 1, 4, 16, 32];

    public Deck Deck { get; } = deck;

    /// <summary>
    /// Whether the deck's pads jump rather than working hotcues. Turning it on turns GRID and ROLL mode off, and either
    /// of them turned on turns it off.
    /// </summary>
    public bool Active
    {
        get
        {
            if (grid is { Active: true } || roll is { Active: true })
                _active = false;
            return _active;
        }
        set
        {
            _active = value;
            if (!value)
                return;
            if (grid is not null)
                grid.Active = false;
            if (roll is not null)
                roll.Active = false;
        }
    }

    /// <summary>
    /// What pad <paramref name="index"/> says: how many beats it jumps, its direction shown by an arrow beside.
    /// </summary>
    public static string Label(int index) => $"{Math.Abs(Sizes[index]):0}";

    /// <summary>
    /// Which way pad <paramref name="index"/> jumps: -1 back, 1 on.
    /// </summary>
    public static int Direction(int index) => Math.Sign(Sizes[index]);

    public void Down(int index)
    {
        if (index >= 0 && index < Sizes.Count)
            Deck.BeatJump(Sizes[index]);
    }
}
