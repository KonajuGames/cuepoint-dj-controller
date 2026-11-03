using Brutality.Midi;

namespace Brutality.Tests.Midi;

public class ControllerMappingTests
{
    static MidiBinding Note(string control, int number, int channel = 0) => new() { Control = control, Kind = MidiKind.Note, Channel = channel, Number = number };

    static ControllerMapping Preset() =>
        new() { Device = "Pad", Bindings = [Note("A.play", 11), Note("A.cue", 12), Note("A.sync", 13), Note("A.slip", 64)] };

    static IEnumerable<(string, int)> Bound(ControllerMapping mapping) => mapping.Bindings.Select(binding => (binding.Control, binding.Number)).Order();

    [Fact]
    public void Controls_added_to_the_preset_since_come_through_beside_what_was_changed_by_hand()
    {
        // Saved when the preset was play, cue and sync, with cue moved to another button and sync unbound.
        var saved = new ControllerMapping { Device = "Pad", Bindings = [Note("A.play", 11), Note("A.cue", 12), Note("A.sync", 13)] };
        saved.Bind(Note("A.cue", 20));
        saved.Unbind("A.sync");

        var mapping = ControllerMapping.Over(Preset(), saved);

        Assert.Equal(new[] { ("A.cue", 20), ("A.play", 11), ("A.slip", 64) }, Bound(mapping));
        Assert.Equal(new[] { "A.cue", "A.sync" }, mapping.Changed!.Order());
    }

    [Fact]
    public void A_preset_binding_whose_button_was_given_to_another_control_stays_out()
    {
        var saved = new ControllerMapping { Device = "Pad" };
        saved.Bind(Note("A.sync", 11));

        var mapping = ControllerMapping.Over(Preset(), saved);

        // Play's button is sync's now, so play is left unbound rather than sharing it.
        Assert.DoesNotContain(mapping.Bindings, binding => binding.Control == "A.play");
        Assert.Equal(new[] { ("A.cue", 12), ("A.slip", 64), ("A.sync", 11) }, Bound(mapping));
    }

    [Fact]
    public void A_control_given_another_control_s_button_counts_that_one_as_changed()
    {
        var mapping = Preset();

        mapping.Bind(Note("A.sync", 12));

        Assert.Equal(new[] { "A.cue", "A.sync" }, mapping.Changed!.Order());
    }

    [Fact]
    public void A_mapping_saved_before_changes_were_kept_takes_what_it_binds_as_changed()
    {
        var saved = new ControllerMapping { Device = "Pad", Bindings = [Note("A.play", 11), Note("A.cue", 30)] };

        var mapping = ControllerMapping.Over(Preset(), saved);

        Assert.Equal(new[] { ("A.cue", 30), ("A.play", 11), ("A.slip", 64), ("A.sync", 13) }, Bound(mapping));
    }

    [Fact]
    public void What_was_changed_by_hand_is_saved_and_read_back()
    {
        using var folder = new TempFolder();
        var mapping = Preset();
        mapping.Unbind("A.slip");

        Assert.Null(mapping.Save(folder["Pad.json"]));
        var loaded = ControllerMapping.Load(folder["Pad.json"])!;

        Assert.Equal(new[] { "A.slip" }, loaded.Changed);
        Assert.DoesNotContain(ControllerMapping.Over(Preset(), loaded).Bindings, binding => binding.Control == "A.slip");
    }

    [Fact]
    public void The_DDJ_SB3_s_newer_controls_reach_a_mapping_saved_without_them()
    {
        var preset = ControllerPresets.For("DDJ-SB3")!;
        var saved = new ControllerMapping
        {
            Device = "DDJ-SB3",
            Bindings = preset.Bindings.Where(binding => !binding.Control.EndsWith(".slip") && !binding.Control.Contains(".jump")).ToList(),
        };

        var mapping = ControllerMapping.Over(preset, saved);

        Assert.Contains(mapping.Bindings, binding => binding.Control == "A.slip");
        Assert.Contains(mapping.Bindings, binding => binding.Control == "B.jump");
        Assert.Equal(preset.Bindings.Count, mapping.Bindings.Count);
    }
}
