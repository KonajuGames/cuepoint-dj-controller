using Cuepoint.Music;

namespace Cuepoint.Midi;

/// <summary>
/// Built-in mappings for controllers known by name, used until a control is learned differently.
/// </summary>
static class ControllerPresets
{
    public static ControllerMapping? For(string device) =>
        device switch
        {
            "DDJ-SB3" => PioneerDdj(device),
            // Older Pioneer controllers give their maker's name as well; which the DDJ-SB gives isn't known yet.
            "DDJ-SB" or "PIONEER DDJ-SB" => PioneerDdjSb(device),
            "DDJ-FLX4" => PioneerDdjRekordbox(device, flx4: true),
            "DDJ-400" => PioneerDdjRekordbox(device, flx4: false),
            _ => null,
        };

    /// <summary>
    /// Pioneer's DDJ layout, as on the DDJ-SB3: each deck's controls on its own channel (deck 1 on 1, deck 2 on 2),
    /// its pads on another (8 and 9), and the shared mixer and browser on 7. Shift + FX FADE puts the pads in ROLL
    /// mode, and HOT CUE back to hotcues; the pads send the same notes either way. Shift + HOT CUE puts them in BEAT
    /// JUMP mode, where they send notes of their own (0x40 up, Shift 8 higher still), in pairs, back then on, by 4
    /// and 8 beats along the top row and 16 and 32 along the bottom.
    ///
    /// FX FADE's pads (notes 16 to 23) play transition effects by working other controls over time, which is mostly
    /// all there is to map for them: the filter swept, the channel fader faded, and loops set (notes 0x4A and 0x4C, 4
    /// and 1 beats), halved (the halve button's own note) and left (0x4D). The backspins (pads 4 and 8) send the same
    /// flick of the wheel as each other, so they're told apart by their pads. PAD SCRATCH's patterns touch the wheel
    /// and turn it (CC 0x22, the record held), with fader cuts, from hotcue 4. Faders and knobs send fourteen bits,
    /// the fine half 32 above the coarse. Shift sends different messages rather than changing what the app does, so
    /// shifted controls are bound like any other: pads with Shift held come 8 notes higher, and the jog wheel
    /// turned with Shift comes on its own controls.
    /// </summary>
    static ControllerMapping PioneerDdj(string device)
    {
        var mapping = new ControllerMapping { Device = device };

        void Note(string control, int channel, int note) =>
            mapping.Bindings.Add(new MidiBinding { Control = control, Kind = MidiKind.Note, Channel = channel, Number = note });
        void Control(string control, int channel, int number, bool fine = true, StepEncoding steps = StepEncoding.Offset64) =>
            mapping.Bindings.Add(new MidiBinding
            {
                Control = control,
                Kind = MidiKind.ControlChange,
                Channel = channel,
                Number = number,
                Fine = fine ? number + 32 : null,
                Steps = steps,
            });

        const int MIXER = 6;
        string[] decks = ["A", "B"];
        for (var i = 0; i < decks.Length; i++)
        {
            var deck = decks[i];
            var channel = i;
            var pads = 7 + i;

            Note($"{deck}.play", channel, 0x0B);
            Note($"{deck}.cue", channel, 0x0C);
            Note($"{deck}.sync", channel, 0x58);
            Note($"{deck}.pfl", channel, 0x54);
            Note($"{deck}.keylock", channel, 0x1A);
            // Shift + VINYL is slip.
            Note($"{deck}.slip", channel, 0x40);
            // VINYL has nothing else to do here, so it's the effect on and off; Shift + KEY LOCK the next effect.
            Note($"{deck}.fx.on", channel, 0x17);
            Note($"{deck}.fx.next", channel, 0x60);
            Note($"{deck}.loop", channel, 0x14);
            Note($"{deck}.loop.halve", channel, 0x12);
            Note($"{deck}.loop.double", channel, 0x13);
            Note($"{deck}.loop.beats4", channel, 0x4A);
            Note($"{deck}.loop.beats1", channel, 0x4C);
            Note($"{deck}.loop.exit", channel, 0x4D);
            // Shift + 1/2X and Shift + 2X are loop in and out, and Shift + AUTO LOOP reloops.
            Note($"{deck}.loop.in", channel, 0x61);
            Note($"{deck}.loop.out", channel, 0x62);
            Note($"{deck}.reloop", channel, 0x50);
            for (var pad = 0; pad < ControlCatalog.HOTCUES; pad++)
            {
                Note($"{deck}.hotcue{pad + 1}", pads, pad);
                Note($"{deck}.hotcue{pad + 1}.delete", pads, pad + 8);
                Note(pad switch { 3 => $"{deck}.backspin.short", 7 => $"{deck}.backspin.long", _ => $"{deck}.fxfade" }, pads, 0x10 + pad);
            }
            Note($"{deck}.hotcues", channel, 0x1B);
            Note($"{deck}.roll", channel, 0x6B);
            Note($"{deck}.jump", channel, 0x69);
            for (var pad = 0; pad < JumpPads.Sizes.Count; pad++)
            {
                Note($"{deck}.jump{pad + 1}", pads, 0x40 + pad);
                Note($"{deck}.jump{pad + 1}", pads, 0x48 + pad);
            }

            // Pioneer's tempo sliders are minus at the top, where they send 0.
            Control($"{deck}.tempo", channel, 0x00);
            Control($"{deck}.volume", channel, 0x13);
            Control($"{deck}.trim", channel, 0x04);
            Control($"{deck}.eq.high", channel, 0x07);
            Control($"{deck}.eq.mid", channel, 0x0B);
            Control($"{deck}.eq.low", channel, 0x0F);
            Control($"{deck}.filter", MIXER, 0x17 + i);

            // The wheel's top (touched) and its outer edge both nudge or move through the song.
            Control($"{deck}.jog", channel, 0x21, fine: false);
            mapping.Bindings.Add(new MidiBinding { Control = $"{deck}.jog", Kind = MidiKind.ControlChange, Channel = channel, Number = 0x23 });
            Note($"{deck}.jog.touch", channel, 0x36);
            Note($"{deck}.jog.touch", channel, 0x67);
            Control($"{deck}.scratch", channel, 0x22, fine: false);
            Control($"{deck}.search", channel, 0x1F, fine: false);
            Control($"{deck}.search", channel, 0x26, fine: false);

            Note($"{deck}.load", MIXER, 0x46 + i);
        }

        Control("crossfader", MIXER, 0x1F);
        Control("browse", MIXER, 0x40, fine: false, steps: StepEncoding.TwosComplement);
        Note("browse.press", MIXER, 0x41);
        return mapping;
    }

