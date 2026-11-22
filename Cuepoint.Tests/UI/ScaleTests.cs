using Brutal.Numerics;

namespace Cuepoint.Tests.UI;

/// <summary>
/// Scaling the whole screen: text rasterised bigger but measured the same, and everything drawn bigger on whole
/// pixels.
/// </summary>
public class ScaleTests
{
    static (FontAtlas Atlas, Font Font) Atlas(float scale)
    {
        var atlas = new FontAtlas { Scale = scale };
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        atlas.Build();
        return (atlas, font);
    }

    [Fact]
    public void Text_rasterised_at_twice_the_size_measures_about_the_same_in_the_UI_s_units()
    {
        var (_, plain) = Atlas(1);
        var (_, doubled) = Atlas(2);

        Assert.Equal(plain.PixelSize, doubled.PixelSize);
        Assert.InRange(doubled.LineHeight, plain.LineHeight - 1, plain.LineHeight + 1);
        var text = "Northern Static, 124.00 BPM";
        Assert.InRange(doubled.Measure(text).X, plain.Measure(text).X * 0.95f, plain.Measure(text).X * 1.05f);
        // Each glyph's half the size of its bitmap in the atlas.
        var glyph = doubled.GetGlyph('H');
        Assert.Equal(glyph.Size.X * 2, MathF.Round(glyph.Size.X * 2));
    }

    [Fact]
    public void Drawn_at_one_and_a_half_times_a_rectangle_lands_on_whole_pixels()
    {
        var (atlas, _) = Atlas(1.5f);
        var batch = new Batch(atlas) { Scale = 1.5f };

        batch.AddRect(new float2(1, 1), new float2(10, 1), Colors.White);

        var corners = batch.Vertices.ToArray().Select(vertex => vertex.Position).ToList();
        Assert.Contains(new float2(2, 2), corners);
        Assert.Contains(new float2(17, 4), corners);
        Assert.All(corners, corner => Assert.Equal(corner, new float2(MathF.Round(corner.X), MathF.Round(corner.Y))));
    }

    [Fact]
    public void At_a_scale_of_one_nothing_moves()
    {
        var (atlas, _) = Atlas(1);
        var batch = new Batch(atlas);

        batch.AddRect(new float2(1.25f, 1), new float2(10, 1), Colors.White);

        Assert.Contains(new float2(1.25f, 1), batch.Vertices.ToArray().Select(vertex => vertex.Position));
    }
}
