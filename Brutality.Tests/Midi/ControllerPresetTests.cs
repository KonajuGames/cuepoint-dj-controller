using Brutality.Midi;

namespace Brutality.Tests.Midi;

public class ControllerPresetTests
{
    static readonly ControllerMapping Ddj = ControllerPresets.For("DDJ-SB3")!;

    static IEnumerable<MidiBinding> Notes(string control) => Ddj.Bindings.Where(binding => binding.Control == control && binding.Kind == MidiKind.Note);

    [Fact]
    public void Every_control_the_preset_binds_is_one_the_app_knows()
    {
        var known = ControlCatalog.For(["A", "B"]).Select(control => control.Id).ToHashSet();

        Assert.All(Ddj.Bindings, binding => Assert.Contains(binding.Control, known));
    }

    [Fact]
    public void Shift_and_HOT_CUE_puts_each_deck_in_beat_jump_mode()
    {
        // Pioneer's list of MIDI messages: SHIFT + HOT CUE is note 0x69 on the deck's own channel, by HOT CUE's 0x1B.
        Assert.Equal((0, 0x69), Notes("A.jump").Select(binding => (binding.Channel, binding.Number)).Single());
        Assert.Equal((1, 0x69), Notes("B.jump").Select(binding => (binding.Channel, binding.Number)).Single());
        Assert.Equal((0, 0x1B), Notes("A.hotcues").Select(binding => (binding.Channel, binding.Number)).Single());
    }

    [Fact]
    public void In_beat_jump_mode_the_pads_send_their_own_notes_with_or_without_shift()
    {
        for (var pad = 1; pad <= 8; pad++)
        {
            Assert.Equal(new[] { (7, 0x3F + pad), (7, 0x47 + pad) }, Notes($"A.jump{pad}").Select(binding => (binding.Channel, binding.Number)).Order());
            Assert.Equal(new[] { (8, 0x3F + pad), (8, 0x47 + pad) }, Notes($"B.jump{pad}").Select(binding => (binding.Channel, binding.Number)).Order());
        }
    }

    [Theory]
    // Shift + 1/2X, Shift + 2X and Shift + AUTO LOOP, on each deck's own channel.
    [InlineData("loop.in", 0x61)]
    [InlineData("loop.out", 0x62)]
    [InlineData("reloop", 0x50)]
    public void Shift_and_the_loop_buttons_set_loops_by_hand(string action, int note)
    {
        Assert.Equal((0, note), Notes($"A.{action}").Select(binding => (binding.Channel, binding.Number)).Single());
        Assert.Equal((1, note), Notes($"B.{action}").Select(binding => (binding.Channel, binding.Number)).Single());
    }

    [Fact]
    public void No_two_controls_share_a_message()
    {
        // A control can have more than one message (the jog wheel's top and edge), but not the other way round.
        var shared = Ddj.Bindings
            .GroupBy(binding => (binding.Kind, binding.Channel, binding.Number))
            .Where(group => group.Select(binding => binding.Control).Distinct().Count() > 1)
            .Select(group => group.Key);

        Assert.Empty(shared);
    }
}
