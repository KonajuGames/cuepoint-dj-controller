using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// The app's colours, in one place so the screen hangs together: near-black panels, muted text for labels, orange
/// for the app's own accents, and a colour for each deck that its waveforms, meters and buttons share.
/// </summary>
static class Theme
{
    public static readonly float4 Background = Colors.Hex(0x121318);
    public static readonly float4 TitleBar = Colors.Hex(0x0B0C0F);
    public static readonly float4 Panel = Colors.Hex(0x1B1C22);
    public static readonly float4 PanelRaised = Colors.Hex(0x23242C);
    public static readonly float4 PanelBorder = Colors.Hex(0x2E3039);
    public static readonly float4 Well = Colors.Hex(0x0E0F12);

    public static readonly float4 Text = Colors.Hex(0xE4E5EC);
    public static readonly float4 Muted = Colors.Hex(0x8C8E9C);
    public static readonly float4 Faint = Colors.Hex(0x5A5C68);

    public static readonly float4 Accent = Colors.Hex(0xEB7828);
    public static readonly float4 Focus = Colors.Hex(0x7AB8FF);

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
    /// <paramref name="color"/> with its alpha multiplied by <paramref name="alpha"/>.
    /// </summary>
    public static float4 Fade(float4 color, float alpha) => color * new float4(1, 1, 1, alpha);

    /// <summary>
    /// Mixes from <paramref name="from"/> to <paramref name="to"/> by <paramref name="t"/>, from 0 to 1.
    /// </summary>
    public static float4 Mix(float4 from, float4 to, float t) => from + (to - from) * Math.Clamp(t, 0, 1);

    /// <summary>
    /// A colour for each Camelot number, round the colour wheel as the keys go round theirs, so keys that mix sit
    /// next to each other in colour too.
    /// </summary>
    public static float4 CamelotColor(int number)
    {
        var hue = (number - 1) / 12f;
        return Hsv(hue, 0.55f, 1f);
    }

    static float4 Hsv(float hue, float saturation, float value)
    {
        float Channel(float n)
        {
            var k = (n + hue * 6) % 6;
            return value - value * saturation * MathF.Max(0, MathF.Min(MathF.Min(k, 4 - k), 1));
        }
        // Worked out in sRGB, as picked, then made linear for the swapchain.
        float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return new float4(Linear(Channel(5)), Linear(Channel(3)), Linear(Channel(1)), 1);
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

