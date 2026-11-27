namespace Cuepoint.Midi;

/// <summary>
/// MIDI as a stream of bytes, as a Mac's CoreMIDI passes it, rather than whole messages as Windows' winmm does: read
/// into channel messages, with running status (a message's status byte left out when it's the same as the one before),
/// and written out again. System exclusive and the other system messages, the clock among them, are skipped on the way
/// in, wherever they come.
/// </summary>
sealed class MidiBytes
{
    byte _status;
    byte? _first;
    bool _inSysEx;

    /// <summary>
    /// How many data bytes follow <paramref name="status"/>: two for notes and controls, one for a program change or
    /// channel pressure, and none for the clock and the other real-time messages.
    /// </summary>
    public static int DataLength(byte status) =>
        status switch
        {
            < 0x80 => 0,
            < 0xC0 => 2,
            < 0xE0 => 1,
            < 0xF0 => 2,
            0xF1 or 0xF3 => 1,
            0xF2 => 2,
            _ => 0,
        };

    /// <summary>
    /// The bytes of <paramref name="message"/>, as many as its status calls for.
    /// </summary>
    public static int Write(MidiMessage message, Span<byte> bytes)
    {
        bytes[0] = message.Status;
        var length = DataLength(message.Status);
        if (length > 0)
            bytes[1] = message.Data1;
        if (length > 1)
            bytes[2] = message.Data2;
        return 1 + length;
    }

    /// <summary>
    /// Reads <paramref name="bytes"/>, handing each whole channel message to <paramref name="received"/>; a message cut
    /// off at the end carries on into the next call.
    /// </summary>
    public void Read(ReadOnlySpan<byte> bytes, Action<MidiMessage> received)
    {
        foreach (var value in bytes)
        {
            if (value >= 0xF8)
                continue; // Real-time (the clock, start, stop), which can come even mid-message.
            if (value == 0xF0)
            {
                _inSysEx = true;
                continue;
            }
            if (value >= 0x80)
            {
                _inSysEx = false;
                // A system message cancels running status; its data bytes are skipped as status-less data below.
                _status = value < 0xF0 ? value : (byte)0;
                _first = null;
                continue;
            }
            if (_inSysEx || _status == 0)
                continue;

            var length = DataLength(_status);
            if (length == 1)
                received(new MidiMessage(_status, value, 0));
            else if (_first is not { } first)
                _first = value;
            else
            {
                received(new MidiMessage(_status, first, value));
                _first = null;
            }
        }
    }
}
