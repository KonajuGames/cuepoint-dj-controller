using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Audio.Stems;

namespace Cuepoint.Tests.Audio;

public class DjEditTests
{
    const int RATE = 44100;
    const int CHANNELS = 2;
    const double BPM = 120;
    const double BEAT = 60 / BPM;
    const double BAR = 4 * BEAT;
    const double FIRST_BEAT = 0.25;

    /// <summary>
    /// A made-up song at 120 BPM, its bars starting at a quarter of a second: kick on every beat, clap on two and
    /// four, hi-hats between, in bars <paramref name="drums"/>; a vocal in bars <paramref name="vocals"/>; bass and
    /// pads (unless not <paramref name="bass"/> or <paramref name="pads"/>) all through <paramref name="bars"/> bars
    /// once the drums start; and anything else in <paramref name="extra"/>, a stereo frame at a time from a time in
    /// seconds.
    /// </summary>
    static (PcmAudio Song, SongStems Stems, BeatGrid Grid) MakeSong(int bars, Range drums, Range vocals, int tailBars = 1, Func<double, float>? extra = null, int fadeBars = 0, bool bass = true, bool pads = true, int drumPickupBeats = 0)
    {
        var duration = FIRST_BEAT + (bars + tailBars) * BAR;
        var frames = (int)(duration * RATE);
        var (v, d, b, song) = (new float[frames * CHANNELS], new float[frames * CHANNELS], new float[frames * CHANNELS], new float[frames * CHANNELS]);
        var random = new Random(3);
        var (drumFrom, drumTo) = (drums.Start.GetOffset(bars), drums.End.GetOffset(bars));
        var (vocalFrom, vocalTo) = (vocals.Start.GetOffset(bars), vocals.End.GetOffset(bars));
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / RATE;
            var beats = (t - FIRST_BEAT) / BEAT;
            var bar = (int)Math.Floor(beats / 4);
            var sinceBeat = (beats - Math.Floor(beats)) * BEAT;
            var beatInBar = (int)Math.Floor(beats) % 4;
            var noise = (float)(random.NextDouble() * 2 - 1);

            var drum = 0f;
            // The drums play their bars, and with a pickup, the last beats of the bar before them.
            if (beats >= 0 && (bar >= drumFrom || bar == drumFrom - 1 && beatInBar >= 4 - drumPickupBeats) && bar < drumTo)
            {
                drum += 0.5f * MathF.Sin((float)(2 * Math.PI * 60 * sinceBeat)) * MathF.Exp((float)(-sinceBeat / 0.06));
                if (beatInBar is 1 or 3 && sinceBeat < 0.06)
                    drum += 0.3f * noise * (float)(1 - sinceBeat / 0.06);
                var sinceOff = sinceBeat - BEAT / 2;
                if (sinceOff is >= 0 and < 0.03)
                    drum += 0.15f * noise;
            }
            var vocal = beats >= 0 && bar >= vocalFrom && bar < vocalTo ? 0.2f * MathF.Sin((float)(2 * Math.PI * 440 * t)) : 0f;
            var low = bass && beats >= 0 && bar >= drumFrom && bar < bars ? 0.2f * MathF.Sin((float)(2 * Math.PI * 55 * t)) : 0f;
            var rest = (pads && beats >= 0 && bar >= drumFrom && bar < bars ? 0.05f * MathF.Sin((float)(2 * Math.PI * 330 * t)) : 0f) + (extra?.Invoke(t) ?? 0);
            // Everything fading away over the last bars, if it fades out.
            var fade = fadeBars > 0 ? (float)Math.Clamp((bars - beats / 4) / fadeBars, 0, 1) : 1;
            (vocal, drum, low, rest) = (vocal * fade, drum * fade, low * fade, rest * fade);
            for (var c = 0; c < CHANNELS; c++)
            {
                var at = i * CHANNELS + c;
                (v[at], d[at], b[at]) = (vocal, drum, low);
                song[at] = vocal + drum + low + rest;
            }
        }
        return (new PcmAudio(song, CHANNELS, RATE), new SongStems(v, d, b, CHANNELS, RATE), BeatGrid.Constant(BPM, FIRST_BEAT, duration));
    }

    static double Rms(float[] samples, double from, double to)
    {
        var (start, end) = ((int)(from * RATE) * CHANNELS, Math.Min(samples.Length, (int)(to * RATE) * CHANNELS));
        var sum = 0.0;
        for (var i = start; i < end; i++)
            sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, end - start));
    }

    [Fact]
    public void A_song_with_vocals_end_to_end_gets_sixteen_bars_of_beats_at_each_end()
    {
        var (song, stems, grid) = MakeSong(40, .., ..);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(why);
        Assert.NotNull(edit);
        // The intro's sixteen bars end on the song's first downbeat.
        Assert.Equal(16 * BAR - FIRST_BEAT, edit.Offset, 3);
        Assert.Equal(16 * BAR, edit.IntroEnd!.Value, 3);
        // Cued to the very start, the intro's first beat.
        Assert.Equal(0, edit.Cue);
        // Its drums play from the very start, on a downbeat.
        Assert.True(Rms(edit.Audio.Samples, 0, 0.1) > 0.1, $"{Rms(edit.Audio.Samples, 0, 0.1)} {Rms(edit.Audio.Samples, 0.1, 0.5)} {Rms(song.Samples, FIRST_BEAT, FIRST_BEAT + 0.1)} {edit.Summary}");
        Assert.True(Rms(edit.Audio.Samples, 4 * BAR, 6 * BAR) > 0.05);
        Assert.Equal(0, edit.Grid.BeatNumberAt(0) % 4, 3);
        Assert.True(edit.Grid.IsBarStart(Math.Round(edit.Grid.BeatNumberAt(0))));
        Assert.True(edit.Grid.IsBarStart(Math.Round(edit.Grid.BeatNumberAt(edit.IntroEnd!.Value))));
        Assert.False(edit.Grid.IsVariable);
        // Its marker's on the very first beat, so however few decimal places it's kept to, that's where its first beat is.
        Assert.Equal(0, edit.Grid.Markers[0].Time);
        Assert.Equal(0, edit.Grid.FirstBeat);
        Assert.Equal(BPM, edit.Grid.MainBpm, 6);

        // The outro starts where the drums stop, after the fortieth bar, and runs two whole phrases to a last hit.
        Assert.Equal(16 * BAR + 40 * BAR, edit.OutroStart!.Value, 3);
        Assert.True(Rms(edit.Audio.Samples, edit.OutroStart.Value + 2 * BAR, edit.OutroStart.Value + 4 * BAR) > 0.05);
        Assert.Equal(edit.OutroStart.Value + 16 * BAR + BEAT, (double)edit.Audio.Frames / RATE, 2);
    }

    [Fact]
    public void The_bass_comes_in_a_phrase_into_the_intro_and_drops_out_a_phrase_before_the_end()
    {
        var (song, stems, grid) = MakeSong(40, .., ..);

        var (edit, _) = DjEdit.Make(song, stems, grid);

        var samples = edit!.Audio.Samples;
        double Bass(double from) => Goertzel(samples.AsSpan((int)(from * RATE) * CHANNELS, (int)(2 * BAR * RATE) * CHANNELS).ToArray(), 55);
        Assert.Contains("its bass coming in after 8", edit.Summary);
        Assert.Contains("its bass dropping out for the last 8", edit.Summary);
        // Drums alone for the first eight bars, then the bass with them.
        Assert.True(Bass(3 * BAR) < 0.02, $"{Bass(3 * BAR)}");
        Assert.True(Bass(11 * BAR) > 0.1, $"{Bass(11 * BAR)}");
        // The bass carries on from the song, then the last eight bars are drums alone again.
        var outro = edit.OutroStart!.Value;
        Assert.True(Bass(outro + 3 * BAR) > 0.1, $"{Bass(outro + 3 * BAR)}");
        Assert.True(Bass(outro + 11 * BAR) < 0.02, $"{Bass(outro + 11 * BAR)}");
    }

    [Fact]
    public void Without_bass_the_instruments_come_in_instead()
    {
        var (song, stems, grid) = MakeSong(40, .., .., bass: false);

        var (edit, _) = DjEdit.Make(song, stems, grid);

        var samples = edit!.Audio.Samples;
        double Pads(double from) => Goertzel(samples.AsSpan((int)(from * RATE) * CHANNELS, (int)(2 * BAR * RATE) * CHANNELS).ToArray(), 330);
        Assert.Contains("its instruments coming in after 8", edit.Summary);
        Assert.True(Pads(3 * BAR) < 0.01, $"{Pads(3 * BAR)}");
        Assert.True(Pads(11 * BAR) > 0.03, $"{Pads(11 * BAR)}");
    }

    [Fact]
    public void With_nothing_but_drums_the_kick_drops_out_for_a_bar_instead()
    {
        var (song, stems, grid) = MakeSong(40, .., .., bass: false, pads: false);

        var (edit, _) = DjEdit.Make(song, stems, grid);

        var samples = edit!.Audio.Samples;
        double Kick(double from) => Goertzel(samples.AsSpan((int)(from * RATE) * CHANNELS, (int)(BAR * RATE) * CHANNELS).ToArray(), 60);
        Assert.Contains("the kick dropping out for bar 8", edit.Summary);
        Assert.Contains("the kick dropping out for a bar before the last 8", edit.Summary);
        // The eighth bar has its hi-hats and claps, but not its kick.
        Assert.True(Kick(7 * BAR) < Kick(6 * BAR) / 4, $"{Kick(7 * BAR)} against {Kick(6 * BAR)}");
        Assert.True(Rms(samples, 7 * BAR, 8 * BAR) > 0.01);
        Assert.True(Kick(8 * BAR) > Kick(6 * BAR) / 2);
        var outro = edit.OutroStart!.Value;
        Assert.True(Kick(outro + 7 * BAR) < Kick(outro + 5 * BAR) / 4, $"{Kick(outro + 7 * BAR)} against {Kick(outro + 5 * BAR)}");
    }

    [Fact]
    public void The_song_itself_is_untouched_between_the_new_ends()
    {
        var (song, stems, grid) = MakeSong(40, .., ..);

        var (edit, _) = DjEdit.Make(song, stems, grid);

        var shift = (long)Math.Round(edit!.Offset * RATE);
        for (var frame = (long)(5 * BAR * RATE); frame < (long)(35 * BAR * RATE); frame += 101)
        {
            for (var c = 0; c < CHANNELS; c++)
                Assert.Equal(song.Samples[frame * CHANNELS + c], edit.Audio.Samples[(frame + shift) * CHANNELS + c]);
        }
    }

    [Fact]
    public void The_intro_is_the_songs_own_drums()
    {
        var (song, stems, grid) = MakeSong(40, .., ..);

        var (edit, _) = DjEdit.Make(song, stems, grid);

        // The kicks on the first and third beats of a bar of the intro, past the first seam, are the song's, up to
        // the hi-hat between.
        var intro = edit!.Audio.Samples;
        foreach (var beat in new[] { 0, 2 })
        {
            var (introAt, songAt) = ((int)((2 * BAR + beat * BEAT) * RATE), (int)((FIRST_BEAT + 10 * BAR + beat * BEAT) * RATE));
            for (var i = 10; i < (int)(BEAT / 2 * RATE) - 10; i += 7)
                Assert.True(MathF.Abs(intro[(introAt + i) * CHANNELS] - stems.Drums[(songAt + i) * CHANNELS]) < 1e-3f, $"Beat {beat}, frame {i}: {intro[(introAt + i) * CHANNELS]} not {stems.Drums[(songAt + i) * CHANNELS]}");
        }
    }

    [Fact]
    public void A_song_that_starts_and_ends_on_beats_alone_has_nothing_to_add()
    {
        var (song, stems, grid) = MakeSong(40, .., 16..24);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(edit);
        Assert.Contains("each end already", why);
    }

    [Fact]
    public void Only_the_end_without_beats_alone_gets_them()
    {
        var (song, stems, grid) = MakeSong(40, .., 16..);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(why);
        Assert.Equal(0, edit!.Offset);
        Assert.Null(edit.IntroEnd);
        Assert.NotNull(edit.OutroStart);
        // Cued to the song's first beat, where it's first heard, not to the outro.
        Assert.Equal(FIRST_BEAT, edit.Cue, 2);
        for (var i = 0; i < (int)(30 * BAR * RATE) * CHANNELS; i += 97)
            Assert.Equal(song.Samples[i], edit.Audio.Samples[i]);
    }

    [Fact]
    public void A_song_that_carries_on_past_its_drums_is_heard_out_before_the_outros_beats()
    {
        // The drums stop after bar 34, but the vocals carry on to bar 40: the outro comes in on the phrase line there.
        var (song, stems, grid) = MakeSong(40, ..34, ..);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(why);
        Assert.Equal(edit!.Offset + FIRST_BEAT + 40 * BAR, edit.OutroStart!.Value, 3);
        Assert.Contains("16 bars after it", edit.Summary);
        // The song's own ending is as it was, without beats under it.
        var shift = (long)Math.Round(edit.Offset * RATE);
        for (var frame = (long)((FIRST_BEAT + 34 * BAR + 0.1) * RATE); frame < (long)((FIRST_BEAT + 40 * BAR - 0.1) * RATE); frame += 101)
            Assert.Equal(song.Samples[frame * CHANNELS], edit.Audio.Samples[(frame + shift) * CHANNELS]);
    }

    [Fact]
    public void A_quiet_lead_in_comes_before_the_first_downbeat()
    {
        // A soft bar of something before the song proper: its phrases are counted from the bar after.
        var (song, stems, grid) = MakeSong(40, 1.., 1.., extra: t => t >= FIRST_BEAT && t < FIRST_BEAT + BAR ? 0.05f * MathF.Sin((float)(2 * Math.PI * 660 * t)) : 0);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(why);
        Assert.Equal(16 * BAR - (FIRST_BEAT + BAR), edit!.Offset, 3);
        Assert.Contains("fill into its first beat", edit.Summary);
    }

    [Fact]
    public void Beats_that_stop_partway_through_a_phrase_carry_on_to_the_end_of_the_next()
    {
        // The drums and vocals stop after bar 37, leaving the pads to die away: the outro runs from there to bar 56,
        // nineteen bars.
        var (song, stems, grid) = MakeSong(40, ..37, ..37, bass: false);

        var (edit, _) = DjEdit.Make(song, stems, grid);

        Assert.Equal(edit!.Offset + FIRST_BEAT + 37 * BAR, edit.OutroStart!.Value, 3);
        Assert.Equal(edit.Offset + FIRST_BEAT + 56 * BAR + BEAT, (double)edit.Audio.Frames / RATE, 2);
        Assert.Contains("19 bars after it", edit.Summary);
    }

    [Fact]
    public void A_fade_out_gives_way_to_the_outros_beats_where_it_starts()
    {
        // Everything fades away over the last ten bars, from bar 30.
        var (song, stems, grid) = MakeSong(40, .., .., fadeBars: 10);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(why);
        Assert.Contains("in place of its fade-out", edit!.Summary);
        // The beats take over on the four-bar line where the fade's barely begun, and the song's gone two bars later.
        var outroBar = Math.Round((edit.OutroStart!.Value - edit.Offset - FIRST_BEAT) / BAR);
        Assert.InRange(outroBar, 28, 32);
        Assert.Equal(0, outroBar % 4);
        var gone = edit.OutroStart.Value + 2 * BAR;
        Assert.True(Rms(edit.Audio.Samples, gone, gone + BAR) > 0.05);
        // What's left after that is the loop alone: the song's vocal, at 440 Hz, isn't in it.
        var loopOnly = edit.Audio.Samples.AsSpan((int)(gone * RATE) * CHANNELS, (int)(BAR * RATE) * CHANNELS).ToArray();
        Assert.True(Goertzel(loopOnly, 440) < 0.01, $"{Goertzel(loopOnly, 440)}");
        Assert.True(Goertzel(song.Samples.AsSpan((int)((gone - edit.Offset) * RATE) * CHANNELS, (int)(BAR * RATE) * CHANNELS).ToArray(), 440) > 0.02);
    }

    // How strong a frequency is in the left channel, as an amplitude.
    static double Goertzel(float[] samples, double hz)
    {
        var (re, im) = (0.0, 0.0);
        var n = samples.Length / CHANNELS;
        for (var i = 0; i < n; i++)
        {
            var phase = 2 * Math.PI * hz * i / RATE;
            (re, im) = (re + samples[i * CHANNELS] * Math.Cos(phase), im + samples[i * CHANNELS] * Math.Sin(phase));
        }
        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    [Fact]
    public void A_drum_fill_before_the_first_downbeat_is_left_a_gap_in_the_beats()
    {
        // Two beats of drums before the first bar of them: the first downbeat is the second bar's.
        var pickup = FIRST_BEAT + 2 * BEAT;
        var (song, stems, grid) = MakeSong(40, 1.., 1.., drumPickupBeats: 2);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(why);
        Assert.Equal(16 * BAR - (FIRST_BEAT + BAR), edit!.Offset, 3);
        Assert.Contains("gap for its pickup", edit.Summary);
        // Over the fill there's nothing but the fill.
        var shift = (long)Math.Round(edit.Offset * RATE);
        for (var frame = (long)((pickup + 0.01) * RATE); frame < (long)((FIRST_BEAT + BAR - 0.01) * RATE); frame += 13)
            Assert.Equal(song.Samples[frame * CHANNELS], edit.Audio.Samples[(frame + shift) * CHANNELS]);
        // And the beats play up to it.
        Assert.True(Rms(edit.Audio.Samples, pickup + edit.Offset - BEAT, pickup + edit.Offset - 0.05) > 0.05);
    }

    [Fact]
    public void A_voice_before_the_first_downbeat_has_the_beats_carry_on_under_it()
    {
        // A sung beat and a half before the first bar of the song, which comes in without drums, as an a cappella
        // start does: the beats carry on under it, rather than stopping dead for it, and die away from the downbeat.
        var pickup = FIRST_BEAT + 2.5 * BEAT;
        var (song, stems, grid) = MakeSong(40, 3.., 1.., extra: t => t >= pickup && t < FIRST_BEAT + BAR ? 0.3f * MathF.Sin((float)(2 * Math.PI * 660 * t)) : 0);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(why);
        Assert.Contains("landing on its first beat", edit!.Summary);
        Assert.DoesNotContain("gap", edit.Summary);
        var downbeat = edit.IntroEnd!.Value;
        var under = edit.Audio.Samples.AsSpan((int)((downbeat - BEAT) * RATE) * CHANNELS, (int)(BEAT * RATE) * CHANNELS).ToArray();
        var alone = song.Samples.AsSpan((int)((downbeat - BEAT - edit.Offset) * RATE) * CHANNELS, (int)(BEAT * RATE) * CHANNELS).ToArray();
        Assert.True(Rms(under, 0, BEAT) > Rms(alone, 0, BEAT) * 1.2, $"{Rms(under, 0, BEAT)} against the voice alone, {Rms(alone, 0, BEAT)}");
        // The bass brought in with the beats dies away over the first beat of the song, rather than stopping dead.
        double Bass(double from, double seconds) => Goertzel(edit.Audio.Samples.AsSpan((int)(from * RATE) * CHANNELS, (int)(seconds * RATE) * CHANNELS).ToArray(), 55);
        Assert.True(Bass(downbeat, BEAT / 4) > 0.03, $"{Bass(downbeat, BEAT / 4)}");
        Assert.True(Bass(downbeat + BEAT, BEAT) < 0.01, $"{Bass(downbeat + BEAT, BEAT)}");
    }

    [Fact]
    public void A_song_that_comes_straight_in_on_its_drums_gets_a_fill_into_it()
    {
        var (song, stems, grid) = MakeSong(40, .., ..);

        var (edit, _) = DjEdit.Make(song, stems, grid);

        Assert.Contains("fill into its first beat", edit!.Summary);
    }

    [Fact]
    public void A_song_without_drums_has_nothing_to_make_beats_from()
    {
        var (song, stems, grid) = MakeSong(40, 0..0, ..);

        var (edit, why) = DjEdit.Make(song, stems, grid);

        Assert.Null(edit);
        Assert.Contains("no drums", why);
    }

    [Theory]
    [InlineData("Song (DJ Edit)", true)]
    [InlineData("Song (Cuepoint DJ Edit)", true)]
    [InlineData("Song (Extended Mix)", true)]
    [InlineData("Song [Extended]", true)]
    [InlineData("Song (Club Mix)", true)]
    [InlineData("Song (Intro - Clean)", true)]
    [InlineData("Song (Dirty Intro)", true)]
    [InlineData("Song - Extended Version", true)]
    [InlineData("Song (12\" Mix)", true)]
    [InlineData("Song", false)]
    [InlineData("Song (Radio Edit)", false)]
    [InlineData("Song (Original Mix)", false)]
    [InlineData("Introduction to Love", false)]
    [InlineData("Extended Family", false)]
    public void Edits_and_extended_mixes_are_known_by_their_names(string title, bool edit) =>
        Assert.Equal(edit, DjEdit.LooksLikeEdit(title, $@"C:\Music\{title.Replace('"', '_')}.mp3"));

    [Theory]
    [InlineData("Song", "Song (Cuepoint DJ Edit)")]
    [InlineData("Song (Radio Edit)", "Song (Cuepoint DJ Edit)")]
    [InlineData("Song (radio mix)", "Song (Cuepoint DJ Edit)")]
    [InlineData("Song [Single Version]", "Song (Cuepoint DJ Edit)")]
    [InlineData("Song - Radio Edit", "Song (Cuepoint DJ Edit)")]
    [InlineData("Song (Original Mix)", "Song (Cuepoint DJ Edit)")]
    [InlineData("Song (Edit)", "Song (Cuepoint DJ Edit)")]
    [InlineData("Song (feat. Singer) (Radio Edit)", "Song (feat. Singer) (Cuepoint DJ Edit)")]
    [InlineData("Song (Someone Remix)", "Song (Someone Remix) (Cuepoint DJ Edit)")]
    [InlineData("Song (Acoustic Version)", "Song (Acoustic Version) (Cuepoint DJ Edit)")]
    public void An_edit_is_named_for_the_song_in_place_of_its_radio_edit(string title, string edit) => Assert.Equal(edit, DjEdit.EditTitle(title));

    [WindowsFact]
    public void An_edits_file_is_named_as_its_title_is()
    {
        Assert.Equal(@"C:\Music\01 Artist - Song (Cuepoint DJ Edit).flac", DjEdit.EditPath(@"C:\Music\01 Artist - Song (Radio Edit).mp3"));
        Assert.Equal(@"C:\Music\Song (Cuepoint DJ Edit).flac", DjEdit.EditPath(@"C:\Music\Song.m4a"));
        // Before they were FLACs, they were WAVs.
        Assert.Equal(@"C:\Music\Song (Cuepoint DJ Edit).wav", DjEdit.OldEditPath(@"C:\Music\Song.m4a"));
    }
}
