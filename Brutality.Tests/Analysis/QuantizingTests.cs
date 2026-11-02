using Brutality.Audio.Analysis;
using Brutality.Music;

namespace Brutality.Tests.Analysis;

public class QuantizingTests
{
    // 120 BPM, a beat every half second from 0.25 s.
    static readonly BeatGrid Steady = BeatGrid.Constant(120, 0.25, 300);

    // 120 BPM from a second in, then 60 BPM from five seconds: eight beats in the first stretch.
    static readonly BeatGrid Drifting = new([new GridMarker(1, 120), new GridMarker(5, 60)], 60);

    [Theory]
    [InlineData(10.2, 10.25)]
    [InlineData(10.45, 10.25)]
    [InlineData(10.55, 10.75)]
    [InlineData(0.1, 0.25)]
    public void Snapping_goes_to_the_nearest_beat(double seconds, double expected)
    {
        Assert.Equal(expected, Quantizing.Snap(Steady, seconds), 9);
    }

    [Fact]
    public void Jumping_moves_whole_beats_at_the_same_point_in_the_beat()
    {
        // A tenth of a second past a beat, sixteen beats on, is a tenth of a second past the beat eight seconds on.
        Assert.Equal(18.35, Quantizing.Jump(Steady, 10.35, 16), 9);
        Assert.Equal(2.35, Quantizing.Jump(Steady, 10.35, -16), 9);
    }

    [Fact]
    public void Jumping_counts_through_a_tempo_change()
    {
        // From beat 6 at 120 BPM (4 s), four beats on: two to the change at beat 8, then two at 60 BPM.
        Assert.Equal(7, Quantizing.Jump(Drifting, 4, 4), 9);
        Assert.Equal(4, Quantizing.Jump(Drifting, 7, -4), 9);
    }

    [Fact]
    public void Jumps_there_and_back_come_back_to_the_same_place()
    {
        foreach (var start in new[] { 3.3, 6.1, 20.7 })
        {
            foreach (var beats in new double[] { 1, 4, 16, 32 })
                Assert.Equal(start, Quantizing.Jump(Drifting, Quantizing.Jump(Drifting, start, beats), -beats), 9);
        }
    }

    [Theory]
    // Just past a beat, going to a hotcue on a beat, lands just past that beat.
    [InlineData(10.30, 50.25, 50.30)]
    // Just before a beat lands just before the hotcue's beat's next one, keeping where the music is in its beat.
    [InlineData(10.70, 50.25, 50.70)]
    // A hotcue set off the beat is taken as its nearest beat.
    [InlineData(10.30, 50.20, 50.30)]
    public void A_jump_while_playing_stays_in_time(double from, double hotcue, double expected)
    {
        Assert.Equal(expected, Quantizing.InPhase(Steady, from, hotcue), 9);
    }

    [Fact]
    public void Jump_pads_go_back_on_the_left_and_on_on_the_right_up_to_eight_bars()
    {
        Assert.Equal(8, JumpPads.Sizes.Count);
        Assert.All(JumpPads.Sizes.Take(4), size => Assert.True(size < 0));
        Assert.All(JumpPads.Sizes.Skip(4), size => Assert.True(size > 0));
        Assert.Equal(32, JumpPads.Sizes.Max());
        Assert.Equal(JumpPads.Sizes.Select(size => -size).Reverse(), JumpPads.Sizes);
        Assert.Equal("16", JumpPads.Label(1));
        Assert.Equal(-1, JumpPads.Direction(1));
        Assert.Equal(1, JumpPads.Direction(6));
    }
}
