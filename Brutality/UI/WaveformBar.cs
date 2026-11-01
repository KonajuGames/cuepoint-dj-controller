using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.UI;

/// <summary>
/// A song's progress bar drawn as its waveform: loud passages stand tall and breaks and drops show as gaps, so you can
/// see what's coming. The part already played is coloured; seeking works as on any <see cref="Slider"/>. With no
/// <see cref="Waveform"/> it draws a plain track. With a <see cref="Grid"/>, its beats are drawn over the waveform,
/// as many as fit: every beat, or every bar, or every few bars, with every fourth line brighter. <see cref="Slider.Min"/>
/// and <see cref="Slider.Max"/> are taken to be seconds into the song, for placing them. <see cref="Flags"/>, such as
/// hotcues, are drawn over all that as coloured lines with a labelled flag at the top.
/// </summary>
sealed class WaveformBar : Slider
{
    /// <summary>
    /// The closest grid lines are drawn, in pixels. Any closer and they blur into a comb; at this, a whole song
    /// across a deck shows a line every four bars and a brighter one every sixteen, the phrases dance music is built
    /// in.
    /// </summary>
    public const float MIN_GRID_SPACING = 10;

    public Waveform? Waveform { get; set; }

    public BeatGrid? Grid { get; set; }

    /// <summary>
    /// Places to mark, as values between <see cref="Slider.Min"/> and <see cref="Slider.Max"/>: each a line in its
    /// colour with a flag at the top carrying its label, in <see cref="FlagFont"/>.
    /// </summary>
    public IReadOnlyList<(float Value, float4 Color, string Label)> Flags { get; set; } = [];

    /// <summary>
    /// A stretch to shade, such as a loop, as values between <see cref="Slider.Min"/> and <see cref="Slider.Max"/>,
    /// in <see cref="RegionColor"/>, with solid edges.
    /// </summary>
    public (float Start, float End)? Region { get; set; }

    public float4 RegionColor { get; set; } = Colors.Hex(0x48D17A);

    public Font? FlagFont { get; set; }

    public float4 GridColor { get; set; } = Colors.Hex(0xFFFFFF, 0.14f);
    public float4 StrongGridColor { get; set; } = Colors.Hex(0xFFFFFF, 0.55f);

    public float4 PlayedColor { get; set; } = Colors.Hex(0xEB7828);
    public float4 UnplayedColor { get; set; } = Colors.Hex(0xFFFFFF, 0.35f);
    public float4 BackgroundColor { get; set; } = Colors.Hex(0x000000, 0.25f);

    protected override void DrawTrack(Batch batch)
    {
        if (Waveform is not { } waveform || IsVertical)
        {
            base.DrawTrack(batch);
            return;
        }

        batch.AddRect(ScreenPosition, Size, Faded(BackgroundColor));

        // One column per pixel of the handle's travel, centred on the handle's middle at each point, mirrored about
        // the bar's centre line. Quiet stretches keep a pixel of height so the song's extent still shows.
        var left = ScreenPosition.X + HandleLength / 2;
        var middle = ScreenPosition.Y + Size.Y / 2;
        var halfHeight = Size.Y / 2 - 1;
        var columns = (int)MathF.Ceiling(Travel);
        var playhead = OffsetOf(Fraction);
        var played = Faded(PlayedColor);
        var unplayed = Faded(UnplayedColor);
        for (var column = 0; column < columns; column++)
        {
            var peak = waveform.PeakBetween(column / Travel, (column + 1) / Travel);
            var height = MathF.Max(1, MathF.Round(peak * halfHeight));
            batch.AddRect(
                new float2(left + column, middle - height),
                new float2(1, height * 2),
                column + 0.5f < playhead ? played : unplayed
            );
        }

        DrawRegion(batch, left);
        DrawGrid(batch, left);
        DrawFlags(batch, left);
    }

    void DrawRegion(Batch batch, float left)
    {
        if (Region is not { } region || Max <= Min)
            return;
        var start = MathF.Round(left + OffsetOf(Math.Clamp((region.Start - Min) / (Max - Min), 0, 1)));
        var end = MathF.Max(start + 1, MathF.Round(left + OffsetOf(Math.Clamp((region.End - Min) / (Max - Min), 0, 1))));
        batch.AddRect(new float2(start, ScreenPosition.Y), new float2(end - start, Size.Y), Faded(Theme.Fade(RegionColor, 0.35f)));
        batch.AddRect(new float2(start, ScreenPosition.Y), new float2(1, Size.Y), Faded(RegionColor));
        batch.AddRect(new float2(end - 1, ScreenPosition.Y), new float2(1, Size.Y), Faded(RegionColor));
    }

    void DrawFlags(Batch batch, float left)
    {
        if (Max <= Min)
            return;
        foreach (var (value, color, label) in Flags)
        {
            var fraction = Math.Clamp((value - Min) / (Max - Min), 0, 1);
            var x = MathF.Round(left + OffsetOf(fraction));
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(1, Size.Y), Faded(color));
            if (FlagFont is { } font)
                DrawFlag(batch, font, new float2(x, ScreenPosition.Y), label, Faded(color), Faded(Theme.TitleBar));
        }
    }

    /// <summary>
    /// A small flag in <paramref name="color"/> hanging right from <paramref name="at"/>, with <paramref name="label"/>
    /// in it: the head of a hotcue's line.
    /// </summary>
    public static void DrawFlag(Batch batch, Font font, float2 at, string label, float4 color, float4 textColor)
    {
        var extent = font.Measure(label);
        var height = MathF.Round(font.GetGlyph('0').Bearing.Y + 4);
        var size = new float2(MathF.Round(extent.X + 4), height);
        batch.AddRect(at, size, color);
        var baseline = at.Y + MathF.Round((height + font.GetGlyph('0').Bearing.Y) / 2);
        batch.AddText(font, label, new float2(at.X + 2, baseline - font.Ascender), textColor);
    }

    /// <summary>
    /// How many beats apart the grid lines are, to keep them at least <see cref="MIN_GRID_SPACING"/> apart: a
    /// power of two, so they always fall on bars once they're four or more apart.
    /// </summary>
    public static int GridInterval(double pixelsPerBeat)
    {
        var interval = 1;
        while (interval * pixelsPerBeat < MIN_GRID_SPACING && interval < 1 << 20)
            interval *= 2;
        return interval;
    }

    /// <summary>
    /// The beats the grid lines are drawn on, and whether each is one of the brighter ones.
    /// </summary>
    public IEnumerable<(double Beat, bool Strong)> GridLines()
    {
        if (Grid is not { } grid || Max <= Min)
            yield break;

        var first = grid.BeatNumberAt(Min);
        var last = grid.BeatNumberAt(Max);
        if (last <= first)
            yield break;
        var interval = GridInterval(Travel / (last - first));

        // Counted from the grid's first beat, so the same lines show whatever the zoom, and bars stay on bars.
        for (var beat = Math.Ceiling(first / interval) * interval; beat <= last; beat += interval)
            yield return (beat, (long)Math.Round(beat / interval) % 4 == 0);
    }

    void DrawGrid(Batch batch, float left)
    {
        if (Grid is not { } grid)
            return;

        var normal = Faded(GridColor);
        var strong = Faded(StrongGridColor);
        foreach (var (beat, isStrong) in GridLines())
        {
            var fraction = (float)((grid.TimeOfBeat(beat) - Min) / (Max - Min));
            var x = MathF.Round(left + OffsetOf(fraction));
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(1, Size.Y), isStrong ? strong : normal);
        }
    }
}
