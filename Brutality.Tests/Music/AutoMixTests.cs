using Brutality.Audio.Analysis;
using Brutality.Music;
using static Brutality.Tests.Music.LibraryFileTests;

namespace Brutality.Tests.Music;

/// <summary>
/// Choosing transitions, the moves in them, and running a list through two pretend decks.
/// </summary>
public sealed class AutoMixTests
{
    static readonly MusicalKey AMinor = new(9, true);
    static readonly MusicalKey CMajor = new(0, false);
    static readonly MusicalKey DFlatMajor = new(1, false);

    const double FIRST_BEAT = 0.1;

    /// <summary>
    /// A song with a kick on every beat: bass full in its body, sparse before <paramref name="introEnd"/> and after
    /// <paramref name="outroFrom"/>, and silent from <paramref name="silentFrom"/>.
    /// </summary>
    static Func<double, double, BandLevels> Beats(double bpm, double introEnd = 0, double outroFrom = double.MaxValue, double silentFrom = double.MaxValue, double kickAt = 0)
    {
        var beat = 60 / bpm;
        return (start, end) =>
        {
            if (start >= silentFrom)
                return default;
            var middle = (start + end) / 2;
            var bass = middle < introEnd || middle > outroFrom ? 0.2f : 1f;
            if (end - start < beat * 0.5)
            {
                var phase = (start - FIRST_BEAT - kickAt * beat) / beat % 1;
                phase = phase < 0 ? phase + 1 : phase;
                bass *= phase < 0.2 || phase > 0.95 ? 1 : 0.5f;
            }
            return new BandLevels(1, bass, 0.5f, 0.3f);
        };
    }

    static MixSong Song_(double bpm, MusicalKey key, double duration = 300, Func<double, double, BandLevels>? levels = null, double cue = 0) =>
        new(new TrackAnalysis(BeatGrid.Constant(bpm, FIRST_BEAT, duration), -9, 0.9f, key), duration, cue, levels ?? Beats(bpm));

    static BeatGrid Grid(MixSong song) => song.Analysis!.Grid;

    [Fact]
    public void Sparse_ends_get_a_long_blend_starting_on_a_phrase_in_the_outro()
    {
        var outgoing = Song_(125, AMinor, levels: Beats(125, outroFrom: 220));
        var incoming = Song_(125, CMajor, levels: Beats(125, introEnd: 80));

        var plan = TransitionPlanner.Plan(outgoing, incoming, 10);

        Assert.Equal(TransitionKind.Blend, plan.Kind);
        Assert.Equal(32, plan.Bars);
        Assert.True(plan.BeatMatched);
        Assert.True(plan.StartAt >= 220);
        Assert.True(plan.EndAt <= 300);
        var beat = Grid(outgoing).BeatNumberAt(plan.StartAt);
        Assert.Equal(Math.Round(beat), beat, 6);
        Assert.Equal(0, Math.Round(beat) % 32);
    }

    [Fact]
    public void A_new_song_starts_a_bar_early_to_line_up()
    {
        var outgoing = Song_(125, AMinor);
        var incoming = Song_(125, AMinor);

        var plan = TransitionPlanner.Plan(outgoing, incoming, 10);

        Assert.Equal(plan.StartAt - 4 * 60.0 / 125, plan.IncomingAt, 6);
        Assert.True(plan.IncomingFrom >= 0);
        Assert.True(Grid(incoming).IsBarStart(Math.Round(Grid(incoming).BeatNumberAt(plan.IncomingFrom))));
    }

    [Fact]
    public void Two_full_on_songs_get_a_short_bass_swap()
    {
        var plan = TransitionPlanner.Plan(Song_(125, AMinor), Song_(126, AMinor), 10);

        Assert.Equal(TransitionKind.BassSwap, plan.Kind);
        Assert.Equal(8, plan.Bars);
    }

    [Fact]
    public void Clashing_keys_get_a_filter_sweep()
    {
        var plan = TransitionPlanner.Plan(Song_(125, AMinor), Song_(125, DFlatMajor), 10);

        Assert.Equal(TransitionKind.FilterSweep, plan.Kind);
        Assert.True(plan.BeatMatched);
    }

    [Fact]
    public void Tempos_too_far_apart_get_an_echo_out_with_no_matching()
    {
        var plan = TransitionPlanner.Plan(Song_(125, AMinor), Song_(100, AMinor), 10);

        Assert.Equal(TransitionKind.EchoOut, plan.Kind);
        Assert.False(plan.BeatMatched);
        Assert.True(plan.IncomingAt > plan.StartAt);
    }

