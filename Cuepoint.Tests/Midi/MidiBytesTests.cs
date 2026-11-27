using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

public class MidiBytesTests
{
    static List<MidiMessage> Read(MidiBytes reader, params byte[] bytes)
    {
        var messages = new List<MidiMessage>();
        reader.Read(bytes, messages.Add);
        return messages;
    }

    [Fact]
    public void Notes_and_controls_come_out_whole()
    {
        var messages = Read(new MidiBytes(), 0x90, 60, 127, 0xB1, 7, 64);

        Assert.Equal([MidiMessage.Note(0, 60, 127), MidiMessage.Control(1, 7, 64)], messages);
    }

    [Fact]
    public void Running_status_repeats_the_last_status_for_each_pair_of_data_bytes()
    {
        var messages = Read(new MidiBytes(), 0xB0, 1, 10, 1, 11, 2, 12);

        Assert.Equal([MidiMessage.Control(0, 1, 10), MidiMessage.Control(0, 1, 11), MidiMessage.Control(0, 2, 12)], messages);
    }

    [Fact]
    public void The_clock_mid_message_and_system_exclusive_are_skipped()
    {
        var messages = Read(new MidiBytes(), 0x90, 0xF8, 60, 0xF8, 100, 0xF0, 0x7E, 0x01, 0x02, 0xF7, 0xB0, 3, 4);

        Assert.Equal([MidiMessage.Note(0, 60, 100), MidiMessage.Control(0, 3, 4)], messages);
    }

    [Fact]
    public void A_message_split_between_packets_is_put_together()
    {
        var reader = new MidiBytes();

        Assert.Empty(Read(reader, 0x90, 60));
        Assert.Equal([MidiMessage.Note(0, 60, 0)], Read(reader, 0));
    }

    [Fact]
    public void A_program_change_has_one_data_byte()
    {
        var messages = Read(new MidiBytes(), 0xC2, 5, 6);

        Assert.Equal([new MidiMessage(0xC2, 5, 0), new MidiMessage(0xC2, 6, 0)], messages);
    }

    [Fact]
    public void Written_out_a_message_is_as_long_as_its_status_says()
    {
        Span<byte> bytes = stackalloc byte[3];

        Assert.Equal(3, MidiBytes.Write(MidiMessage.Control(0, 7, 100), bytes));
        Assert.Equal([0xB0, 7, 100], bytes.ToArray());
        Assert.Equal(1, MidiBytes.Write(new MidiMessage(0xF8, 0, 0), bytes));
        Assert.Equal(0xF8, bytes[0]);
    }
}
