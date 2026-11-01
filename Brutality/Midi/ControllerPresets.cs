namespace Brutality.Midi;

/// <summary>
/// Built-in mappings for controllers known by name, used until a control is learned differently.
/// </summary>
static class ControllerPresets
{
    public static ControllerMapping? For(string device) =>
        device switch
        {
            "DDJ-SB3" => PioneerDdj(device),
            _ => null,
        };

    /// <summary>
    /// Pioneer's DDJ layout, as on the DDJ-SB3: each deck's controls on its own channel (deck 1 on 1, deck 2 on 2),
    /// its pads on another (8 and 9), and the shared mixer and browser on 7. Faders and knobs send fourteen bits,
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
            Note($"{deck}.loop", channel, 0x14);
            Note($"{deck}.loop.halve", channel, 0x12);
            Note($"{deck}.loop.double", channel, 0x13);
            for (var pad = 0; pad < ControlCatalog.HOTCUES; pad++)
            {
                Note($"{deck}.hotcue{pad + 1}", pads, pad);
                Note($"{deck}.hotcue{pad + 1}.delete", pads, pad + 8);
            }

            // Pioneer's tempo sliders are minus at the top, where they send 0.
            Control($"{deck}.tempo", channel, 0x00);
            Control($"{deck}.volume", channel, 0x13);
            Control($"{deck}.eq.high", channel, 0x07);
            Control($"{deck}.eq.mid", channel, 0x0B);
            Control($"{deck}.eq.low", channel, 0x0F);
            Control($"{deck}.filter", MIXER, 0x17 + i);

            // The wheel's top (touched) and its outer edge both nudge or move through the song.
            Control($"{deck}.jog", channel, 0x21, fine: false);
            mapping.Bindings.Add(new MidiBinding { Control = $"{deck}.jog", Kind = MidiKind.ControlChange, Channel = channel, Number = 0x23 });
            Control($"{deck}.search", channel, 0x1F, fine: false);
            Control($"{deck}.search", channel, 0x26, fine: false);

            Note($"{deck}.load", MIXER, 0x46 + i);
        }

        Control("crossfader", MIXER, 0x1F);
        Control("browse", MIXER, 0x40, fine: false, steps: StepEncoding.TwosComplement);
        Note("browse.press", MIXER, 0x41);
        return mapping;
    }
}