    [Fact]
    public void Half_time_is_close_enough_to_match()
    {
        var plan = TransitionPlanner.Plan(Song_(170, AMinor), Song_(85, AMinor), 10);

        Assert.True(plan.BeatMatched);
        Assert.Equal(plan.StartAt - 8 * 60.0 / 170, plan.IncomingAt, 6);
    }

    [Fact]
    public void A_tempo_that_drifts_is_still_beat_matched()
    {
        var grid = new BeatGrid([new GridMarker(0.1, 124.9), new GridMarker(60.1, 123.1), new GridMarker(76.1, 124.9)], 300);
        var drifting = new MixSong(new TrackAnalysis(grid, -9, 0.9f, AMinor), 300, 0, Beats(124.9));

        Assert.True(TransitionPlanner.Plan(drifting, Song_(126, AMinor), 10).BeatMatched);
        Assert.True(TransitionPlanner.Plan(Song_(126, AMinor), drifting, 10).BeatMatched);
    }

    [Fact]
    public void A_tempo_that_drifts_out_of_reach_where_they_d_meet_isn_t_matched()
    {
        var grid = new BeatGrid([new GridMarker(0.1, 100), new GridMarker(120.1, 125)], 300);
        var drifting = new MixSong(new TrackAnalysis(grid, -9, 0.9f, AMinor), 300, 0, Beats(125));

        Assert.False(TransitionPlanner.Plan(Song_(125, AMinor), drifting, 10).BeatMatched);
    }

    [Fact]
    public void A_grid_on_the_off_beat_is_lined_up_by_what_s_heard()
    {
        var outgoing = Song_(125, AMinor);
        var incoming = Song_(125, AMinor, levels: Beats(125, kickAt: 0.5));

        var plan = TransitionPlanner.Plan(outgoing, incoming, 10);

        Assert.True(plan.BeatMatched);
        Assert.Equal(0.5, Math.Abs(plan.BeatShift), 1);
    }

    [Fact]
    public void The_kicks_are_lined_up_ahead_of_the_hi_hats()
    {
        BandLevels Off(double start, double end, double kickAt, double hatAt)
        {
            var beat = 60.0 / 125;
            float Hit(double at)
            {
                var phase = ((start - FIRST_BEAT) / beat - at) % 1;
                phase = phase < 0 ? phase + 1 : phase;
                return phase < 0.15 ? 1 : 0.2f;
            }
            return end - start < beat * 0.5 ? new BandLevels(1, Hit(kickAt), 0.5f, Hit(hatAt)) : new BandLevels(1, 1, 0.5f, 0.5f);
        }
        var outgoing = Song_(125, AMinor, levels: (a, b) => Off(a, b, 0, 0.5));
        var incoming = Song_(125, AMinor, levels: (a, b) => Off(a, b, 0.5, 0.75));

        var plan = TransitionPlanner.Plan(outgoing, incoming, 10);

        Assert.Equal(0.5, Math.Abs(plan.BeatShift), 1);
    }

    [Fact]
    public void Grids_that_agree_with_what_s_heard_are_left_alone()
    {
        var plan = TransitionPlanner.Plan(Song_(125, AMinor), Song_(125, AMinor), 10);

        Assert.Equal(0, plan.BeatShift, 2);
    }

    [Fact]
    public void Songs_with_nothing_to_go_by_are_not_shifted()
    {
        var flat = Song_(125, AMinor, levels: (_, _) => new BandLevels(0.6f, 0.4f, 0.5f, 0.2f));

        Assert.Equal(0, TransitionPlanner.Plan(Song_(125, AMinor), flat, 10).BeatShift);
    }

    [Fact]
    public void A_song_not_analysed_gets_a_crossfade()
    {
        var unknown = new MixSong(null, 300, 0, Beats(120));

        var plan = TransitionPlanner.Plan(Song_(125, AMinor), unknown, 10);

        Assert.Equal(TransitionKind.Crossfade, plan.Kind);
        Assert.False(plan.BeatMatched);
    }

