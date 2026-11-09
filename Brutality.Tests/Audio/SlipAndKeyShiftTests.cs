using Brutal.GlfwApi;
using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.Tests.Audio;

/// <summary>
/// Slip and key shift, on a real deck playing silence; with the other tests that make an audio engine, as only one can
/// be going at a time. Where the song's got to under a slip is kept by the clock, so these hold whether or not
/// there's a sound card playing.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class SlipAndKeyShiftTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Deck _deck;

    public SlipAndKeyShiftTests()
    {
        Silence(_folder["song.wav"]);
        _deck = _engine.CreateDeck("A");
        _deck.Load(_folder["song.wav"]);
        _deck.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 30), -14, 0.5f, new MusicalKey(9, true));
        _deck.Quantize = false;
    }

    public void Dispose()
    {
        _deck.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// Silence as a WAV, for a deck to load and play.
    /// </summary>
    static void Silence(string path, double seconds = 30)
    {
        const int rate = 8000;
        var samples = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }

    // A hotcue at the start, and the deck playing from ten seconds in.
    void PlayingFromTen()
    {
        _deck.HotcueDown(0);
        _deck.Seek(TimeSpan.FromSeconds(10));
        _deck.Play();
    }

    [Fact]
    public void A_hotcue_held_under_slip_drops_back_in_where_the_song_would_have_got_to()
    {
        PlayingFromTen();
        _deck.Slip = true;

        _deck.HotcueDown(0);
        Assert.True(_deck.IsSlipping);
        Assert.True(_deck.Position.TotalSeconds < 2);
        Thread.Sleep(400);
        Assert.True(_deck.SlipPosition!.Value.TotalSeconds >= 10.3);
        _deck.HotcueUp(0);

        Assert.False(_deck.IsSlipping);
        Assert.Null(_deck.SlipPosition);
        Assert.True(_deck.IsPlaying);
        Assert.InRange(_deck.Position.TotalSeconds, 10.3, 12);
    }

    [Fact]
    public void Without_slip_letting_go_of_a_hotcue_stays_where_it_jumped()
    {
        PlayingFromTen();

        _deck.HotcueDown(0);
        Thread.Sleep(100);
        _deck.HotcueUp(0);

        Assert.False(_deck.IsSlipping);
        Assert.True(_deck.Position.TotalSeconds < 2);
    }

    [Fact]
    public void A_loop_under_slip_carries_on_from_where_the_song_got_to_once_it_s_left()
    {
        PlayingFromTen();
        _deck.Slip = true;
        _deck.LoopBeats = 0.25;

        _deck.ToggleLoop();
        Assert.True(_deck.IsLooping);
        Assert.True(_deck.IsSlipping);
        Thread.Sleep(500);
        _deck.ExitLoop();

        // An eighth of a second's loop would have left it at most that far on; slipping, it's half a second on.
        Assert.False(_deck.IsLooping);
        Assert.InRange(_deck.Position.TotalSeconds, 10.4, 12);
    }

    [Fact]
    public void Pausing_during_a_slip_stops_the_song_underneath_too()
    {
        PlayingFromTen();
        _deck.Slip = true;

        _deck.HotcueDown(0);
        _deck.Pause();
        Thread.Sleep(300);
        _deck.HotcueUp(0);

        Assert.False(_deck.IsPlaying);
        Assert.InRange(_deck.Position.TotalSeconds, 10, 10.2);
    }

    [Fact]
    public void Turning_slip_off_midway_stays_where_the_deck_is()
    {
        PlayingFromTen();
        _deck.Slip = true;

        _deck.HotcueDown(0);
        _deck.Slip = false;
        _deck.HotcueUp(0);

        Assert.False(_deck.IsSlipping);
        Assert.True(_deck.Position.TotalSeconds < 2);
    }

    [Fact]
    public void Cue_clears_what_an_echo_has_ringing_whenever_it_pauses_the_deck()
    {
        _deck.Play();
        var before = _deck.Effects.SilenceCount;

        // Playing, Cue goes back to the cue point and pauses.
        _deck.CueDown();
        _deck.CueUp();
        Assert.False(_deck.IsPlaying);
        Assert.True(_deck.Effects.SilenceCount > before);

        // Paused, it plays while held, and pauses again when let go.
        _deck.CueDown();
        Assert.True(_deck.IsCuePreviewing);
        before = _deck.Effects.SilenceCount;
        _deck.CueUp();
        Assert.True(_deck.Effects.SilenceCount > before);
    }

    [Fact]
    public void The_effect_buffers_are_made_with_the_deck_not_when_it_starts_playing()
    {
        Assert.Equal(1, _deck.Effects.PrepareCount);
        Assert.Equal(1, _deck.KeyLockUnit.PrepareCount);

        _deck.Play();
        Thread.Sleep(300);

        Assert.Equal(1, _deck.Effects.PrepareCount);
        Assert.Equal(1, _deck.KeyLockUnit.PrepareCount);
    }

    [Fact]
    public void A_deck_is_audible_only_while_playing_into_the_main_mix()
    {
        Assert.False(_deck.IsAudible);

        _deck.Play();
        Assert.True(_deck.IsAudible);

        _deck.Volume = 0;
        Assert.False(_deck.IsAudible);
        _deck.Volume = 1;
        _deck.Pause();
        Assert.False(_deck.IsAudible);
    }

    [Fact]
    public void A_deck_on_the_side_the_crossfader_has_left_is_not_audible()
    {
        using var left = _engine.CreateDeck("L", CrossfaderSide.Left);
        left.Load(_folder["song.wav"]);
        left.Play();

        _engine.Crossfader = 0;
        Assert.True(left.IsAudible);
        _engine.Crossfader = 1;
        Assert.False(left.IsAudible);
    }

    [Fact]
    public void Moving_the_cue_point_says_so_but_setting_it_again_where_it_is_does_not()
    {
        var moved = 0;
        _deck.CuePointChanged += _ => moved++;
        _deck.Seek(TimeSpan.FromSeconds(10));

        _deck.CueDown();
        _deck.CueUp();
        Assert.Equal(1, moved);
        Assert.Equal(10, _deck.CuePoint.TotalSeconds, 2);

        _deck.CueDown();
        _deck.CueUp();
        Assert.Equal(1, moved);
    }

    [Fact]
    public void A_saved_cue_point_goes_ahead_of_the_first_memory_cue_and_is_where_the_song_waits()
    {
        _deck.SetMemoryCues([TimeSpan.FromSeconds(4)], TimeSpan.FromSeconds(9));

        Assert.Equal(9, _deck.CuePoint.TotalSeconds, 2);
        Assert.Equal(9, _deck.Position.TotalSeconds, 2);
        Assert.Equal(new[] { TimeSpan.FromSeconds(4) }, _deck.MemoryCues);
    }

    [Fact]
    public void A_saved_cue_point_works_without_memory_cues_and_one_past_the_end_is_ignored()
    {
        _deck.SetMemoryCues([], TimeSpan.FromSeconds(7));
        Assert.Equal(7, _deck.CuePoint.TotalSeconds, 2);

        _deck.Load(_folder["song.wav"]);
        _deck.SetMemoryCues([], TimeSpan.FromSeconds(99));
        Assert.Equal(0, _deck.CuePoint.TotalSeconds, 2);
    }

    [Fact]
    public void Slip_only_starts_while_playing()
    {
        _deck.HotcueDown(0);
        _deck.Seek(TimeSpan.FromSeconds(10));
        _deck.Slip = true;

        _deck.HotcueDown(0);

        Assert.False(_deck.IsSlipping);
        Assert.True(_deck.Position.TotalSeconds < 1);
    }

    [Fact]
    public void A_song_s_first_memory_cue_is_its_cue_point_and_where_it_waits_once_loaded()
    {
        _deck.SetMemoryCues([TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(99)]);

        Assert.Equal(new[] { TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12) }, _deck.MemoryCues);
        Assert.Equal(4, _deck.CuePoint.TotalSeconds, 2);
        Assert.Equal(4, _deck.Position.TotalSeconds, 2);
        Assert.False(_deck.IsPlaying);

        _deck.Load(_folder["song.wav"]);
        Assert.Empty(_deck.MemoryCues);
    }

    [Fact]
    public void Memory_cues_put_back_partway_through_set_the_cue_point_but_leave_the_deck_where_it_is()
    {
        _deck.Seek(TimeSpan.FromSeconds(10));

        _deck.SetMemoryCues([TimeSpan.FromSeconds(4)]);

        Assert.Equal(4, _deck.CuePoint.TotalSeconds, 2);
        Assert.Equal(10, _deck.Position.TotalSeconds, 2);
    }

    [Fact]
    public void Key_shift_moves_the_key_by_semitones_within_an_octave_and_goes_with_the_song()
    {
        _deck.KeyShift = 2;
        Assert.Equal(2, _deck.KeyChange);
        Assert.Equal(new MusicalKey(11, true), _deck.Key);

        _deck.KeyShift = 20;
        Assert.Equal(Deck.MAX_KEY_SHIFT, _deck.KeyShift);
        _deck.KeyShift = -20;
        Assert.Equal(-Deck.MAX_KEY_SHIFT, _deck.KeyShift);

        _deck.Load(_folder["song.wav"]);
        Assert.Equal(0, _deck.KeyShift);
    }

    [Fact]
    public void The_keyboard_works_slip_and_key_shift()
    {
        var keys = new KeyboardControls([_deck], _engine);
        void Press(GlfwKey key, GlfwModifier mods)
        {
            keys.Key(key, GlfwKeyAction.Press, mods);
            keys.Key(key, GlfwKeyAction.Release, mods);
        }

        Press(GlfwKey.G, GlfwModifier.Shift);
        Assert.True(_deck.Slip);
        Assert.False(_deck.KeyLocked);

        Press(GlfwKey.Number6, GlfwModifier.Alt);
        Press(GlfwKey.Number6, GlfwModifier.Alt);
        Press(GlfwKey.Number5, GlfwModifier.Alt);
        keys.Update(0.1);
        Assert.Equal(1, _deck.KeyShift);
        Assert.Equal(0, _deck.Tempo);

        Press(GlfwKey.G, GlfwModifier.Alt);
        Assert.Equal(0, _deck.KeyShift);
        Assert.False(_deck.KeyLocked);
    }

    /// <summary>
    /// The other deck, with a song in <paramref name="key"/> on it.
    /// </summary>
    Deck Other(MusicalKey key)
    {
        var other = _engine.CreateDeck("B");
        other.Load(_folder["song.wav"]);
        other.Analysis = new TrackAnalysis(BeatGrid.Constant(124, 0, 30), -14, 0.5f, key);
        return other;
    }

    [Theory]
    // A minor (8A) to E minor (9A): five down rather than seven up.
    [InlineData(4, true, -5)]
    // To D minor (7A): five up.
    [InlineData(2, true, 5)]
    // To C major (8B), A minor's relative major: already matched, so no shift.
    [InlineData(0, false, 0)]
    // To G major (9B), E minor's relative: as for E minor.
    [InlineData(7, false, -5)]
    public void Key_match_shifts_the_key_as_little_as_it_takes_to_mix_with_the_other_deck(int tonic, bool minor, int shift)
    {
        using var other = Other(new MusicalKey(tonic, minor));
        _deck.TempoRange = 0.16;
        _deck.Tempo = 0.1225;
        Assert.True(_deck.CanKeyMatch);

        Assert.True(_deck.KeyMatch());

        Assert.Equal(shift, _deck.KeyShift);
        Assert.True(_deck.KeyLocked);
        Assert.True(_deck.IsKeyMatched);
        Assert.Equal(other.Key!.Value.CamelotNumber, _deck.Key!.Value.CamelotNumber);
    }

    [Fact]
    public void Key_match_does_nothing_without_a_key_on_the_other_deck()
    {
        Assert.False(_deck.CanKeyMatch);
        Assert.False(_deck.KeyMatch());
        Assert.Equal(0, _deck.KeyShift);
        Assert.False(_deck.KeyLocked);
    }

    [Fact]
    public void Key_shift_adds_to_what_the_tempo_fader_does_to_the_key_unless_it_s_locked()
    {
        _deck.TempoRange = 0.16;
        _deck.Tempo = 0.1225;
        _deck.KeyShift = -1;
        Assert.Equal(1, _deck.KeyChange);

        _deck.KeyLocked = true;
        Assert.Equal(-1, _deck.KeyChange);
    }
}

