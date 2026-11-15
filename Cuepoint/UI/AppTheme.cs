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
/// A solid square with a letter or two centred in it: a deck's letter, or the app's logo. The letters' ink is what's
/// centred, not their line box, which carries room for descenders and accents that would push them off centre.
/// </summary>
sealed class Badge(Font font, string text, float4 background, float4 color, float size) : Element
{
    public override float2 Measure() => Size = new float2(size, size);

    public override void Draw(Batch batch)
    {
        batch.AddRect(ScreenPosition, Size, background);

        // The ink's bounds relative to where the text is drawn from, placed as Batch.AddText places each glyph: the
        // pen moving right along a baseline an ascender down.
        float penX = 0, left = float.MaxValue, right = float.MinValue, top = float.MaxValue, bottom = float.MinValue;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = font.GetGlyph(rune.Value);
            if (glyph.Size.X > 0 && glyph.Size.Y > 0)
            {
                var x = MathF.Round(penX) + glyph.Bearing.X;
                var y = font.Ascender - glyph.Bearing.Y;
                (left, right) = (MathF.Min(left, x), MathF.Max(right, x + glyph.Size.X));
                (top, bottom) = (MathF.Min(top, y), MathF.Max(bottom, y + glyph.Size.Y));
            }
            penX += glyph.Advance;
        }
        if (left > right)
            return;

        var origin = ScreenPosition + new float2((size - (right - left)) / 2 - left, (size - (bottom - top)) / 2 - top);
        batch.AddText(font, text, new float2(MathF.Round(origin.X), MathF.Round(origin.Y)), color);
    }
}