    [Fact]
    public void A_song_with_nothing_marking_its_beats_gets_a_beat_matched_fade()
    {
        var ballad = Song_(80, AMinor, levels: (_, _) => new BandLevels(0.6f, 0.4f, 0.5f, 0.2f));

        var plan = TransitionPlanner.Plan(ballad, Song_(80, AMinor), 10);

        Assert.Equal(TransitionKind.Crossfade, plan.Kind);
        Assert.True(plan.BeatMatched);
    }

    [Fact]
    public void Silence_at_the_end_isn_t_mixed_into()
    {
        var outgoing = Song_(125, AMinor, levels: Beats(125, silentFrom: 270));

        var plan = TransitionPlanner.Plan(outgoing, Song_(125, AMinor), 10);

        Assert.True(plan.EndAt <= 270.3, $"Ends at {plan.EndAt}");
    }

    [Fact]
    public void Nothing_happens_before_the_earliest_it_can()
    {
        var plan = TransitionPlanner.Plan(Song_(125, AMinor), Song_(125, AMinor), 280);

        Assert.True(plan.IncomingAt >= 280);
        Assert.True(plan.EndAt <= 300.01);
    }

    [Fact]
    public void Left_too_late_it_settles_for_a_quick_fade()
    {
        var plan = TransitionPlanner.Plan(Song_(125, AMinor), Song_(125, AMinor), 298);

        Assert.Equal(TransitionKind.Crossfade, plan.Kind);
        Assert.True(plan.EndAt > plan.StartAt);
    }

    [Fact]
    public void A_short_new_song_isn_t_taken_up_by_the_transition()
    {
        var plan = TransitionPlanner.Plan(Song_(125, AMinor, levels: Beats(125, outroFrom: 220)), Song_(125, AMinor, duration: 40, levels: Beats(125, introEnd: 40)), 10);

        Assert.True(plan.EndAt - plan.StartAt <= 20);
    }

    [Fact]
    public void A_new_song_comes_in_at_the_downbeat_nearest_its_cue()
    {
        var bar = 4 * 60.0 / 125;
        var incoming = Song_(125, AMinor, cue: FIRST_BEAT + 8 * bar + 0.05);

        var plan = TransitionPlanner.Plan(Song_(125, AMinor), incoming, 10);

        Assert.Equal(FIRST_BEAT + 7 * bar, plan.IncomingFrom, 6);
    }

    [Theory]
    [InlineData("Blend")]
    [InlineData("BassSwap")]
    [InlineData("FilterSweep")]
    [InlineData("EchoOut")]
    [InlineData("Crossfade")]
    public void Every_transition_goes_from_the_old_song_to_the_new_and_never_back(string name)
    {
        var kind = Enum.Parse<TransitionKind>(name);
        Assert.Equal(0, TransitionPlanner.At(kind, 0).Crossfade);
        Assert.Equal(1, TransitionPlanner.At(kind, 1).Crossfade);
        var last = 0f;
        for (var p = 0.0; p <= 1; p += 0.01)
        {
            var fade = TransitionPlanner.At(kind, p).Crossfade;
            Assert.True(fade >= last);
            last = fade;
        }
    }

    [Fact]
    public void In_a_blend_the_basses_are_never_both_up()
    {
        Assert.Equal(-1, TransitionPlanner.At(TransitionKind.Blend, 0).InLow);
        Assert.Equal(0, TransitionPlanner.At(TransitionKind.Blend, 1).InLow);
        Assert.Equal(-1, TransitionPlanner.At(TransitionKind.Blend, 1).OutLow);
        for (var p = 0.0; p <= 1; p += 0.01)
        {
            var moves = TransitionPlanner.At(TransitionKind.Blend, p);
            Assert.Equal(-1, moves.OutLow + moves.InLow, 3);
        }
    }

    [Fact]
    public void An_echo_out_cuts_the_sound_and_leaves_the_echo()
    {
        Assert.Equal(0, TransitionPlanner.At(TransitionKind.EchoOut, 0.2).OutMid);
        var cut = TransitionPlanner.At(TransitionKind.EchoOut, 0.5);
        Assert.Equal(-1, cut.OutLow);
        Assert.Equal(-1, cut.OutMid);
        Assert.Equal(-1, cut.OutHigh);
        Assert.True(cut.Echo);
    }

