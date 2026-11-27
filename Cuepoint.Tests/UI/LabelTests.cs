namespace Cuepoint.Tests.UI;

public class LabelTests
{
    [Fact]
    public void A_reading_with_a_min_width_keeps_one_width_as_it_gains_or_loses_digits()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        // Glyphs only have their widths once the atlas is laid out.
        atlas.Build();
        var label = new Label(font) { MinWidth = MathF.Ceiling(font.Measure("CPU 100%").X) };

        var widths = new[] { "CPU 5%", "CPU 25%", "CPU 100%" }.Select(text =>
        {
            label.Text = text;
            return label.Measure().X;
        }).ToList();

        Assert.Single(widths.Distinct());
        Assert.True(new Label(font, "CPU 5%").Measure().X < label.MinWidth);
    }
}
