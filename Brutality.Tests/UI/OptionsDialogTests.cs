using Brutality.Audio;
using Brutality.UI;

namespace Brutality.Tests.UI;

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
        Assert.Equal(3, dropdowns.Count);
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
}
