using Cuepoint.Audio;

namespace Cuepoint.Music;

/// <summary>
/// A deck's pads in SAMPLER mode: each plays one of the <see cref="Sampler"/>'s samples, the same on every deck, so
/// either hand can fire them; <see cref="Bank"/> says which eight.
/// </summary>
sealed class SamplerPads(Deck deck, Sampler sampler, GridEditor? grid = null, RollPads? roll = null, JumpPads? jump = null)
{
    bool _active;

    public Deck Deck { get; } = deck;

    public Sampler Sampler { get; } = sampler;

    /// <summary>
    /// Whether the deck's pads play samples rather than working hotcues. Turning it on turns GRID, ROLL and BEAT JUMP
    /// mode off, and any of them turned on turns it off.
    /// </summary>
    public bool Active
    {
        get
        {
            if (grid is { Active: true } || roll is { Active: true } || jump is { Active: true })
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
            if (jump is not null)
                jump.Active = false;
        }
    }

    /// <summary>
    /// Which bank of eight samples the pads play: 0 the first, 1 the second.
    /// </summary>
    public int Bank { get; set; }

    /// <summary>
    /// The sampler's slot pad <paramref name="index"/> plays, in the bank chosen.
    /// </summary>
    public int Slot(int index) => Bank * Sampler.PADS + index;

    public void Down(int index) => Sampler.Trigger(Slot(index));

    /// <summary>
    /// A pad Shift-clicked: its sample taken off it.
    /// </summary>
    public void Clear(int index) => Sampler.Clear(Slot(index));
}
