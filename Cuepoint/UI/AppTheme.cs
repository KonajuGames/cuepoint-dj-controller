using Brutal.Numerics;

namespace Cuepoint.UI;

/// <summary>
/// Cuepoint's own colours, on top of the ones the Cue apps share (<see cref="Theme"/>): a colour for each deck that
/// its waveforms, meters and buttons share, the hotcues' and the Camelot keys'.
/// </summary>
static class AppTheme
{
    /// <summary>
    /// The tick on a song played this session.
    /// </summary>
    public static readonly float4 Played = Colors.Hex(0x6CCB7A);

    /// <summary>
    /// A loop, wherever it shows: its button lit, and its stretch of the waveforms shaded.
    /// </summary>
    public static readonly float4 Loop = Colors.Hex(0x48D17A);

    public static readonly float4 DeckA = Colors.Hex(0x2F8FFF);
    public static readonly float4 DeckB = Colors.Hex(0xEE4444);

    /// <summary>
    /// The colour for the deck called <paramref name="name"/>: blue for A, red for B.
    /// </summary>
    public static float4 DeckColor(string name) => name == "B" ? DeckB : DeckA;

    static readonly float4[] Hotcues =
    [
        Colors.Hex(0xE8453C),
        Colors.Hex(0xF08A24),
        Colors.Hex(0xF2C230),
        Colors.Hex(0x4CC35A),
        Colors.Hex(0x2EC4C4),
        Colors.Hex(0x3D8BF2),
        Colors.Hex(0x8E5CF0),
        Colors.Hex(0xE858B8),
    ];

    /// <summary>
    /// Hotcue <paramref name="index"/>'s colour, from 0: round the rainbow, so neighbouring pads are told apart.
    /// </summary>
    public static float4 HotcueColor(int index) => Hotcues[((index % Hotcues.Length) + Hotcues.Length) % Hotcues.Length];

    /// <summary>
    /// A colour for each Camelot number, round the colour wheel as the keys go round theirs, so keys that mix sit
    /// next to each other in colour too.
    /// </summary>
    public static float4 CamelotColor(int number)
    {
        var hue = (number - 1) / 12f;
        return Theme.Hsv(hue, 0.55f, 1f);
    }
}

/// <summary>
/// A solid square with a letter or two centred in it: a deck's letter, or the app's logo.
/// </summary>
sealed class Badge(Font font, string text, float4 background, float4 color, float size) : Element
{
    public override float2 Measure() => Size = new float2(size, size);

    public override void Draw(Batch batch)
    {
        batch.AddRect(ScreenPosition, Size, background);
        var extent = font.Measure(text);
        batch.AddText(font, text, new float2(MathF.Round(ScreenPosition.X + (size - extent.X) / 2), MathF.Round(ScreenPosition.Y + (size - extent.Y) / 2)), color);
    }
}