    sealed class FakeDeck(string name, float end) : IMixDeck
    {
        public string Name => name;
        public float CrossfaderEnd => end;
        public string? FilePath { get; set; }
        public bool IsLoading { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsAudible { get; set; }
        public double Seconds { get; set; }
        public TimeSpan Position => TimeSpan.FromSeconds(Seconds);
        public TimeSpan Duration { get; set; }
        public TrackAnalysis? Analysis { get; set; }
        public TimeSpan CuePoint => TimeSpan.Zero;
        public double Tempo { get; set; }
        public float Volume { get; set; } = 0.5f;
        public bool KeyLocked { get; set; }
        public float Low { get; set; }
        public float Mid { get; set; }
        public float High { get; set; }
        public float Filter { get; set; }
        public bool Echo { get; private set; }
        public int Syncs { get; private set; }
        public int Follows { get; private set; }
        public int LetGo { get; private set; }
        public Func<double, double, BandLevels> Levels { get; set; } = (_, _) => default;
        public FakeDeck? Master { get; set; }

        public BandLevels LevelsBetween(double start, double finish) => Levels(start, finish);
        public void SetEcho(bool on) => Echo = on;
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Seek(TimeSpan position) => Seconds = position.TotalSeconds;

        public void FollowBeat(double beatShift) => Follows++;
        public void StopFollowing() => LetGo++;

        public bool Sync(double beatShift)
        {
            Syncs++;
            if (Master?.Analysis is { } master && Analysis is { } own)
                Tempo = master.Bpm * (1 + Master.Tempo) / own.Bpm - 1;
            return true;
        }

        public void Fail(string path) => LoadFailed?.Invoke(path);

        public event Action<string>? LoadFailed;
    }

    sealed class Rig
    {
        public readonly FakeDeck A = new("A", 0);
        public readonly FakeDeck B = new("B", 1);
        public float Crossfader;
        public readonly AutoMix Mix;
        public readonly List<string> Logged = [];
        public readonly List<(Song Song, FakeDeck Deck)> Loads = [];
        public Func<Song, bool> WillLoad = _ => true;
        public Func<Song, (TrackAnalysis? Analysis, double Seconds)> Describe = _ => (Analysed(125), 300);

        public static TrackAnalysis Analysed(double bpm, double seconds = 300) => new(BeatGrid.Constant(bpm, FIRST_BEAT, seconds), -9, 0.9f, AMinor);

        public Rig(int songs = 3)
        {
            (A.Master, B.Master) = (B, A);
            Mix = new AutoMix([A, B], () => Crossfader, value => Crossfader = value, (song, deck) => Load(song, (FakeDeck)deck));
            Mix.Log += Logged.Add;
            var list = Enumerable.Range(1, songs).Select(i => Song($@"C:\Music\{i}.mp3", title: $"Song {i}")).ToList();
            foreach (var song in list)
                song.Duration = TimeSpan.FromSeconds(300);
            Mix.Open("Set", list);
        }

        void Load(Song song, FakeDeck deck)
        {
            Loads.Add((song, deck));
            if (!WillLoad(song))
            {
                deck.Fail(song.Path);
                return;
            }
            var (analysis, seconds) = Describe(song);
            (deck.FilePath, deck.Analysis, deck.Duration, deck.Seconds, deck.Levels) = (song.Path, analysis, TimeSpan.FromSeconds(seconds), 0, Beats(analysis?.Bpm ?? 125));
        }

        /// <summary>
        /// Runs for <paramref name="seconds"/> in fortieths of a second, the decks moving on as the audio would.
        /// </summary>
        public void Run(double seconds)
        {
            const double STEP = 0.025;
            for (var t = 0.0; t < seconds; t += STEP)
            {
                foreach (var deck in new[] { A, B })
                {
                    if (!deck.IsPlaying)
                        continue;
                    deck.Seconds += STEP * (1 + deck.Tempo);
                    if (deck.Seconds >= deck.Duration.TotalSeconds)
                        (deck.IsPlaying, deck.Seconds) = (false, deck.Duration.TotalSeconds);
                }
                Mix.Update(STEP);
            }
        }

        public Rig Started()
        {
            Assert.Null(Mix.Start());
            Mix.Update(0.025);
            return this;
        }
    }

    [Fact]
    public void Playing_puts_the_first_song_on_deck_A_with_the_mixer_set_straight()
    {
        var rig = new Rig();
        (rig.Crossfader, rig.A.Low, rig.B.Filter) = (0.7f, -0.5f, 0.4f);

        rig.Started();

        Assert.Equal(0, rig.Crossfader);
        Assert.Equal(0, rig.A.Low);
        Assert.Equal(0, rig.B.Filter);
        Assert.Equal(1, rig.A.Volume);
        Assert.Equal(1, rig.B.Volume);
        Assert.Equal(rig.Mix.Songs[0], rig.Loads[0].Song);
        Assert.Equal(rig.A, rig.Loads[0].Deck);
        Assert.True(rig.A.IsPlaying);
        Assert.Equal(0, rig.Mix.CurrentIndex);
    }

