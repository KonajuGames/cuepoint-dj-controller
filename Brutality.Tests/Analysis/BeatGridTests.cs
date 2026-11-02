using Brutality.Audio.Analysis;

namespace Brutality.Tests.Analysis;

public class BeatGridTests
{
    // 120 BPM from a second in, then 60 BPM from five seconds: eight beats in the first stretch.
    static BeatGrid Drifting() => new([new GridMarker(1, 120), new GridMarker(5, 60)], duration: 20);

    [Fact]
    public void A_steady_grid_counts_beats_from_its_marker()
    {
        var grid = BeatGrid.Constant(120, 0.5, 60);

        Assert.Equal(0, grid.BeatNumberAt(0.5), 9);
        Assert.Equal(1, grid.BeatNumberAt(1.0), 9);
        Assert.Equal(-1, grid.BeatNumberAt(0.0), 9);
        Assert.Equal(2.5, grid.TimeOfBeat(4), 9);
        Assert.Equal(120, grid.BpmAt(30));
        Assert.False(grid.IsVariable);
    }

    [Fact]
    public void Beats_run_from_the_start_of_the_song_to_its_end()
    {
        var beats = BeatGrid.Constant(120, 0.5, 60).Beats().ToList();

        Assert.Equal(0, beats[0], 9);
        Assert.Equal(120, beats.Count);
        Assert.Equal(59.5, beats[^1], 9);
    }

    [Fact]
    public void A_drifting_grid_counts_on_through_each_tempo_change()
    {
        var grid = Drifting();

        Assert.Equal(8, grid.BeatNumberAt(5), 9);
        Assert.Equal(10, grid.BeatNumberAt(7), 9);
        Assert.Equal(3, grid.TimeOfBeat(4), 9);
        Assert.Equal(7, grid.TimeOfBeat(10), 9);
        Assert.Equal(120, grid.BpmAt(4.9));
        Assert.Equal(60, grid.BpmAt(5));
        Assert.True(grid.IsVariable);
    }

    [Theory]
    [InlineData(-3.25)]
    [InlineData(0)]
    [InlineData(4.5)]
    [InlineData(7.75)]
    [InlineData(19.9)]
    public void Time_and_beat_number_are_each_other_reversed(double seconds)
    {
        var grid = Drifting();

        Assert.Equal(seconds, grid.TimeOfBeat(grid.BeatNumberAt(seconds)), 9);
    }

    [Fact]
    public void The_beats_of_a_drifting_grid_change_spacing_at_the_marker()
    {
        var beats = Drifting().Beats().ToList();

        Assert.Contains(beats, beat => Math.Abs(beat - 4.5) < 1e-9);
        Assert.Contains(beats, beat => Math.Abs(beat - 5) < 1e-9);
        Assert.Contains(beats, beat => Math.Abs(beat - 6) < 1e-9);
        Assert.DoesNotContain(beats, beat => Math.Abs(beat - 5.5) < 1e-9);
        Assert.Equal(beats.Order(), beats);
    }

    [Fact]
    public void The_main_tempo_is_the_one_covering_most_of_the_song()
    {
        // Five seconds at 120, fifteen at 60.
        Assert.Equal(60, Drifting().MainBpm);
        Assert.Equal(128, BeatGrid.Constant(128, 0, 60).MainBpm);
    }

    [Theory]
    [InlineData(0, 1, 3)]
    [InlineData(1, 1, 0)]
    [InlineData(5, 1, 0)]
    [InlineData(-1, 1, 2)]
    [InlineData(6, 0, 2)]
    public void Bars_start_on_the_downbeat(double beat, int downbeat, int expected)
    {
        var grid = BeatGrid.Constant(120, 0, 60).WithDownbeat(downbeat);

        Assert.Equal(expected, grid.BarPosition(beat));
        Assert.Equal(expected == 0, grid.IsBarStart(beat));
    }

    [Fact]
    public void A_downbeat_given_out_of_range_wraps_round_the_bar()
    {
        Assert.Equal(3, BeatGrid.Constant(120, 0, 60).WithDownbeat(-1).DownbeatOffset);
        Assert.Equal(1, BeatGrid.Constant(120, 0, 60).WithDownbeat(9).DownbeatOffset);
    }

    [Fact]
    public void A_grid_needs_markers_in_order()
    {
        Assert.Throws<ArgumentException>(() => new BeatGrid([], 60));
        Assert.Throws<ArgumentException>(() => new BeatGrid([new GridMarker(5, 120), new GridMarker(5, 100)], 60));
    }

    [Fact]
    public void Grids_are_equal_by_their_markers_length_and_downbeat()
    {
        Assert.Equal(BeatGrid.Constant(120, 0.5, 60), BeatGrid.Constant(120, 0.5, 60));
        Assert.NotEqual(BeatGrid.Constant(120, 0.5, 60), BeatGrid.Constant(120, 0.5, 60).WithDownbeat(1));
        Assert.NotEqual(BeatGrid.Constant(120, 0.5, 60), BeatGrid.Constant(121, 0.5, 60));
    }
}
