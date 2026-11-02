using Brutality.Audio.Analysis;

namespace Brutality.Tests.Analysis;

public class GridEditingTests
{
    // 120 BPM with a beat on the start, bars starting on beat 1 (so beat 4 is the last of its bar).
    static GridEdit Fresh() => new(BeatGrid.Constant(120, 0, 60).WithDownbeat(1), []);

    [Fact]
    public void The_first_pin_shifts_the_whole_grid()
    {
        var edit = GridEditing.SetBeat(Fresh(), 2.02);

        Assert.Equal(new[] { 2.02 }, edit.Pins);
        Assert.Equal(0.02, edit.Grid.Markers[0].Time, 9);
        Assert.Equal(120, edit.Grid.Markers[0].Bpm);
        Assert.Equal(1, edit.Grid.DownbeatOffset);
    }

    [Fact]
    public void A_second_pin_sets_the_tempo_between_them_keeping_the_bars()
    {
        var before = Fresh();
        var once = GridEditing.SetBeat(before, 2.02);
        // Beat 8 of the grid, at 4.02 after the shift, dragged to 4.12.
        var twice = GridEditing.MoveBeat(once, 8, 4.12);
        var grid = twice.Grid;

        Assert.Equal(new[] { 2.02, 4.12 }, twice.Pins);
        Assert.Equal(60 * 4 / 2.1, grid.BpmAt(3), 6);
        // Both pins sit exactly on beats, four apart.
        Assert.Equal(grid.BeatNumberAt(2.02) + 4, grid.BeatNumberAt(4.12), 6);
        // The beat at the second pin is still where it was in its bar.
        Assert.Equal(before.Grid.BarPosition(8), grid.BarPosition(Math.Round(grid.BeatNumberAt(4.12))));
    }

    [Fact]
    public void Three_pins_make_a_grid_that_changes_tempo()
    {
        var edit = GridEditing.SetBeat(Fresh(), 0);
        edit = GridEditing.MoveBeat(edit, 8, 4);
        edit = GridEditing.MoveBeat(edit, 16, 7.5);

        Assert.True(edit.Grid.IsVariable);
        Assert.Equal(120, edit.Grid.BpmAt(2), 6);
        Assert.Equal(60 * 8 / 3.5, edit.Grid.BpmAt(5), 6);
    }

    [Fact]
    public void A_beat_dragged_past_its_neighbour_stops_short_of_it()
    {
        var edit = GridEditing.SetBeat(Fresh(), 0);
        edit = GridEditing.MoveBeat(edit, 8, 4);

        // Beat 8 dragged right back past beat 0's pin.
        var dragged = GridEditing.MoveBeat(edit, 8, -1);

        Assert.True(dragged.Pins[1] > dragged.Pins[0]);
        Assert.Equal(2, dragged.Pins[1], 6);
    }

    [Fact]
    public void Removing_a_pin_makes_the_grid_from_those_left()
    {
        var edit = GridEditing.SetBeat(Fresh(), 0);
        edit = GridEditing.MoveBeat(edit, 8, 4);
        edit = GridEditing.MoveBeat(edit, 16, 7.5);

        var removed = GridEditing.RemovePin(edit, 7.4);

        Assert.Equal(new[] { 0.0, 4.0 }, removed.Pins);
        Assert.False(removed.Grid.IsVariable);
        Assert.Equal(120, removed.Grid.BpmAt(10), 6);
    }

    [Fact]
    public void Nudging_moves_the_grid_and_its_pin()
    {
        var edit = GridEditing.SetBeat(Fresh(), 1);

        var nudged = GridEditing.Nudge(edit, GridEditing.NUDGE, playhead: 0);

        Assert.Equal(1 + GridEditing.NUDGE, nudged.Pins[0], 9);
        Assert.Equal(edit.Grid.Markers[0].Time + GridEditing.NUDGE, nudged.Grid.Markers[0].Time, 9);
    }

    [Fact]
    public void Setting_the_downbeat_makes_that_beat_the_one()
    {
        var edit = GridEditing.Downbeat(Fresh(), 1.5);

        Assert.Equal(0, edit.Grid.BarPosition(3));
    }

    [Fact]
    public void Doubling_and_halving_the_tempo_keeps_pins_that_still_fall_on_beats()
    {
        var edit = GridEditing.SetBeat(Fresh(), 0);
        edit = edit with { Pins = [0, 0.5, 1] };

        var doubled = GridEditing.Scale(edit, 2);
        var halved = GridEditing.Scale(edit, 0.5);

        Assert.Equal(240, doubled.Grid.Markers[0].Bpm);
        Assert.Equal(new[] { 0, 0.5, 1 }, doubled.Pins);
        Assert.Equal(60, halved.Grid.Markers[0].Bpm);
        Assert.Equal(new[] { 0.0, 1.0 }, halved.Pins);
    }

    [Fact]
    public void Making_a_drifting_grid_steady_keeps_the_beat_at_the_playhead()
    {
        var drifting = new GridEdit(new BeatGrid([new GridMarker(1, 120), new GridMarker(5, 60)], 20), []);

        var steady = GridEditing.MakeSteady(drifting, playhead: 7.1);

        Assert.False(steady.Grid.IsVariable);
        Assert.Equal(60, steady.Grid.Markers[0].Bpm);
        Assert.Equal(new[] { 7.0 }, steady.Pins);
        Assert.Equal(Math.Round(steady.Grid.BeatNumberAt(7)), steady.Grid.BeatNumberAt(7), 6);
    }
}
