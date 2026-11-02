using Brutal.Numerics;
using Brutality.UI;

namespace Brutality.Tests.UI;

public class WaveformOptionsTests
{
    [Fact]
    public void Three_band_colours_show_which_band_is_strongest()
    {
        var kick = ScrollingWaveform.ThreeBand(bass: 1, mid: 0.1f, high: 0.05f);
        var vocal = ScrollingWaveform.ThreeBand(bass: 0.1f, mid: 1, high: 0.2f);
        var hats = ScrollingWaveform.ThreeBand(bass: 0, mid: 0.1f, high: 1);
        var everything = ScrollingWaveform.ThreeBand(bass: 1, mid: 1, high: 1);

        Assert.True(kick.X > kick.Y && kick.X > kick.Z);
        Assert.True(vocal.Y > vocal.X && vocal.Y > vocal.Z);
        Assert.True(hats.Z > hats.X && hats.Z > hats.Y);
        Assert.Equal(new float4(1, 1, 1, 1), everything);
    }

    [Fact]
    public void A_band_well_ahead_of_the_others_is_a_strong_colour_not_a_pastel()
    {
        // Mostly bass, with some mids and a little treble, as a kick drum is.
        var kick = ScrollingWaveform.ThreeBand(bass: 1, mid: 0.5f, high: 0.3f);

        Assert.Equal(1, kick.X);
        Assert.True(kick.Y < 0.25f, $"green {kick.Y}");
        Assert.True(kick.Z < 0.1f, $"blue {kick.Z}");
    }

    [Fact]
    public void Silence_has_a_colour_too()
    {
        var silence = ScrollingWaveform.ThreeBand(0, 0, 0);

        Assert.False(float.IsNaN(silence.X));
        Assert.Equal(1, silence.W);
    }

    [Fact]
    public void Waveform_settings_come_back_as_they_were_saved()
    {
        using var folder = new TempFolder();
        var path = folder["settings.json"];
        new AppSettings { WaveformSeconds = 7.5f, WaveformColours = nameof(WaveformColours.ThreeBand), WaveformBeatGrid = false, TallWaveforms = true }.Save(path);

        var loaded = AppSettings.Load(path);

        Assert.Equal(7.5f, loaded.WaveformSeconds);
        Assert.Equal("ThreeBand", loaded.WaveformColours);
        Assert.False(loaded.WaveformBeatGrid);
        Assert.True(loaded.TallWaveforms);
    }

    [Fact]
    public void Waveform_settings_start_as_they_always_were()
    {
        var settings = new AppSettings();

        Assert.Equal(12, settings.WaveformSeconds);
        Assert.Null(settings.WaveformColours);
        Assert.True(settings.WaveformBeatGrid);
        Assert.False(settings.TallWaveforms);
    }

    /// <summary>
    /// An element that takes the mouse wheel, as a waveform does to zoom.
    /// </summary>
    sealed class Wheeled : Element
    {
        public float Turned;
        public override bool IsInteractive => true;
        public override float2 Measure() => Size = new float2(100, 50);
        public override void Draw(Batch batch) { }

        public override bool Wheel(float2 notches)
        {
            Turned += notches.Y;
            return true;
        }
    }

    [Fact]
    public void The_wheel_goes_first_to_what_is_under_the_mouse()
    {
        var root = new Root();
        var scroll = root.Add(new ScrollPanel { MaxHeight = 60 });
        var wheeled = scroll.Add(new Wheeled());
        scroll.Add(new Wheeled());
        root.Layout(new float2(800, 600));
        root.MouseMove(new float2(10, 10));

        Assert.True(root.MouseWheel(new float2(0, 2)));

        Assert.Equal(2, wheeled.Turned);
        Assert.Equal(0, scroll.ScrollOffset.Y);
    }
}
