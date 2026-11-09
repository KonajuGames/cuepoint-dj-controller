using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.UI;

/// <summary>
/// A song's progress bar drawn as its waveform, standing up from the bottom (the top half of the waveform, which is
/// all an overview needs): loud passages stand tall and breaks and drops show as gaps, so you can see what's coming.
/// The part already played is coloured in the deck's colour; or, with <see cref="Colours"/> three bands and a
/// <see cref="Detail"/> to colour it by, the song's in bass, mids and treble, the part to come at full colour and
/// the part played much darker. Seeking works as on any
/// <see cref="Slider"/>. With no
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

    /// <summary>
    /// The song's waveform in detail, for its bass, mids and treble, to colour it by in three bands.
    /// </summary>
    public DetailWaveform? Detail { get; set; }

    /// <summary>
    /// The deck's colour, or three bands, as the scrolling waveforms are.
    /// </summary>
    public WaveformColours Colours { get; set; }

    /// <summary>
    /// How bright the part of the song already played is, in three bands, against the part to come at full colour: in
    /// linear light, so about a third as bright to the eye.
    /// </summary>
    const float PLAYED_THREE_BAND = 0.1f;

    /// <summary>
    /// How much of their strength the beat grid's lines keep over the part played, darkened in three bands.
    /// </summary>
    const float PLAYED_GRID = 0.3f;

    // Whether the part played is drawn darker, as it is in three bands, for the grid's lines over it to be too.
    bool _darkenPlayed;

    /// <summary>
    /// How sharply the weaker bands fade beside the strongest in three bands: more than the scrolling waveforms'
    /// (see <see cref="ScrollingWaveform.ThreeBand"/>), since a column here covers a long stretch, where the bands
    /// even out and would come out nearly white.
    /// </summary>
    const float OVERVIEW_CONTRAST = 6;

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

    /// <summary>
    /// Where to shade from to the end, in <see cref="WarningColor"/>, for a song about to run out; none if null.
    /// </summary>
    public float? WarningFrom { get; set; }

    public float4 WarningColor { get; set; } = Theme.Warning;

    /// <summary>
    /// A second, fainter playhead, as a value between <see cref="Slider.Min"/> and <see cref="Slider.Max"/>: where a
    /// slipping deck's song has got to underneath; none if null.
    /// </summary>
    public float? Ghost { get; set; }

    public float4 GhostColor { get; set; } = Colors.Hex(0xFFFFFF, 0.6f);

    /// <summary>
    /// Beats pinned by hand, as values between <see cref="Slider.Min"/> and <see cref="Slider.Max"/>: each a line
    /// in <see cref="PinColor"/> with a block at its top and bottom.
    /// </summary>
    public IReadOnlyList<double> Pins { get; set; } = [];

    /// <summary>
    /// The song's memory cues, in seconds: each a faint line in <see cref="MemoryCueColor"/> with a small triangle at
    /// the bottom, as Rekordbox marks them.
    /// </summary>
    public IReadOnlyList<TimeSpan> MemoryCues { get; set; } = [];

    public float4 MemoryCueColor { get; set; } = Colors.Hex(0xE5484D);

    public float4 PinColor { get; set; } = Colors.White;

    public Font? FlagFont { get; set; }

    public float4 GridColor { get; set; } = Colors.Hex(0xFFFFFF, 0.14f);
    public float4 StrongGridColor { get; set; } = Colors.Hex(0xFFFFFF, 0.55f);

    public float4 PlayedColor { get; set; } = Colors.Hex(0xEB7828);
    public float4 UnplayedColor { get; set; } = Colors.Hex(0xFFFFFF, 0.35f);
    public float4 BackgroundColor { get; set; } = Colors.Hex(0x000000, 0.25f);

    // Each column's height and colour in three bands, worked out once for a song at a size: they don't change as it
    // plays, and looking through the bands for every column takes a good part of a frame.
    float[] _columnHeights = [];
    float4[] _columnBands = [];
    (Waveform? Waveform, DetailWaveform? Bands, int Columns, float Tallest, float Min, float Max) _columnsFor;

    void PrepareColumns(Waveform waveform, DetailWaveform? bands, int columns, float tallest)
    {
        var wanted = (waveform, bands, columns, tallest, Min, Max);
        if (_columnsFor == wanted)
            return;
        _columnsFor = wanted;
        _columnHeights = new float[columns];
        _columnBands = bands is null ? [] : new float4[columns];
        for (var column = 0; column < columns; column++)
        {
            var peak = waveform.PeakBetween(column / Travel, (column + 1) / Travel);
            _columnHeights[column] = MathF.Max(1, MathF.Round(peak * tallest));
            if (bands is null)
                continue;
            // The column's stretch of the song, in seconds, and its bands weighted as the scrolling waveforms weight
            // them, treble being far quieter than bass in energy for how much it's heard.
            var (_, bass, mids, treble) = bands.Between(Min + column / Travel * (Max - Min), Min + (column + 1) / Travel * (Max - Min));
            _columnBands[column] = ScrollingWaveform.ThreeBand(bass, mids * 1.5f, treble * 2.5f, OVERVIEW_CONTRAST);
        }
    }

    protected override void DrawTrack(Batch batch)
    {
        if (Waveform is not { } waveform || IsVertical)
        {
            base.DrawTrack(batch);
            return;
        }

        batch.AddRect(ScreenPosition, Size, Faded(BackgroundColor));

        // One column per pixel of the handle's travel, centred on the handle's middle at each point, standing up
        // from the bottom. Quiet stretches keep a pixel of height so the song's extent still shows.
        var left = ScreenPosition.X + HandleLength / 2;
        var bottom = ScreenPosition.Y + Size.Y;
        var tallest = Size.Y - 1;
        var columns = (int)MathF.Ceiling(Travel);
        var playhead = OffsetOf(Fraction);
        var played = Faded(PlayedColor);
        var unplayed = Faded(UnplayedColor);
        var bands = Colours == WaveformColours.ThreeBand && Detail is not null && Max > Min ? Detail : null;
        _darkenPlayed = bands is not null;
        PrepareColumns(waveform, bands, columns, tallest);
        for (var column = 0; column < columns; column++)
        {
            var height = _columnHeights[column];
            var isPlayed = column + 0.5f < playhead;
            var color = isPlayed ? played : unplayed;
            if (bands is not null)
            {
                var band = _columnBands[column];
                color = Faded(isPlayed ? band * new float4(PLAYED_THREE_BAND, PLAYED_THREE_BAND, PLAYED_THREE_BAND, 1) : band);
            }
            batch.AddRect(new float2(left + column, bottom - height), new float2(1, height), color);
        }

        DrawRegion(batch, left);
        DrawWarning(batch, left);
        DrawGrid(batch, left);
        DrawPins(batch, left);
        DrawMemoryCues(batch, left);
        DrawFlags(batch, left);
        DrawGhost(batch, left);
    }

    void DrawGhost(Batch batch, float left)
    {
        if (Ghost is not { } ghost || Max <= Min)
            return;
        var x = MathF.Round(left + OffsetOf(Math.Clamp((ghost - Min) / (Max - Min), 0, 1)));
        // Dashed, so it doesn't pass for the playhead.
        for (var y = 0f; y < Size.Y; y += 6)
            batch.AddRect(new float2(x, ScreenPosition.Y + y), new float2(2, MathF.Min(3, Size.Y - y)), Faded(GhostColor));
    }

    void DrawWarning(Batch batch, float left)
    {
        if (WarningFrom is not { } from || Max <= Min)
            return;
        var start = MathF.Round(left + OffsetOf(Math.Clamp((from - Min) / (Max - Min), 0, 1)));
        var end = MathF.Round(left + OffsetOf(1));
        if (end > start)
            batch.AddRect(new float2(start, ScreenPosition.Y), new float2(end - start, Size.Y), Faded(Theme.Fade(WarningColor, 0.35f)));
    }

    void DrawMemoryCues(Batch batch, float left)
    {
        if (Max <= Min)
            return;
        var bottom = ScreenPosition.Y + Size.Y;
        foreach (var cue in MemoryCues)
        {
            var x = MathF.Round(left + OffsetOf(Math.Clamp(((float)cue.TotalSeconds - Min) / (Max - Min), 0, 1)));
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(1, Size.Y), Faded(Theme.Fade(MemoryCueColor, 0.6f)));
            // A triangle pointing up at it, 7 pixels across.
            for (var row = 0; row < 4; row++)
                batch.AddRect(new float2(x - row, bottom - 4 + row), new float2(2 * row + 1, 1), Faded(MemoryCueColor));
        }
    }

    void DrawPins(Batch batch, float left)
    {
        if (Max <= Min)
            return;
        foreach (var pin in Pins)
        {
            var x = MathF.Round(left + OffsetOf(Math.Clamp(((float)pin - Min) / (Max - Min), 0, 1)));
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(1, Size.Y), Faded(Theme.Fade(PinColor, 0.8f)));
            batch.AddRect(new float2(x - 2, ScreenPosition.Y), new float2(5, 3), Faded(PinColor));
            batch.AddRect(new float2(x - 2, ScreenPosition.Y + Size.Y - 3), new float2(5, 3), Faded(PinColor));
        }
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

        // Counted from the song's first downbeat, so the same lines show whatever the zoom, bars stay on bars, and the
        // brighter lines mark phrases.
        var offset = grid.DownbeatOffset;
        for (var beat = Math.Ceiling((first - offset) / interval) * interval + offset; beat <= last; beat += interval)
            yield return (beat, ((long)Math.Round((beat - offset) / interval) % 4 + 4) % 4 == 0);
    }

    void DrawGrid(Batch batch, float left)
    {
        if (Grid is not { } grid)
            return;

        var normal = Faded(GridColor);
        var strong = Faded(StrongGridColor);
        // Over the part played, darkened, they're dimmer too, so the bars and phrases don't stand out of it.
        var dim = new float4(1, 1, 1, PLAYED_GRID);
        var playhead = left + OffsetOf(Fraction);
        foreach (var (beat, isStrong) in GridLines())
        {
            var fraction = (float)((grid.TimeOfBeat(beat) - Min) / (Max - Min));
            var x = MathF.Round(left + OffsetOf(fraction));
            var color = isStrong ? strong : normal;
            if (_darkenPlayed && x + 0.5f < playhead)
                color *= dim;
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(1, Size.Y), color);
        }
    }
}