public class KeyLockTests
{
    const int RATE = 48000;

    /// <summary>
    /// How many times a second the sound crosses zero going up: its frequency, for a sine.
    /// </summary>
    static double Frequency(ReadOnlySpan<float> mono)
    {
        var crossings = 0;
        for (var i = 1; i < mono.Length; i++)
            if (mono[i - 1] < 0 && mono[i] >= 0)
                crossings++;
        return crossings * (double)RATE / mono.Length;
    }

    /// <summary>
    /// A second of a sine at <paramref name="hz"/> through the key lock, a block at a time, with the first half second
    /// left out while it settles.
    /// </summary>
    static unsafe float[] Through(KeyLock keyLock, double hz, double speed)
    {
        const int block = 512;
        var output = new List<float>();
        var samples = new float[block];
        for (var start = 0; start < RATE; start += block)
        {
            for (var i = 0; i < block; i++)
                samples[i] = (float)Math.Sin(2 * Math.PI * hz * (start + i) / RATE);
            fixed (float* at = samples)
                keyLock.Process(at, block, 1, RATE, speed);
            if (start >= RATE / 2)
                output.AddRange(samples);
        }
        return [.. output];
    }

    [Theory]
    [InlineData(12)]
    [InlineData(-12)]
    [InlineData(7)]
    public void Shifting_the_key_moves_the_pitch_by_that_many_semitones(int semitones)
    {
        var keyLock = new KeyLock { Shift = MathF.Pow(2, semitones / 12f) };

        var heard = Frequency(Through(keyLock, 440, 1));

        Assert.InRange(heard / 440, Math.Pow(2, (semitones - 0.5) / 12), Math.Pow(2, (semitones + 0.5) / 12));
    }

    [Fact]
    public void With_key_lock_on_the_shift_is_from_the_song_s_own_key_whatever_the_speed()
    {
        // The track sped up 10% arrives at 484 Hz; locked and a semitone up, it comes out a semitone above 440.
        var keyLock = new KeyLock { Enabled = true, Shift = MathF.Pow(2, 1 / 12f) };

        var heard = Frequency(Through(keyLock, 440 * 1.1, 1.1));

        Assert.InRange(heard, 440 * Math.Pow(2, 0.5 / 12), 440 * Math.Pow(2, 1.5 / 12));
    }

    [Fact]
    public void Unshifted_and_unlocked_it_leaves_the_sound_alone()
    {
        var keyLock = new KeyLock();

        Assert.InRange(Frequency(Through(keyLock, 440, 1.1)), 436, 444);
    }
}
