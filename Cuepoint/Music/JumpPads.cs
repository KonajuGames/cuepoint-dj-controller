using Cuepoint.Audio;

namespace Cuepoint.Music;

/// <summary>
/// A deck's pads in BEAT JUMP mode: in pairs, back and on through the song by 4, 8, 16 and 32 beats, staying in time
/// (see <see cref="Deck.BeatJump"/>). Four beats is a bar, and 32 eight bars, a phrase.
/// </summary>
sealed class JumpPads(Deck deck, GridEditor? grid = null, RollPads? roll = null)
{
    bool _active;

    /// <summary>
    /// Each pad's jump, in beats: back for negative. Each pair of pads, back then on, jumps the same way.
    /// </summary>
    public static readonly IReadOnlyList<double> Sizes = [-4, 4, -8, 8, -16, 16, -32, 32];

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
