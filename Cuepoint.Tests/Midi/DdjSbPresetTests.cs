using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

/// <summary>
/// The DDJ-SB's built-in mapping, checked against Pioneer's list of MIDI messages for it (ver. 1.00). Channels are
/// counted from 0 here, as on the wire: the list's channel 1 is 0.
/// </summary>
public class DdjSbPresetTests
{
    static readonly ControllerMapping Sb = ControllerPresets.For("DDJ-SB")!;

    static (int Channel, int Number)[] Messages(string control, MidiKind kind = MidiKind.Note) =>
        Sb.Bindings.Where(binding => binding.Control == control && binding.Kind == kind).Select(binding => (binding.Channel, binding.Number)).Order().ToArray();

    [Fact]
    public void It_is_found_by_either_name_the_controller_might_give()
    {
        Assert.NotNull(ControllerPresets.For("PIONEER DDJ-SB"));
        Assert.Equal("PIONEER DDJ-SB", ControllerPresets.For("PIONEER DDJ-SB")!.Device);
    }

    [Fact]
    public void Every_control_bound_is_one_the_app_knows()
    {
        var known = ControlCatalog.For(["A", "B"]).Select(control => control.Id).ToHashSet();
        Assert.All(Sb.Bindings, binding => Assert.Contains(binding.Control, known));
    }

    [Fact]
    public void No_message_works_two_controls()
    {
        var shared = Sb.Bindings.GroupBy(binding => (binding.Kind, binding.Channel, binding.Number)).Where(group => group.Select(binding => binding.Control).Distinct().Count() > 1);
        Assert.Empty(shared);
    }

    [Fact]
    public void The_transport_mixer_and_browser_are_where_the_list_says()
    {
        // PLAY/PAUSE 0x0B, CUE 0x0C, SYNC 0x58, CUE (headphones) 0x54, KEYLOCK 0x1A and Shift + VINYL 0x4E.
        Assert.Equal([(0, 0x0B)], Messages("A.play"));
        Assert.Equal([(1, 0x0C)], Messages("B.cue"));
        Assert.Equal([(0, 0x58)], Messages("A.sync"));
        Assert.Equal([(1, 0x54)], Messages("B.pfl"));
        Assert.Equal([(0, 0x1A)], Messages("A.keylock"));
        Assert.Equal([(1, 0x4E)], Messages("B.slip"));
        // TEMPO 0x00, CH FADER 0x13 and the EQs 0x07, 0x0B and 0x0F, each with its fine half 32 above.
        Assert.Equal([(0, 0x00)], Messages("A.tempo", MidiKind.ControlChange));
        Assert.Equal(0x33, Sb.Bindings.Single(binding => binding.Control == "B.volume").Fine);
        Assert.Equal([(0, 0x07)], Messages("A.eq.high", MidiKind.ControlChange));
        Assert.Equal([(1, 0x0B)], Messages("B.eq.mid", MidiKind.ControlChange));
        Assert.Equal([(0, 0x0F)], Messages("A.eq.low", MidiKind.ControlChange));
        // FILTER 0x17 and 0x18, CROSSFADER 0x1F, BROWSE 0x40 (Shift 0x64), its press 0x41 and LOAD 0x46 and 0x47, on 7.
        Assert.Equal([(6, 0x17)], Messages("A.filter", MidiKind.ControlChange));
        Assert.Equal([(6, 0x18)], Messages("B.filter", MidiKind.ControlChange));
        Assert.Equal([(6, 0x1F)], Messages("crossfader", MidiKind.ControlChange));
        Assert.Equal([(6, 0x40)], Messages("browse", MidiKind.ControlChange));
        Assert.Equal([(6, 0x64)], Messages("browse.folders", MidiKind.ControlChange));
        Assert.Equal([(6, 0x41)], Messages("browse.press"));
        Assert.Equal([(6, 0x46)], Messages("A.load"));
        Assert.Equal([(6, 0x47)], Messages("B.load"));
        Assert.Equal(StepEncoding.TwosComplement, Sb.Bindings.Single(binding => binding.Control == "browse").Steps);
    }

    [Fact]
    public void It_has_no_trim_knobs()
    {
        Assert.DoesNotContain(Sb.Bindings, binding => binding.Control.EndsWith(".trim"));
    }

