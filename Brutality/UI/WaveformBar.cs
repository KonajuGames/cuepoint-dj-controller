using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.UI;

/// <summary>
/// A song's progress bar drawn as its waveform: loud passages stand tall and breaks and drops show as gaps, so you can
/// see what's coming. The part already played is coloured; seeking works as on any <see cref="Slider"/>. With no
/// <see cref="Waveform"/> it draws a plain track. With a <see cref="Grid"/>, its beats are drawn over the waveform,
/// as many as fit: every beat, or every bar, or every few bars, with every fourth line brighter. <see cref="Slider.Min"/>
/// and <see cref="Slider.Max"/> are taken to be seconds into the song, for placing them.
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

        DrawGrid(batch, left);
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
