namespace Brutality.Midi;

enum MidiKind
{
    Note,
    ControlChange,
}

/// <summary>
/// A three-byte MIDI channel message: its status byte (kind and channel) and two data bytes. Only notes and control
/// changes mean anything here; a note-off, or a note-on at velocity 0 as most controllers send for a release, both
/// read as <see cref="MidiKind.Note"/> with <see cref="IsPressed"/> false.
/// </summary>
readonly record struct MidiMessage(byte Status, byte Data1, byte Data2)
{
    const int NOTE_OFF = 0x80;
    const int NOTE_ON = 0x90;
    const int CONTROL_CHANGE = 0xB0;

    public static MidiMessage Note(int channel, int note, int velocity) =>
        new((byte)(NOTE_ON | (channel & 0x0F)), (byte)(note & 0x7F), (byte)(velocity & 0x7F));

    public static MidiMessage Control(int channel, int number, int value) =>
        new((byte)(CONTROL_CHANGE | (channel & 0x0F)), (byte)(number & 0x7F), (byte)(value & 0x7F));

    /// <summary>
    /// The message packed as WinMM passes it: status in the low byte, then the two data bytes.
    /// </summary>
    public static MidiMessage FromPacked(uint packed) => new((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16));

    public uint Packed => Status | (uint)Data1 << 8 | (uint)Data2 << 16;

    public MidiKind? Kind =>
        (Status & 0xF0) switch
        {
            NOTE_ON or NOTE_OFF => MidiKind.Note,
            CONTROL_CHANGE => MidiKind.ControlChange,
            _ => null,
        };

    /// <summary>
    /// The channel, from 0 to 15 (shown to people as 1 to 16).
    /// </summary>
    public int Channel => Status & 0x0F;

    /// <summary>
    /// The note or controller number.
    /// </summary>
    public int Number => Data1;

    public int Value => Data2;

    /// <summary>
    /// For a note, whether it went down rather than up.
    /// </summary>
    public bool IsPressed => (Status & 0xF0) == NOTE_ON && Data2 > 0;

    public override string ToString() =>
        Kind switch
        {
            MidiKind.Note => $"note {Number} on channel {Channel + 1} {(IsPressed ? "down" : "up")}",
            MidiKind.ControlChange => $"CC {Number} on channel {Channel + 1} = {Value}",
            _ => $"{Status:X2} {Data1:X2} {Data2:X2}",
        };
}