    [Fact]
    public void It_won_t_start_over_a_deck_being_heard()
    {
        var rig = new Rig();
        rig.B.IsAudible = true;

        Assert.Contains("Deck B", rig.Mix.Start());
        Assert.False(rig.Mix.IsRunning);
        Assert.Empty(rig.Loads);
    }

    [Fact]
    public void The_next_song_goes_on_the_other_deck_straight_away()
    {
        var rig = new Rig().Started();
        rig.Run(0.1);

        Assert.Equal(rig.Mix.Songs[1], rig.Loads[1].Song);
        Assert.Equal(rig.B, rig.Loads[1].Deck);
        Assert.False(rig.B.IsPlaying);
    }

    [Fact]
    public void A_transition_hands_over_and_leaves_the_mixer_straight_for_the_next()
    {
        var rig = new Rig(2).Started();
        rig.Run(300);

        Assert.Equal(1, rig.Mix.CurrentIndex);
        Assert.Equal(1, rig.Crossfader);
        Assert.True(rig.B.IsPlaying);
        Assert.False(rig.A.IsPlaying);
        Assert.Equal(0, rig.A.Low);
        Assert.Equal(0, rig.B.Low);
        Assert.Equal(0, rig.A.Tempo);
        Assert.True(rig.B.Syncs >= 2);
        Assert.True(rig.B.Follows > 100);
        Assert.True(rig.B.LetGo > 0);
    }

    [Fact]
    public void Decks_take_turns()
    {
        var rig = new Rig().Started();
        rig.Run(600);

        Assert.Equal(2, rig.Mix.CurrentIndex);
        Assert.Equal([rig.A, rig.B, rig.A], rig.Loads.Select(load => load.Deck));
        Assert.Equal(0, rig.Crossfader);
    }

    [Fact]
    public void The_new_song_glides_back_to_its_own_tempo()
    {
        var rig = new Rig(2);
        rig.Describe = song => (Rig.Analysed(song == rig.Mix.Songs[0] ? 125 : 122), 300);
        rig.Started();
        while (rig.Mix.CurrentIndex == 0)
            rig.Run(1);
        var matched = rig.B.Tempo;
        Assert.True(matched > 0.01);

        rig.Run(10);
        Assert.InRange(rig.B.Tempo, 0.0001, matched);

        rig.Run(60);
        Assert.Equal(0, rig.B.Tempo);
    }

    [Fact]
    public void An_echo_out_uses_the_old_deck_s_echo_and_puts_it_back()
    {
        var rig = new Rig(2);
        rig.Describe = song => (Rig.Analysed(song == rig.Mix.Songs[0] ? 125 : 100), 300);
        rig.Started();
        var sawEcho = false;
        while (rig.Mix.CurrentIndex == 0)
        {
            rig.Run(0.5);
            sawEcho |= rig.A.Echo;
        }

        Assert.True(sawEcho);
        Assert.False(rig.A.Echo);
        Assert.True(rig.B.IsPlaying);
    }

    [Fact]
    public void An_echo_out_starts_the_new_song_partway_through()
    {
        var rig = new Rig(2);
        rig.Describe = song => (Rig.Analysed(song == rig.Mix.Songs[0] ? 125 : 100), 300);
        rig.Started();
        while (!rig.A.Echo)
            rig.Run(0.025);
        Assert.False(rig.B.IsPlaying);

        rig.Run(3);

        Assert.True(rig.B.IsPlaying);
        Assert.Equal(0, rig.Mix.CurrentIndex);
    }

    [Fact]
    public void The_last_song_plays_out_and_it_finishes()
    {
        var rig = new Rig(1).Started();
        rig.Run(301);

        Assert.False(rig.Mix.IsRunning);
        Assert.Equal("Finished", rig.Mix.Status);
    }

    [Fact]
    public void A_song_that_won_t_load_is_passed_over()
    {
        var rig = new Rig();
        rig.WillLoad = song => song != rig.Mix.Songs[1];
        rig.Started();
        rig.Run(300);

        Assert.Equal(2, rig.Mix.CurrentIndex);
        Assert.Contains(rig.Logged, line => line.Contains("Song 2"));
    }