    [Fact]
    public void The_jog_wheel_scratches_only_with_VINYL_on()
    {
        // Touched 0x36 with VINYL on (Shift 0x67); 0x35 with it off, which mustn't hold the record.
        Assert.Equal([(0, 0x36), (0, 0x67)], Messages("A.jog.touch"));
        Assert.DoesNotContain(Sb.Bindings, binding => binding.Kind == MidiKind.Note && binding.Number == 0x35);
        // The top turned: 0x22 with VINYL on, 0x23 with it off; the edge 0x21; with Shift 0x1F and 0x26.
        Assert.Equal([(1, 0x22)], Messages("B.scratch", MidiKind.ControlChange));
        Assert.Equal([(0, 0x21), (0, 0x23)], Messages("A.jog", MidiKind.ControlChange));
        Assert.Equal([(1, 0x1F), (1, 0x26)], Messages("B.search", MidiKind.ControlChange));
        // VINYL itself (0x17) is the controller's own.
        Assert.DoesNotContain(Sb.Bindings, binding => binding.Channel < 2 && binding.Kind == MidiKind.Note && binding.Number == 0x17);
    }

    [Fact]
    public void Each_decks_effects_section_works_its_effect()
    {
        // FX1 on channel 5, FX2 on 6: FX-1 ON 0x47 (Shift 0x63), FX-2 ON 0x48, FX-3 ON 0x49 and the FX-1 knob 0x02.
        Assert.Equal([(4, 0x47)], Messages("A.fx.on"));
        Assert.Equal([(5, 0x63)], Messages("B.fx.next"));
        Assert.Equal([(4, 0x48)], Messages("A.fx.shorter"));
        Assert.Equal([(5, 0x49)], Messages("B.fx.longer"));
        Assert.Equal([(4, 0x02)], Messages("A.fx.level", MidiKind.ControlChange));
        Assert.Equal(0x22, Sb.Bindings.Single(binding => binding.Control == "B.fx.level").Fine);
    }

    [Fact]
    public void The_mode_buttons_put_the_pads_in_the_matching_mode()
    {
        // HOT CUE 0x1B and Shift + HOT CUE (HOT CUE 5-8) 0x69, Shift + AUTO LOOP (ROLL) 0x6B and SAMPLER 0x22.
        Assert.Equal([(0, 0x1B), (0, 0x69)], Messages("A.hotcues"));
        Assert.Equal([(1, 0x6B)], Messages("B.roll"));
        Assert.Equal([(0, 0x22)], Messages("A.sampler"));
    }

    [Fact]
    public void The_pads_send_notes_of_their_own_in_each_mode()
    {
        for (var pad = 1; pad <= 4; pad++)
        {
            var note = pad - 1;
            // HOT CUE 0x00 and SAMPLER 0x30 work hotcues 1 to 4; HOT CUE 5-8 0x40 works 5 to 8; Shift is 8 higher.
            Assert.Equal([(7, note), (7, 0x30 + note)], Messages($"A.hotcue{pad}"));
            Assert.Equal([(8, 0x08 + note)], Messages($"B.hotcue{pad}.delete"));
            Assert.Equal([(7, 0x40 + note)], Messages($"A.hotcue{pad + 4}"));
            Assert.Equal([(8, 0x48 + note)], Messages($"B.hotcue{pad + 4}.delete"));
            // AUTO LOOP 0x10 and ROLL 0x50.
            Assert.Equal([(7, 0x10 + note)], Messages($"A.beatloop{pad + 2}"));
            Assert.Equal([(8, 0x50 + note)], Messages($"B.roll{pad + 2}"));
        }
        Assert.Equal([1.0, 2, 4, 8], Enumerable.Range(3, 4).Select(pad => ControlCatalog.BeatLoopSizes[pad - 1]));
    }

    [Theory]
    // MANUAL LOOP's pads from 0x20, and with Shift from 0x28.
    [InlineData("loop.in", 0x20)]
    [InlineData("loop.out", 0x21)]
    [InlineData("loop.exit", 0x22)]
    [InlineData("loop.halve", 0x23)]
    [InlineData("loop.back", 0x28)]
    [InlineData("loop.on", 0x29)]
    [InlineData("reloop", 0x2A)]
    [InlineData("loop.double", 0x2B)]
    public void MANUAL_LOOPs_pads_set_and_change_loops(string action, int note)
    {
        Assert.Equal([(7, note)], Messages($"A.{action}"));
        Assert.Equal([(8, note)], Messages($"B.{action}"));
    }
}
