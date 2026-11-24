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

    /// <summary>
    /// The decks' CUE button, outlined and labelled in it, amber as a CDJ's is.
    /// </summary>
    public static readonly float4 Cue = Colors.Hex(0xF2A33A);

    /// <summary>
    /// The decks' play button, outlined in it, and filled with it while playing, green as a CDJ's is.
    /// </summary>
    public static readonly float4 Play = Colors.Hex(0x3CCF6E);

    /// <summary>
    /// The pads in SAMPLER mode.
    /// </summary>
    public static readonly float4 Sampler = Colors.Hex(0xB57BE0);

    /// <summary>
    /// STEMS mode's tint.
    /// </summary>
    public static readonly float4 Stems = Colors.Hex(0xE8A03C);

    // Each stem's pads: vocals, drums, bass, instruments.
    static readonly float4[] StemColors = [Colors.Hex(0xFF6FA8), Colors.Hex(0xFFB547), Colors.Hex(0x4FA3FF), Colors.Hex(0x5ED4A0)];

    public static float4 StemColor(int stem) => StemColors[stem % StemColors.Length];

    /// <summary>
    /// What the waveforms, the phase meter and the beat trackers are drawn on: dark in every look, as a DJ app's
    /// waveforms are, so the beat grid's and playhead's white lines show on it.
    /// </summary>
    public static readonly float4 Screen = Colors.Hex(0x0E0F12);

    /// <summary>
    /// Behind a deck's overview: darkening the panel on a dark look, and the waveforms' own dark on a light one.
    /// </summary>
    public static float4 Overview => Theme.IsLight ? Screen : Colors.Hex(0x000000, 0.25f);

    /// <summary>
    /// An unlit box or track in a meter (the beat trackers', the phase meter's): black, but on a look whose panels are
    /// black already, a dark grey that shows against them.
    /// </summary>
    public static float4 Unlit => Theme.Kind == ThemeKind.HighContrast ? Theme.PanelRaised : Colors.Hex(0x000000);

    /// <summary>
    /// <paramref name="color"/>, deepened on a light look, for a bright colour (a tag's, a folder icon's) that would
    /// wash out on white.
    /// </summary>
    public static float4 OnPage(float4 color) => Theme.IsLight ? Theme.Mix(color, Colors.Hex(0x000000), 0.35f) : color;

    public static readonly float4 DeckA = Colors.Hex(0x2F8FFF);
    public static readonly float4 DeckB = Colors.Hex(0xEE4444);

    /// <summary>
    /// The colour for the deck called <paramref name="name"/>: blue for A, red for B.
    /// </summary>
    public static float4 DeckColor(string name) => name == "B" ? DeckB : DeckA;

    /// <summary>
    /// The hotcue pads' colours, in order round the rainbow, with what to call each, to give one a hot cue.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, uint Hex)> HotcuePalette =
    [
        ("Red", 0xE8453C),
        ("Orange", 0xF08A24),
        ("Yellow", 0xF2C230),
        ("Green", 0x4CC35A),
        ("Cyan", 0x2EC4C4),
        ("Blue", 0x3D8BF2),
        ("Purple", 0x8E5CF0),
        ("Pink", 0xE858B8),
    ];

    static readonly float4[] Hotcues = [.. HotcuePalette.Select(colour => Colors.Hex(colour.Hex))];

    /// <summary>
    /// Hotcue <paramref name="index"/>'s colour, from 0: round the rainbow, so neighbouring pads are told apart.
    /// </summary>
    public static float4 HotcueColor(int index) => Hotcues[((index % Hotcues.Length) + Hotcues.Length) % Hotcues.Length];

    /// <summary>
    /// Hotcue <paramref name="index"/>'s colour on <paramref name="deck"/>: the one it's been given, or its pad's own.
    /// </summary>
    public static float4 HotcueColor(Cuepoint.Audio.Deck deck, int index) =>
        deck.DetailOf(index)?.Color is { } given ? Colors.Hex(given) : HotcueColor(index);

    /// <summary>
    /// A colour for each Camelot number, round the colour wheel as the keys go round theirs, so keys that mix sit
    /// next to each other in colour too.
    /// </summary>
    public static float4 CamelotColor(int number)
    {
        // Deeper and less bright on a light look, as pastels wash out on white.
        var hue = (number - 1) / 12f;
        return Theme.IsLight ? Theme.Hsv(hue, 0.85f, 0.62f) : Theme.Hsv(hue, 0.55f, 1f);
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

