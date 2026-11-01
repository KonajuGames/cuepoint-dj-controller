using Brutality.Audio;

namespace Brutality.Music;

/// <summary>
/// A deck's pads in ROLL mode: each holds a loop roll of its size while it's down (see <see cref="Deck.RollDown"/>),
/// from a sixteenth of a beat on the first to eight beats on the last, and lets it go when it comes up, dropping back
/// in where the song has got to.
/// </summary>
sealed class RollPads(Deck deck, GridEditor? grid = null)
{
    bool _active;

    /// <summary>
    /// Each pad's roll, in beats.
    /// </summary>
    public static readonly IReadOnlyList<double> Sizes = [1.0 / 16, 1.0 / 8, 1.0 / 4, 1.0 / 2, 1, 2, 4, 8];

    public Deck Deck { get; } = deck;

    /// <summary>
    /// Whether the deck's pads roll loops rather than working hotcues. Turning it on turns GRID mode off, and GRID
    /// mode turned on turns it off.
    /// </summary>
    public bool Active
    {
        get
        {
            if (grid is { Active: true })
                _active = false;
            return _active;
        }
        set
        {
            _active = value;
            if (value && grid is not null)
                grid.Active = false;
        }
    }

    /// <summary>
    /// What pad <paramref name="index"/> says: its size in beats.
    /// </summary>
    public static string Label(int index) => Sizes[index] >= 1 ? $"{Sizes[index]:0}" : $"1/{Math.Round(1 / Sizes[index]):0}";

    /// <summary>
    /// Whether pad <paramref name="index"/>'s roll is the one playing.
    /// </summary>
    public bool IsRolling(int index) => Deck.RollBeats == Sizes[index];

    public void Down(int index)
    {
        if (index >= 0 && index < Sizes.Count)
            Deck.RollDown(Sizes[index]);
    }

    public void Up(int index)
    {
        if (index >= 0 && index < Sizes.Count)
            Deck.RollUp(Sizes[index]);
    }
}
