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
            "DDJ-FLX4" => PioneerDdjRekordbox(device, flx4: true),
            "DDJ-400" => PioneerDdjRekordbox(device, flx4: false),
            _ => null,
        };

    /// <summary>
    /// Pioneer's DDJ layout, as on the DDJ-SB3: each deck's controls on its own channel (deck 1 on 1, deck 2 on 2),
    /// its pads on another (8 and 9), and the shared mixer and browser on 7. Shift + FX FADE puts the pads in ROLL
    /// mode, and HOT CUE back to hotcues; the pads send the same notes either way. Shift + HOT CUE puts them in BEAT
    /// JUMP mode, where they send notes of their own (0x40 up, Shift 8 higher still), back on the top row and on along
    /// the bottom.
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
    /// The DDJ-FLX4's and DDJ-400's layout, from Pioneer's lists of MIDI messages (DDJ-FLX4 ver 1.0, DDJ-400 ver
    /// 1.00): each deck's controls on its own channel (1 and 2), its pads on another (8 and 10, with Shift 9 and 11),
    /// and the mixer and browser on 7, much as on the DDJ-SB3. The pads send notes 16 apart in each mode the
    /// controller has, from 0x00 in HOT CUE mode: 0x10 PAD FX 1, 0x20 BEAT JUMP and 0x30 SAMPLER. HOT CUE pads are
    /// hotcues, cleared with Shift; PAD FX 1's are loop rolls; BEAT JUMP's are beat jumps; and SAMPLER's play the
    /// sampler's pads. Each mode's button puts the deck's pads in its mode here too. The other modes (KEYBOARD, PAD FX
    /// 2, BEAT LOOP and KEY SHIFT) have nothing to match here, so their pads do nothing.
    ///
    /// The loop buttons are IN and OUT, then on the FLX4 4 BEAT/EXIT, a four-beat loop or out of one, and on the 400
    /// RELOOP/EXIT, out of a loop or (with Shift) back into the last one; the 400's IN held is a four-beat loop. CUE/LOOP
    /// CALL halves and doubles the loop, and with Shift adds or removes a memory cue. The jog wheel's top (touched)
    /// scratches, as its vinyl mode is on unless an app turns it off; with Shift it searches. The FLX4's CFX knobs and
    /// the 400's FILTER knobs are each channel's filter. Beat FX: the FLX4 says which channel the FX ON/OFF is for, so
    /// it's that deck's effect on and off; on the 400 it doesn't, so it's left out. FX SELECT, BEAT and LEVEL/DEPTH
    /// set both decks' effects, as the controller has one Beat FX section. Neither has KEY LOCK or SLIP buttons.
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

        // MASTER LEVEL is left alone: it sets the controller's own output, so here it would turn the mix down twice.
        Control("crossfader", MIXER, 0x1F);
        Control("browse", MIXER, 0x40, fine: false, steps: StepEncoding.TwosComplement);
        Note("browse.press", MIXER, 0x41);
        return mapping;
    }
}
