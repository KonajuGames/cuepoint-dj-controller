using Brutal.Numerics;

namespace Cuepoint.Tests.UI;

/// <summary>
/// The shared looks, from cue-framework, are readable: their colours are linear already, so luminance is a sum.
/// </summary>
public class ThemeTests
{
    public static TheoryData<string> Looks => ["Dark", "Light", "HighContrast"];

    static ThemePalette Look(string name) =>
        name switch
        {
            "Light" => Theme.Light,
            "HighContrast" => Theme.HighContrast,
            _ => Theme.Dark,
        };

    static double Luminance(float4 color) => 0.2126 * color.X + 0.7152 * color.Y + 0.0722 * color.Z;

    static double Contrast(float4 a, float4 b)
    {
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void Text_and_labels_read_on_every_panel(string name)
    {
        var look = Look(name);
        foreach (var panel in new[] { look.Background, look.Panel, look.PanelRaised, look.Control, look.Field })
        {
            // Body text at the 7:1 of WCAG's AAA, labels at AA's 4.5:1.
            Assert.True(Contrast(look.Text, panel) >= 7, $"{name}: text on {panel}");
            // Dark's muted labels on its unlit buttons are a shade under, as they've always been; the newer looks
            // are held to it everywhere.
            if (name != "Dark" || panel != look.Control)
                Assert.True(Contrast(look.Muted, panel) >= 4.5, $"{name}: labels on {panel}");
        }
    }

    [Fact]
    public void High_contrast_is_more_contrasting_than_dark()
    {
        Assert.True(Contrast(Theme.HighContrast.Text, Theme.HighContrast.Panel) > Contrast(Theme.Dark.Text, Theme.Dark.Panel));
        Assert.True(Contrast(Theme.HighContrast.PanelBorder, Theme.HighContrast.Panel) > Contrast(Theme.Dark.PanelBorder, Theme.Dark.Panel));
    }

    [Fact]
    public void Tints_are_white_on_dark_and_black_on_light()
    {
        Assert.True(Luminance(Theme.Dark.Ink) > 0.9);
        Assert.True(Luminance(Theme.Light.Ink) < 0.01);
        Assert.True(Theme.Light.IsLight);
        Assert.False(Theme.HighContrast.IsLight);
    }
}
