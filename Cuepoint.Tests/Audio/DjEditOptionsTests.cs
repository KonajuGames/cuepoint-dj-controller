using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Audio.Stems;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// DJ edits made as asked: their ends' lengths, what changes halfway, and how the beats give way to the song, take
/// over from it, and end.
/// </summary>
public class DjEditOptionsTests
{
    const int RATE = 44100;
    const int CHANNELS = 2;
    const double BPM = 120;
    const double BEAT = 60 / BPM;
    const double BAR = 4 * BEAT;

    /// <summary>
    /// A song at 120 BPM: a kick at 60 Hz on every beat in bars <paramref name="drums"/>, a vocal at 440 Hz all
    /// through, bass at 55 Hz and pads at 330 Hz once the drums start, and nothing before its first bar.
    /// </summary>
    static (PcmAudio Song, SongStems Stems, BeatGrid Grid) MakeSong(Range drums, int bars = 40)
    {
        var duration = (bars + 1) * BAR;
        var frames = (int)(duration * RATE);
        var (v, d, b, song) = (new float[frames * CHANNELS], new float[frames * CHANNELS], new float[frames * CHANNELS], new float[frames * CHANNELS]);
        var (drumFrom, drumTo) = (drums.Start.GetOffset(bars), drums.End.GetOffset(bars));
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / RATE;
            var bar = (int)(t / BAR);
            var sinceBeat = t % BEAT;
            var playing = bar < bars;
            var drum = playing && bar >= drumFrom && bar < drumTo ? 0.5f * MathF.Sin((float)(2 * Math.PI * 60 * sinceBeat)) * MathF.Exp((float)(-sinceBeat / 0.06)) : 0f;
            var vocal = playing ? 0.2f * MathF.Sin((float)(2 * Math.PI * 440 * t)) : 0f;
            var bass = playing && bar >= drumFrom ? 0.2f * MathF.Sin((float)(2 * Math.PI * 55 * t)) : 0f;
            var pads = playing && bar >= drumFrom ? 0.05f * MathF.Sin((float)(2 * Math.PI * 330 * t)) : 0f;
            for (var c = 0; c < CHANNELS; c++)
            {
                var at = i * CHANNELS + c;
                (v[at], d[at], b[at]) = (vocal, drum, bass);
                song[at] = vocal + drum + bass + pads;
            }
        }
        return (new PcmAudio(song, CHANNELS, RATE), new SongStems(v, d, b, CHANNELS, RATE), BeatGrid.Constant(BPM, 0, duration));
    }

    static DjEditResult Make(DjEditOptions options, Range? drums = null)
    {
        var (song, stems, grid) = MakeSong(drums ?? ..);
        var (edit, why) = DjEdit.Make(song, stems, grid, options);
        Assert.Null(why);
        return edit!;
    }

    // How strong a frequency is in the left channel from `from` for `seconds`, as an amplitude.
    static double Level(DjEditResult edit, double hz, double from, double seconds)
    {
        var samples = edit.Audio.Samples;
        var (start, n) = ((int)(from * RATE), (int)(seconds * RATE));
        var (re, im) = (0.0, 0.0);
        for (var i = 0; i < n && (start + i) * CHANNELS < samples.Length; i++)
        {
            var phase = 2 * Math.PI * hz * i / RATE;
            (re, im) = (re + samples[(start + i) * CHANNELS] * Math.Cos(phase), im + samples[(start + i) * CHANNELS] * Math.Sin(phase));
        }
        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    static double Kick(DjEditResult edit, double from) => Level(edit, 60, from, BAR);

    [Fact]
    public void The_ends_are_as_long_as_asked()
    {
        var edit = Make(new DjEditOptions { IntroBars = 8, OutroBars = 8 });

        Assert.Equal(8 * BAR, edit.IntroEnd!.Value, 3);
        Assert.Contains("8 bars of beats before it", edit.Summary);
        Assert.Contains("8 bars after it", edit.Summary);
        Assert.Equal(edit.OutroStart!.Value + 8 * BAR + BEAT, (double)edit.Audio.Frames / RATE, 2);
    }

    [Fact]
    public void Nothing_comes_in_halfway_if_asked()
    {
        var edit = Make(new DjEditOptions { IntroChange = DjEditChange.Nothing });

        Assert.True(Level(edit, 55, 11 * BAR, 2 * BAR) < 0.01);
        Assert.Contains("beats alone", edit.Summary);
    }

    [Fact]
    public void Bass_and_instruments_come_in_together_if_asked()
    {
        var edit = Make(new DjEditOptions { IntroChange = DjEditChange.BassAndInstruments });

        Assert.True(Level(edit, 55, 3 * BAR, 2 * BAR) < 0.01);
        Assert.True(Level(edit, 55, 11 * BAR, 2 * BAR) > 0.1);
        Assert.True(Level(edit, 330, 11 * BAR, 2 * BAR) > 0.03);
        Assert.Contains("its bass and instruments coming in after 8", edit.Summary);
    }

    [Fact]
    public void A_fade_across_the_songs_start_carries_the_beats_into_it_and_away()
    {
        // The song's own drums don't start until its sixth bar.
        var edit = Make(new DjEditOptions { IntroEnd = DjEditTransition.Fade, IntroEndBeats = 8, IntroEndAcrossSong = true }, 5..);
        var downbeat = edit.IntroEnd!.Value;

        Assert.True(Kick(edit, downbeat - BAR) > 0.03);
        Assert.True(Kick(edit, downbeat) > 0.003, $"{Kick(edit, downbeat)}");
        Assert.True(Kick(edit, downbeat) < Kick(edit, downbeat - BAR));
        Assert.True(Kick(edit, downbeat + 2 * BAR) < 0.001, $"{Kick(edit, downbeat + 2 * BAR)}");
        Assert.Contains("fading out over 2 bars into its start", edit.Summary);
    }

    [Fact]
    public void A_fade_before_the_song_is_over_by_its_first_downbeat()
    {
        var edit = Make(new DjEditOptions { IntroEnd = DjEditTransition.Fade, IntroEndBeats = 8 }, 5..);
        var downbeat = edit.IntroEnd!.Value;

        Assert.True(Kick(edit, downbeat - 4 * BAR) > 0.03);
        Assert.True(Kick(edit, downbeat - BAR) < Kick(edit, downbeat - 4 * BAR) / 2);
        Assert.True(Kick(edit, downbeat) < 0.001);
        Assert.Contains("before it starts", edit.Summary);
    }

    [Fact]
    public void A_filter_sweep_takes_the_beats_away_too()
    {
        var edit = Make(new DjEditOptions { IntroEnd = DjEditTransition.Filter, IntroEndBeats = 8, IntroEndAcrossSong = true }, 5..);
        var downbeat = edit.IntroEnd!.Value;

        Assert.True(Kick(edit, downbeat) > 0.003);
        Assert.True(Kick(edit, downbeat + 2 * BAR) < 0.001);
        Assert.Contains("filtering out over 2 bars", edit.Summary);
    }

    [Fact]
    public void An_echo_rings_on_over_the_songs_start_for_as_long_as_asked()
    {
        var edit = Make(new DjEditOptions { IntroEnd = DjEditTransition.Echo, IntroEndBeats = 8, IntroEndAcrossSong = true, EchoBeats = 1 }, 5..);
        // The same beats stopping dead on the song's first downbeat, as the echo's do: what's left is the echo.
        var stopped = Make(new DjEditOptions { IntroEnd = DjEditTransition.Cut, IntroEndBeats = 1 }, 5..);
        var downbeat = edit.IntroEnd!.Value;
        double Echo(double from)
        {
            var (start, n) = ((int)(from * RATE) * CHANNELS, (int)(BAR * RATE) * CHANNELS);
            return Math.Sqrt(Enumerable.Range(start, n).Average(i => Math.Pow(edit.Audio.Samples[i] - stopped.Audio.Samples[i], 2)));
        }

        Assert.True(Echo(downbeat) > 0.003, $"{Echo(downbeat)}");
        Assert.True(Echo(downbeat + BAR) < Echo(downbeat));
        Assert.True(Echo(downbeat + 2 * BAR) < Echo(downbeat) / 10, $"{Echo(downbeat + 2 * BAR)}");
        Assert.True(Echo(downbeat - 2 * BAR) < 1e-6);
        Assert.Contains("echoing out over 2 bars", edit.Summary);
    }

    [Fact]
    public void A_cut_stops_the_beats_dead_where_asked()
    {
        var edit = Make(new DjEditOptions { IntroEnd = DjEditTransition.Cut, IntroEndBeats = 4, IntroEndAcrossSong = true }, 5..);
        var downbeat = edit.IntroEnd!.Value;

        Assert.True(Kick(edit, downbeat) > 0.03);
        Assert.True(Kick(edit, downbeat + BAR) < 0.001);
    }

    [Fact]
    public void The_outro_can_come_in_under_the_songs_end()
    {
        var plain = Make(DjEditOptions.Default, ..30);
        var under = Make(new DjEditOptions { OutroStart = DjEditTransition.Fade, OutroStartBeats = 8, OutroStartUnderSong = true }, ..30);

        // The song's drums stop after bar 30; the outro's beats are already coming in under its last bars.
        Assert.Equal(plain.OutroStart!.Value, under.OutroStart!.Value, 3);
        var start = under.OutroStart.Value;
        Assert.True(Level(under, 60, start - BAR, BAR) > Level(plain, 60, start - BAR, BAR) * 1.2);
        Assert.Contains("fading in over 2 bars under its end", under.Summary);
    }

    [Fact]
    public void The_outro_can_fade_away_at_its_end()
    {
        var edit = Make(new DjEditOptions { OutroEnd = DjEditTransition.Fade, OutroEndBeats = 16 }, ..30);
        var end = (double)edit.Audio.Frames / RATE;

        Assert.True(Kick(edit, end - BAR) < Kick(edit, end - 6 * BAR) / 4);
        Assert.Contains("fading out over the last 4 bars", edit.Summary);
    }

    [Fact]
    public void An_outro_echoing_out_rings_on_past_its_end()
    {
        var plain = Make(DjEditOptions.Default, ..30);
        var echo = Make(new DjEditOptions { OutroEnd = DjEditTransition.Echo, OutroEndBeats = 8 }, ..30);

        Assert.Equal((double)plain.Audio.Frames / RATE - BEAT + 2 * BAR, (double)echo.Audio.Frames / RATE, 2);
        Assert.True(Kick(echo, (double)echo.Audio.Frames / RATE - 2 * BAR) > 0.001);
    }

    [Fact]
    public void Options_out_of_range_are_brought_into_it()
    {
        var options = new DjEditOptions { IntroBars = 7, OutroBars = 1000, IntroEndBeats = 0, EchoBeats = 0.1 }.Checked();

        Assert.Equal((4, 64, 1, 0.5), (options.IntroBars, options.OutroBars, options.IntroEndBeats, options.EchoBeats));
    }
}