    /// <summary>
    /// The DDJ-SB's layout, from Pioneer's list of MIDI messages for it (ver. 1.00): the SB3's predecessor, with the
    /// same channels (decks on 1 and 2, the mixer and browser on 7, the pads on 8 and 9) and an effects section for
    /// each deck on 5 and 6. Its four pads a deck send notes of their own in each mode, 16 apart: HOT CUE from 0x00,
    /// AUTO LOOP 0x10, MANUAL LOOP 0x20, SAMPLER 0x30, HOT CUE 5-8 (Shift + HOT CUE) 0x40, ROLL (Shift + AUTO LOOP)
    /// 0x50, with Shift 8 higher. The modes whose pads work hotcues put the deck's pads in hotcue mode here too, ROLL
    /// in roll mode and SAMPLER in sampler mode, as a hotcue pad plays a sample there. AUTO LOOP's pads start loops
    /// of 1, 2, 4 and 8 beats; MANUAL LOOP's are loop in, loop out, out of the loop and halve it (with Shift: move
    /// it back and on a beat, reloop, and double it). HOT LOOP (Shift + MANUAL LOOP) and BANK (Shift + SAMPLER) have
    /// nothing to match here.
    ///
    /// VINYL is left to the controller, which switches its wheel's top between scratching (0x22, touched 0x36) and
    /// nudging (0x23, touched 0x35, which mustn't hold the record) itself; Shift + VINYL is slip. Each deck's effects
    /// section works its effect: FX-1 ON turns it on and off (Shift, the next effect), FX-2 and FX-3 ON make it
    /// fewer or more beats, and the FX-1 knob is its level. The SB has no trim knobs, and its FILTER knobs are each
    /// channel's filter. HEADPHONES MIX is left alone: it's the controller's own headphone mix.
    /// </summary>
    static ControllerMapping PioneerDdjSb(string device)
    {
        var mapping = new ControllerMapping { Device = device };

        void Note(string control, int channel, int note) =>
            mapping.Bindings.Add(new MidiBinding { Control = control, Kind = MidiKind.Note, Channel = channel, Number = note });
        void Control(string control, int channel, int number, bool fine = true, StepEncoding steps = StepEncoding.Offset64) =>
            mapping.Bindings.Add(new MidiBinding
            {
                Control = control,
                Kind = MidiKind.ControlChange,
                Channel = channel,
                Number = number,
                Fine = fine ? number + 32 : null,
                Steps = steps,
            });

        const int MIXER = 6;
        const int PADS = 4;
        string[] decks = ["A", "B"];
        for (var i = 0; i < decks.Length; i++)
        {
            var deck = decks[i];
            var channel = i;
            var fx = 4 + i;
            var pads = 7 + i;

            Note($"{deck}.play", channel, 0x0B);
            Note($"{deck}.cue", channel, 0x0C);
            Note($"{deck}.sync", channel, 0x58);
            Note($"{deck}.pfl", channel, 0x54);
            Note($"{deck}.keylock", channel, 0x1A);
            Note($"{deck}.slip", channel, 0x4E);

            for (var pad = 0; pad < PADS; pad++)
            {
                Note($"{deck}.hotcue{pad + 1}", pads, pad);
                Note($"{deck}.hotcue{pad + 1}.delete", pads, 0x08 + pad);
                Note($"{deck}.hotcue{pad + 5}", pads, 0x40 + pad);
                Note($"{deck}.hotcue{pad + 5}.delete", pads, 0x48 + pad);
                // 1, 2, 4 and 8 beats, and rolls of 1/4, 1/2, 1 and 2.
                Note($"{deck}.beatloop{pad + 3}", pads, 0x10 + pad);
                Note($"{deck}.roll{pad + 3}", pads, 0x50 + pad);
                // SAMPLER's pads are the hotcue pads, which play samples while the deck's in SAMPLER mode.
                Note($"{deck}.hotcue{pad + 1}", pads, 0x30 + pad);
            }
            Note($"{deck}.loop.in", pads, 0x20);
            Note($"{deck}.loop.out", pads, 0x21);
            Note($"{deck}.loop.exit", pads, 0x22);
            Note($"{deck}.loop.halve", pads, 0x23);
            Note($"{deck}.loop.back", pads, 0x28);
            Note($"{deck}.loop.on", pads, 0x29);
            Note($"{deck}.reloop", pads, 0x2A);
            Note($"{deck}.loop.double", pads, 0x2B);

            // HOT CUE and Shift + HOT CUE (HOT CUE 5-8), Shift + AUTO LOOP (ROLL) and SAMPLER.
            Note($"{deck}.hotcues", channel, 0x1B);
            Note($"{deck}.hotcues", channel, 0x69);
            Note($"{deck}.roll", channel, 0x6B);
            Note($"{deck}.sampler", channel, 0x22);

            // Pioneer's tempo sliders are minus at the top, where they send 0.
            Control($"{deck}.tempo", channel, 0x00);
            Control($"{deck}.volume", channel, 0x13);
            Control($"{deck}.eq.high", channel, 0x07);
            Control($"{deck}.eq.mid", channel, 0x0B);
            Control($"{deck}.eq.low", channel, 0x0F);
            Control($"{deck}.filter", MIXER, 0x17 + i);

            // The wheel's edge nudges, as does its top with VINYL off; touched with VINYL on, the top scratches. With
            // Shift, the top and the edge search through the song.
            Control($"{deck}.jog", channel, 0x21, fine: false);
            Control($"{deck}.jog", channel, 0x23, fine: false);
            Note($"{deck}.jog.touch", channel, 0x36);
            Note($"{deck}.jog.touch", channel, 0x67);
            Control($"{deck}.scratch", channel, 0x22, fine: false);
            Control($"{deck}.search", channel, 0x1F, fine: false);
            Control($"{deck}.search", channel, 0x26, fine: false);

            Note($"{deck}.fx.on", fx, 0x47);
            Note($"{deck}.fx.next", fx, 0x63);
            Note($"{deck}.fx.shorter", fx, 0x48);
            Note($"{deck}.fx.longer", fx, 0x49);
            Control($"{deck}.fx.level", fx, 0x02);

            Note($"{deck}.load", MIXER, 0x46 + i);
        }

        Control("crossfader", MIXER, 0x1F);
        Control("browse", MIXER, 0x40, fine: false, steps: StepEncoding.TwosComplement);
        Control("browse.folders", MIXER, 0x64, fine: false, steps: StepEncoding.TwosComplement);
        Note("browse.press", MIXER, 0x41);
        return mapping;
    }

