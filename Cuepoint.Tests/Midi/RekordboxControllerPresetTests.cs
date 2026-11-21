using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

/// <summary>
/// The DDJ-FLX4's and DDJ-400's built-in mappings, checked against Pioneer's lists of MIDI messages for them
/// (DDJ-FLX4 ver 1.0, DDJ-400 ver 1.00). Channels are counted from 0 here, as on the wire: the lists' channel 1 is 0.
/// </summary>
public class RekordboxControllerPresetTests
{
    static readonly ControllerMapping Flx4 = ControllerPresets.For("DDJ-FLX4")!;
    static readonly ControllerMapping Ddj400 = ControllerPresets.For("DDJ-400")!;

    public static TheoryData<string> Models => ["DDJ-FLX4", "DDJ-400"];

    static ControllerMapping Mapping(string model) => ControllerPresets.For(model)!;

    static (int Channel, int Number)[] Messages(ControllerMapping mapping, string control, MidiKind kind = MidiKind.Note) =>
        mapping.Bindings.Where(binding => binding.Control == control && binding.Kind == kind).Select(binding => (binding.Channel, binding.Number)).Order().ToArray();

    [Theory]
    [MemberData(nameof(Models))]
    public void Every_control_bound_is_one_the_app_knows(string model)
    {
        var known = ControlCatalog.For(["A", "B"]).Select(control => control.Id).ToHashSet();
        Assert.All(Mapping(model).Bindings, binding => Assert.Contains(binding.Control, known));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Only_the_shared_Beat_FX_section_works_both_decks_at_once(string model)
    {
        var shared = Mapping(model)
            .Bindings.GroupBy(binding => (binding.Kind, binding.Channel, binding.Number))
            .Where(group => group.Select(binding => binding.Control).Distinct().Count() > 1)
            .SelectMany(group => group.Select(binding => binding.Control));
        Assert.All(shared, control => Assert.Matches(@"^[AB]\.fx\.(next|shorter|longer|level)$", control));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void The_transport_mixer_and_browser_are_where_the_lists_say(string model)
    {
        var mapping = Mapping(model);
        // PLAY/PAUSE 0x0B, CUE 0x0C, BEAT SYNC 0x58 and CH CUE 0x54 on each deck's channel.
        Assert.Equal([(0, 0x0B)], Messages(mapping, "A.play"));
        Assert.Equal([(1, 0x0C)], Messages(mapping, "B.cue"));
        Assert.Equal([(0, 0x58)], Messages(mapping, "A.sync"));
        Assert.Equal([(1, 0x54)], Messages(mapping, "B.pfl"));
        // TEMPO CC 0x00, CH FADER 0x13 and EQ LOW 0x0F, each with its fine half 32 above.
        Assert.Equal([(0, 0x00)], Messages(mapping, "A.tempo", MidiKind.ControlChange));
        Assert.Equal(0x33, mapping.Bindings.Single(binding => binding.Control == "B.volume").Fine);
        Assert.Equal([(1, 0x0F)], Messages(mapping, "B.eq.low", MidiKind.ControlChange));
        // CROSSFADER CC 0x1F, the filters (CFX or FILTER) 0x17 and 0x18, BROWSE 0x40 and LOAD 0x46 and 0x47, on 7.
        Assert.Equal([(6, 0x1F)], Messages(mapping, "crossfader", MidiKind.ControlChange));
        Assert.Equal([(6, 0x18)], Messages(mapping, "B.filter", MidiKind.ControlChange));
        Assert.Equal([(6, 0x40)], Messages(mapping, "browse", MidiKind.ControlChange));
        Assert.Equal([(6, 0x47)], Messages(mapping, "B.load"));
        // The jog wheel: touched 0x36 (Shift 0x67), its top 0x22 to scratch, its edge 0x21, and Shift + top 0x29.
        Assert.Equal([(0, 0x36), (0, 0x67)], Messages(mapping, "A.jog.touch"));
        Assert.Equal([(0, 0x22)], Messages(mapping, "A.scratch", MidiKind.ControlChange));
        Assert.Equal([(1, 0x29)], Messages(mapping, "B.search", MidiKind.ControlChange));
        // MASTER LEVEL sets the controller's own output, so it isn't the app's master too.
        Assert.Empty(Messages(mapping, "master", MidiKind.ControlChange));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void The_pads_follow_the_controller_s_modes(string model)
    {
        var mapping = Mapping(model);
        // Deck 1's pads on channel 8 (Shift 9), deck 2's on 10 (Shift 11): HOT CUE from 0x00, PAD FX 1 from 0x10, BEAT
        // JUMP from 0x20 and SAMPLER from 0x30.
        Assert.Equal([(7, 0x00), (7, 0x30)], Messages(mapping, "A.hotcue1"));
        Assert.Equal([(10, 0x07)], Messages(mapping, "B.hotcue8.delete"));
        Assert.Equal([(9, 0x12)], Messages(mapping, "B.roll3"));
        Assert.Equal([(7, 0x24)], Messages(mapping, "A.jump5"));
        // The mode buttons: HOT CUE 0x1B, PAD FX 1 0x1E, BEAT JUMP 0x20 and SAMPLER 0x22.
        Assert.Equal([(0, 0x1B)], Messages(mapping, "A.hotcues"));
        Assert.Equal([(1, 0x1E)], Messages(mapping, "B.roll"));
        Assert.Equal([(0, 0x20)], Messages(mapping, "A.jump"));
        Assert.Equal([(1, 0x22)], Messages(mapping, "B.sampler"));
    }

    [Fact]
    public void Each_has_its_own_loop_buttons()
    {
        // FLX4: IN 0x10, OUT 0x11, 4 BEAT/EXIT 0x4D (Shift 0x50).
        Assert.Equal([(0, 0x4D)], Messages(Flx4, "A.loop"));
        Assert.Equal([(0, 0x50)], Messages(Flx4, "A.reloop"));
        Assert.Empty(Messages(Flx4, "A.loop.exit"));
        // 400: LOOP IN/4BEAT 0x10 (held, 0x14), OUT 0x11, RELOOP/EXIT 0x4D (Shift 0x50).
        Assert.Equal([(1, 0x14)], Messages(Ddj400, "B.loop.beats4"));
        Assert.Equal([(1, 0x4D)], Messages(Ddj400, "B.loop.exit"));
        Assert.Equal([(1, 0x50)], Messages(Ddj400, "B.reloop"));
        Assert.Equal([(0, 0x11)], Messages(Ddj400, "A.loop.out"));
    }

    [Fact]
    public void The_FLX4_s_FX_ON_comes_for_the_channel_chosen_and_the_400_s_follows_CH_SELECT()
    {
        // FX ON/OFF is note 0x47 on channel 5 for CH1 and 6 for CH2 on the FLX4; the 400 says no channel, but its
        // CH SELECT sends 0x10, 0x11 or 0x14 on channel 5 as it's set to CH 1, CH 2 or MASTER.
        Assert.Equal([(4, 0x47)], Messages(Flx4, "A.fx.on"));
        Assert.Equal([(5, 0x47)], Messages(Flx4, "B.fx.on"));
        Assert.Empty(Messages(Ddj400, "A.fx.on"));
        Assert.Equal([(4, 0x47)], Messages(Ddj400, "fx.on"));
        Assert.Equal([(4, 0x10)], Messages(Ddj400, "fx.select.A"));
        Assert.Equal([(4, 0x14)], Messages(Ddj400, "fx.select.both"));
        Assert.Empty(Messages(Flx4, "fx.on"));
        Assert.Equal([(4, 0x63)], Messages(Ddj400, "B.fx.next"));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void BEAT_LOOP_and_KEY_SHIFT_pads_are_where_the_lists_say(string model)
    {
        var mapping = Mapping(model);
        Assert.Equal([(7, 0x60)], Messages(mapping, "A.beatloop1"));
        Assert.Equal([(9, 0x67)], Messages(mapping, "B.beatloop8"));
        Assert.Equal([(7, 0x73)], Messages(mapping, "A.keyshift4"));
        Assert.Equal([(9, 0x74)], Messages(mapping, "B.keyshift5"));
    }
}