    [Fact]
    public void A_last_song_that_won_t_load_leaves_the_one_before_to_play_out()
    {
        var rig = new Rig(2);
        rig.WillLoad = song => song != rig.Mix.Songs[1];
        rig.Started();
        rig.Run(1);
        Assert.Equal(2, rig.Loads.Count);

        rig.Run(300);

        Assert.False(rig.Mix.IsRunning);
        Assert.Equal("Finished", rig.Mix.Status);
    }

    [Fact]
    public void When_no_songs_will_load_it_says_so()
    {
        var rig = new Rig(2);
        rig.WillLoad = _ => false;
        Assert.Null(rig.Mix.Start());
        rig.Run(0.2);

        Assert.False(rig.Mix.IsRunning);
    }

    [Fact]
    public void Pausing_stops_both_decks_and_carries_on_where_it_was()
    {
        var rig = new Rig(2).Started();
        rig.Run(5);
        var at = rig.A.Seconds;

        rig.Mix.TogglePause();
        rig.Run(10);

        Assert.False(rig.A.IsPlaying);
        Assert.Equal(at, rig.A.Seconds);
        Assert.True(rig.Mix.IsRunning);

        rig.Mix.TogglePause();
        rig.Run(1);

        Assert.True(rig.A.IsPlaying);
        Assert.True(rig.Mix.IsRunning);
    }

    public static TheoryData<string> Interference =>
        new() { "crossfader", "fader", "eq", "filter", "tempo", "pause", "seek", "load", "other deck playing" };

    [Theory]
    [MemberData(nameof(Interference))]
    public void Anything_moved_by_hand_stops_it_with_the_music_playing(string what)
    {
        var rig = new Rig().Started();
        rig.Run(5);

        switch (what)
        {
            case "crossfader":
                rig.Crossfader = 0.3f;
                break;
            case "fader":
                rig.A.Volume = 0.6f;
                break;
            case "eq":
                rig.B.High = -0.4f;
                break;
            case "filter":
                rig.A.Filter = 0.2f;
                break;
            case "tempo":
                rig.A.Tempo = 0.02;
                break;
            case "pause":
                rig.A.Pause();
                break;
            case "seek":
                rig.A.Seconds += 20;
                break;
            case "load":
                rig.B.FilePath = @"C:\Music\other.mp3";
                break;
            case "other deck playing":
                rig.B.Play();
                break;
        }
        rig.Run(0.05);

        Assert.False(rig.Mix.IsRunning);
        Assert.Equal("You took over", rig.Mix.Status);
        Assert.Equal(what != "pause", rig.A.IsPlaying);
    }

    [Fact]
    public void Left_alone_a_long_run_is_never_taken_for_interference()
    {
        var rig = new Rig().Started();
        rig.Run(850);

        Assert.DoesNotContain(rig.Logged, line => line.Contains("took over"));
    }

    [Fact]
    public void Stopping_puts_the_decks_key_lock_back()
    {
        var rig = new Rig();
        (rig.A.KeyLocked, rig.B.KeyLocked) = (false, true);
        rig.Started();
        Assert.True(rig.A.KeyLocked);

        rig.Mix.Stop("Stopped");

        Assert.False(rig.A.KeyLocked);
        Assert.True(rig.B.KeyLocked);
    }

    [Fact]
    public void The_clock_runs_on_and_the_total_comes_down_once_a_transition_s_planned()
    {
        var rig = new Rig(2);
        Assert.Equal(TimeSpan.Zero, rig.Mix.Elapsed);
        rig.Started();
        rig.Run(10);

        Assert.InRange(rig.Mix.Elapsed.TotalSeconds, 9.9, 10.1);
        Assert.True(rig.Mix.Total.TotalSeconds < 600);
        Assert.True(rig.Mix.Total.TotalSeconds > 300);
    }

    [Fact]
    public void Starting_again_after_it_s_stopped_goes_from_the_top()
    {
        var rig = new Rig().Started();
        rig.Run(5);
        rig.Mix.Stop("Stopped");
        rig.A.Pause();

        Assert.Null(rig.Mix.Start());

        Assert.Equal(0, rig.Mix.CurrentIndex);
        Assert.Equal(rig.Mix.Songs[0], rig.Loads[^1].Song);
    }
}