    /// <summary>
    /// The DDJ-FLX4's and DDJ-400's layout, from Pioneer's lists of MIDI messages (DDJ-FLX4 ver 1.0, DDJ-400 ver
    /// 1.00): each deck's controls on its own channel (1 and 2), its pads on another (8 and 10, with Shift 9 and 11),
    /// and the mixer and browser on 7, much as on the DDJ-SB3. The pads send notes 16 apart in each mode the
    /// controller has, from 0x00 in HOT CUE mode: 0x10 PAD FX 1, 0x20 BEAT JUMP and 0x30 SAMPLER. HOT CUE pads are
    /// hotcues, cleared with Shift; PAD FX 1's are loop rolls; BEAT JUMP's are beat jumps; and SAMPLER's play the
    /// sampler's pads. Each of those modes' buttons puts the deck's pads in its mode here too. BEAT LOOP's pads (from
    /// 0x60) start loops of 1/4 to 32 beats and KEY SHIFT's (from 0x70) shift the key, 1 to 4 semitones up along the
    /// top row and down along the bottom, whatever mode the screen's pads are in. KEYBOARD and PAD FX 2 have nothing
    /// to match here, so their pads do nothing.
    ///
    /// The loop buttons are IN and OUT, then on the FLX4 4 BEAT/EXIT, a four-beat loop or out of one, and on the 400
    /// RELOOP/EXIT, out of a loop or (with Shift) back into the last one; the 400's IN held is a four-beat loop. CUE/LOOP
    /// CALL halves and doubles the loop, and with Shift adds or removes a memory cue. The jog wheel's top (touched)
    /// scratches, as its vinyl mode is on unless an app turns it off; with Shift it searches. The FLX4's CFX knobs and
    /// the 400's FILTER knobs are each channel's filter. Beat FX: the FLX4 says which channel the FX ON/OFF is for, so
    /// it's that deck's effect on and off; the 400's doesn't, so it works the deck (or both) its CH SELECT was last set
    /// to. FX SELECT, BEAT and LEVEL/DEPTH set both decks' effects, as the controller has one Beat FX section. Neither
    /// has KEY LOCK or SLIP buttons.
    /// </summary>
    static ControllerMapping PioneerDdjRekordbox(string device, bool flx4)
    {
        var mapping = new ControllerMapping { Device = device };

        void Note(string control, int channel, int note) =>
            mapping.Bindings.Add(new MidiBinding { Control = control, Kind = MidiKind.Note, Channel = channel, Number = note });
        void Control(string control, int channel, int number, bool fine = true, StepEncoding steps = StepEncoding.Offset64) =>
            mapping.Bindings.Add(new MidiBinding
            {
                Control = control,
                Kind = MidiKind.ControlChange,
                Channel = channel,
                Number = number,
                Fine = fine ? number + 32 : null,
                Steps = steps,
            });

        const int FX = 4;
        const int MIXER = 6;
        string[] decks = ["A", "B"];
        for (var i = 0; i < decks.Length; i++)
        {
            var deck = decks[i];
            var channel = i;
            var pads = 7 + 2 * i;
            var shiftedPads = pads + 1;

            Note($"{deck}.play", channel, 0x0B);
            Note($"{deck}.cue", channel, 0x0C);
            Note($"{deck}.sync", channel, 0x58);
            Note($"{deck}.pfl", channel, 0x54);

            Note($"{deck}.loop.in", channel, 0x10);
            Note($"{deck}.loop.out", channel, 0x11);
            if (flx4)
            {
                Note($"{deck}.loop", channel, 0x4D);
                Note($"{deck}.reloop", channel, 0x50);
            }
            else
            {
                Note($"{deck}.loop.beats4", channel, 0x14);
                Note($"{deck}.loop.exit", channel, 0x4D);
                Note($"{deck}.reloop", channel, 0x50);
            }
            Note($"{deck}.loop.halve", channel, 0x51);
            Note($"{deck}.loop.double", channel, 0x53);
            Note($"{deck}.memory.set", channel, 0x3D);

            for (var pad = 0; pad < ControlCatalog.HOTCUES; pad++)
            {
                Note($"{deck}.hotcue{pad + 1}", pads, pad);
                Note($"{deck}.hotcue{pad + 1}.delete", shiftedPads, pad);
                Note($"{deck}.roll{pad + 1}", pads, 0x10 + pad);
                Note($"{deck}.jump{pad + 1}", pads, 0x20 + pad);
                Note($"{deck}.beatloop{pad + 1}", pads, 0x60 + pad);
                Note($"{deck}.keyshift{pad + 1}", pads, 0x70 + pad);
                // SAMPLER's pads are the hotcue pads, which play samples while the deck's in SAMPLER mode.
                Note($"{deck}.hotcue{pad + 1}", pads, 0x30 + pad);
            }
            Note($"{deck}.hotcues", channel, 0x1B);
            // The FLX4's PAD FX 1 button, and the 400's Shift + BEAT LOOP, which puts its pads in PAD FX 1.
            Note($"{deck}.roll", channel, 0x1E);
            Note($"{deck}.jump", channel, 0x20);
            Note($"{deck}.sampler", channel, 0x22);

            // Pioneer's tempo sliders are minus at the top, where they send 0.
            Control($"{deck}.tempo", channel, 0x00);
            Control($"{deck}.volume", channel, 0x13);
            Control($"{deck}.trim", channel, 0x04);
            Control($"{deck}.eq.high", channel, 0x07);
            Control($"{deck}.eq.mid", channel, 0x0B);
            Control($"{deck}.eq.low", channel, 0x0F);
            Control($"{deck}.filter", MIXER, 0x17 + i);

            // The wheel's edge nudges; its top, touched, scratches (vinyl mode, 0x22) or nudges (vinyl mode off,
            // 0x23); with Shift, the top searches through the song.
            Control($"{deck}.jog", channel, 0x21, fine: false);
            Control($"{deck}.jog", channel, 0x23, fine: false);
            Note($"{deck}.jog.touch", channel, 0x36);
            Note($"{deck}.jog.touch", channel, 0x67);
            Control($"{deck}.scratch", channel, 0x22, fine: false);
            Control($"{deck}.search", channel, 0x29, fine: false);

            Note($"{deck}.load", MIXER, 0x46 + i);

            // The Beat FX section is shared, so it sets both decks' effects; the FLX4's FX ON/OFF comes on the
            // channel of the deck its CH SELECT is set to.
            Note($"{deck}.fx.next", FX, 0x63);
            Note($"{deck}.fx.shorter", FX, 0x4A);
            Note($"{deck}.fx.longer", FX, 0x4B);
            Control($"{deck}.fx.level", FX, 0x02);
            if (flx4)
                Note($"{deck}.fx.on", FX + i, 0x47);
        }

        // The 400's FX ON/OFF doesn't say which channel it's for, but its CH SELECT switch says where it's set as it
        // moves (CH 1, CH 2 or MASTER), so it works the deck the switch was last set to, or the effect on the whole mix.
        if (!flx4)
        {
            Note("fx.select.A", FX, 0x10);
            Note("fx.select.B", FX, 0x11);
            Note("fx.select.master", FX, 0x14);
            Note("fx.on", FX, 0x47);
        }

        // MASTER LEVEL is left alone: it sets the controller's own output, so here it would turn the mix down twice.
        Control("crossfader", MIXER, 0x1F);
        Control("browse", MIXER, 0x40, fine: false, steps: StepEncoding.TwosComplement);
        Note("browse.press", MIXER, 0x41);
        return mapping;
    }
}
