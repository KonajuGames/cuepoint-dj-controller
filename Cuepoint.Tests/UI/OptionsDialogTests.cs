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
        Assert.Equal(11, dropdowns.Count);
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

    [Fact]
    public void Choosing_a_buffer_size_reopens_the_output_with_it_and_says_so()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        using var engine = new AudioEngine();
        var options = new OptionsDialog(engine, "C:\\Music", "C:\\library.json", font, font, font);
        var size = Within(options).OfType<Dropdown<int?>>().Single();
        var changed = 0;
        options.BufferSizeChanged += () => changed++;

        Assert.Equal(new int?[] { null, 64, 128, 256, 512, 1024, 2048 }, size.Items());
        Assert.Null(engine.BufferFrames);
        size.KeyDown(Key.Down, default);

        Assert.Equal(64, engine.BufferFrames);
        Assert.Equal(1, changed);
        // Whatever the driver gave, there's still an output, and the dialog says what it comes to.
        Assert.NotNull(engine.OutputBuffer);
        Assert.Contains("ms a buffer", OptionsDialog.LatencyText(engine.OutputBuffer));
    }

    [Fact]
    public void The_latency_says_how_long_a_buffer_lasts()
    {
        Assert.Equal("10.0 ms a buffer (480 samples at 48 kHz)", OptionsDialog.LatencyText((480, 48000)));
        Assert.Equal("5.8 ms a buffer (256 samples at 44.1 kHz)", OptionsDialog.LatencyText((256, 44100)));
        Assert.Equal(
            "42.7 ms a buffer (the device's 10.0 ms, 480 samples at 48 kHz, and 32.7 ms mixed ahead)",
            OptionsDialog.LatencyText((480, 48000), 1568)
        );
        Assert.Equal("No output open", OptionsDialog.LatencyText(null, 1568));
    }

    [Fact]
    public void A_size_bigger_than_the_device_opened_with_is_made_up_by_mixing_ahead()
    {
        using var engine = new AudioEngine(2048);

        // However big this machine's device buffer is, the mix ahead makes up the rest, or there's none to make up.
        Assert.Equal(2048, engine.BufferFrames);
        Assert.Equal(AudioOutput.MixAheadFor(2048, engine.OutputBuffer), engine.OutputMixAhead);
        Assert.Equal(engine.OutputMixAhead / (double)AudioOutput.RATE, engine.MixAheadSeconds);

        // The device plays from it for a while without ever finding it short.
        Thread.Sleep(500);
        Assert.Equal(0, engine.OutputShortfalls);

        // And back to the device's own: nothing mixed ahead.
        Assert.True(engine.UseBufferSize(null));
        Assert.Equal(0, engine.OutputMixAhead);
    }
}
