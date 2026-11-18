using Cuepoint.Audio;
using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

/// <summary>
/// The options dialog built for real, with an audio engine and a system font, as the app builds it.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public class OptionsDialogTests
{
    static IEnumerable<Element> Within(Element element) =>
        element is Panel panel ? panel.Children.SelectMany(Within).Prepend(element) : [element];

    [Fact]
    public void Every_dropdown_can_open_its_list()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        using var engine = new AudioEngine();
        var options = new OptionsDialog(engine, "C:\\Music", "C:\\library.json", font, font, font);
        var menu = new ContextMenu(font);

        options.Menu = menu;

        var dropdowns = Within(options).OfType<IOpensMenu>().ToList();
        Assert.Equal(4, dropdowns.Count);
        Assert.All(dropdowns, dropdown => Assert.Same(menu, dropdown.Menu));
    }

    [Fact]
    public void The_waveform_colours_list_has_both_choices()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        using var engine = new AudioEngine();
        var options = new OptionsDialog(engine, "C:\\Music", "C:\\library.json", font, font, font);

        Assert.Equal(new[] { WaveformColours.Deck, WaveformColours.ThreeBand }, options.WaveformColours.Items());
    }

    [Fact]
    public void Choosing_a_crossfader_curve_sets_the_engine_and_says_so()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        using var engine = new AudioEngine();
        var options = new OptionsDialog(engine, "C:\\Music", "C:\\library.json", font, font, font);
        var curve = Within(options).OfType<Dropdown<CrossfaderCurve>>().Single();
        var changed = 0;
        options.CrossfaderCurveChanged += () => changed++;

        Assert.Equal(new[] { CrossfaderCurve.Smooth, CrossfaderCurve.Sharp }, curve.Items());
        curve.KeyDown(Key.Down, default);

        Assert.Equal(CrossfaderCurve.Sharp, engine.CrossfaderCurve);
        Assert.Equal(1, changed);
    }
}
