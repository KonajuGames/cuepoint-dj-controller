using Brutal.Numerics;
using Cuepoint.Audio;

namespace Cuepoint.UI;

/// <summary>
/// How well two decks' beats line up, in a strip between their scrolling waveforms: a track with a line in the middle
/// for the upper deck's beat, and a bar out from it to where the lower deck's beat is, half a beat at either end. As the
/// waveforms scroll leftwards, a lower deck that's ahead has its beats left of the upper's, so the bar points left, the
/// same way its beat lines are off; nudge it the other way to close it up. Green while they're within
/// <see cref="IN_PHASE_SECONDS"/>, with how far apart they are written beside it. Empty unless both decks are playing
/// songs with a beat grid.
/// </summary>
sealed class PhaseMeter : Element
{
    public const float HEIGHT = 14;
    const float TRACK_HEIGHT = 6;
    const float MAX_TRACK_WIDTH = 360;

    /// <summary>
    /// How close the beats have to be, as heard, to count as lined up: about as close as an ear can tell apart.
    /// </summary>
    public const double IN_PHASE_SECONDS = 0.010;

    /// <summary>
    /// How quickly what's shown catches up with each reading, as positions come in jumps of a buffer: how much of the
    /// way it goes in a second, as an exponent.
    /// </summary>
    const double SMOOTHING_RATE = 30;

    readonly Deck _upper;
    readonly Deck _lower;
    readonly Font _font;
    (double Beats, double Seconds)? _shown;
    long _lastUpdate = Environment.TickCount64;

    public PhaseMeter(Deck upper, Deck lower, Font font)
    {
        _upper = upper;
        _lower = lower;
        _font = font;
        Tooltip = $"How far deck {lower.Name}'s beats are from deck {upper.Name}'s: the bar points to where {lower.Name}'s beat is, and is green when they line up";
    }

    public float Width { get; set; } = 800;

    // Only so the pointer resting on it shows the tooltip.
    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// How far the lower deck's beats are ahead of the upper's, smoothed, in beats and seconds as heard; null unless
    /// both decks are playing songs with a beat grid.
    /// </summary>
    public (double Beats, double Seconds)? Shown => _shown;

    /// <summary>
    /// Takes a reading, easing towards it, except across the ends, where half a beat ahead becomes half behind, or
    /// after a jump, which it goes straight to.
    /// </summary>
    public void Update()
    {
        var now = Environment.TickCount64;
        var seconds = (now - _lastUpdate) / 1000.0;
        _lastUpdate = now;

        var reading = _upper.IsPlaying && _lower.IsPlaying ? _lower.PhaseAgainst(_upper) : null;
        _shown = Ease(_shown, reading, 1 - Math.Exp(-SMOOTHING_RATE * seconds));
    }

    internal static (double Beats, double Seconds)? Ease((double Beats, double Seconds)? from, (double Beats, double Seconds)? to, double amount)
    {
        if (to is not { } target || from is not { } shown || Math.Abs(target.Beats - shown.Beats) > 0.25)
            return to;
        return (shown.Beats + (target.Beats - shown.Beats) * amount, shown.Seconds + (target.Seconds - shown.Seconds) * amount);
    }

    public override float2 Measure() => Size = new float2(Width, HEIGHT);

    float TrackWidth => MathF.Min(MAX_TRACK_WIDTH, MathF.Round(Width * 0.3f / 2) * 2);

    public override void Draw(Batch batch)
    {
        var track = new float2(TrackWidth, TRACK_HEIGHT);
        var centre = MathF.Round(ScreenPosition.X + Width / 2);
        var top = MathF.Round(ScreenPosition.Y + (HEIGHT - TRACK_HEIGHT) / 2);
        var left = centre - track.X / 2;
        batch.AddRect(new float2(left, top), track, Colors.Hex(0x000000));

        // Faint marks a quarter of a beat out either way, for a sense of scale.
        foreach (var quarter in new[] { -0.25f, 0.25f })
            batch.AddRect(new float2(MathF.Round(centre + quarter * track.X), top), new float2(1, TRACK_HEIGHT), Colors.Hex(0xFFFFFF, 0.18f));

        if (_shown is { } shown)
        {
            var lined = Math.Abs(shown.Seconds) <= IN_PHASE_SECONDS;
            var color = lined ? Theme.Connected : Theme.Accent;

            // Left for ahead, as its beat lines are on the waveforms. At least two pixels, so a hair out still shows.
            var x = centre - (float)shown.Beats * track.X;
            var (from, to) = x < centre ? (MathF.Floor(MathF.Min(x, centre - 2)), centre) : (centre, MathF.Ceiling(MathF.Max(x, centre + 2)));
            if (!lined)
                batch.AddRect(new float2(from, top + 1), new float2(to - from, TRACK_HEIGHT - 2), color);

            var text = lined ? "in phase" : $"{_lower.Name} {Math.Abs(shown.Seconds) * 1000:0} ms {(shown.Seconds > 0 ? "ahead" : "behind")}";
            var y = MathF.Round(ScreenPosition.Y + HEIGHT / 2 + BeatTrackers.CapHeight(_font) / 2 - _font.Ascender);
            batch.AddText(_font, text, new float2(left + track.X + 8, y), color);
            batch.AddRect(new float2(centre - 1, ScreenPosition.Y), new float2(2, HEIGHT), color);
        }
        else
        {
            batch.AddRect(new float2(centre - 1, top), new float2(2, TRACK_HEIGHT), Colors.Hex(0xFFFFFF, 0.25f));
        }
    }
}
