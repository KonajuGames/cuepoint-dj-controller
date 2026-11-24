using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Music;

/// <summary>
/// A deck's pads in JUMP mode: in pairs, back and on through the song by 1, 4, 16 and 32 bars, staying in time (see
/// <see cref="Deck.BeatJump"/>). A bar puts a drop back in place, 16 bars skips an intro, and 32 bars a whole section.
/// </summary>
sealed class JumpPads(Deck deck, GridEditor? grid = null, RollPads? roll = null)
{
    bool _active;

    /// <summary>
    /// Each pad's jump, in beats: back for negative. Each pair of pads, back then on, jumps the same way.
    /// </summary>
    public static readonly IReadOnlyList<double> Sizes = [-4, 4, -16, 16, -64, 64, -128, 128];

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
    /// How many bars pad <paramref name="index"/> jumps, shown once for its pair, on the border between them.
    /// </summary>
    public static string Label(int index) => $"{Bars(index):0}";

    /// <summary>
    /// How many bars pad <paramref name="index"/> jumps, either way.
    /// </summary>
    public static double Bars(int index) => Math.Abs(Sizes[index]) / BeatGrid.BEATS_PER_BAR;

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
