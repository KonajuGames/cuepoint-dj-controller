using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

/// <summary>
/// A pretend DDJ-400 sending its BEAT LOOP and KEY SHIFT pads and its Beat FX section to two real decks.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class RekordboxControllerPadTests : IDisposable
{
    sealed class Ports : IMidiPorts
    {
        public Action<MidiMessage>? Received;

        public IReadOnlyList<string> InputNames() => ["DDJ-400"];
        public IReadOnlyList<string> OutputNames() => [];

        public IDisposable? OpenInput(int index, Action<MidiMessage> received)
        {
            Received = received;
            return new Nothing();
        }

        public IMidiOutput? OpenOutput(int index) => null;

        sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Ports _ports = new();
    readonly MidiDevices _devices;
    readonly MidiControls _controls;
    readonly Deck _a;
    readonly Deck _b;

    public RekordboxControllerPadTests()
    {
        Silence(_folder["song.wav"]);
        _a = Loaded("A");
        _b = Loaded("B");
        _devices = new MidiDevices(_ports);
        _controls = new MidiControls(_devices, [_a, _b], _engine, _folder["controllers"]);
        _controls.Update(0, 0);
    }

    Deck Loaded(string name)
    {
        var deck = _engine.CreateDeck(name);
        deck.Load(_folder["song.wav"]);
        deck.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 30), -14, 0.5f, new MusicalKey(9, true));
        return deck;
    }

    public void Dispose()
    {
        _controls.Dispose();
        _devices.Dispose();
        _a.Dispose();
        _b.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Silence(string path, double seconds = 30)
    {
        const int rate = 8000;
        var samples = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }

    // A press and release, as a pad or button sends, read by the controls.
    void Press(int channel, int note)
    {
        _ports.Received!(MidiMessage.Note(channel, note, 0x7F));
        _ports.Received!(MidiMessage.Note(channel, note, 0x00));
        _controls.Update(1, 0);
    }

    [Fact]
    public void BEAT_LOOP_pads_start_loops_of_their_size_and_leave_them_pressed_again()
    {
        _a.Play();
        // Deck 1's pads are on channel 8 (7 on the wire); BEAT LOOP's from 0x60, a quarter of a beat to 32.
        Press(7, 0x60 + 5);
        Assert.True(_a.IsLooping);
        Assert.Equal(8, _a.LoopBeats);

        Press(7, 0x60 + 1);
        Assert.Equal(0.5, _a.LoopBeats);

        Press(7, 0x60 + 1);
        Assert.False(_a.IsLooping);
    }

    [Fact]
    public void KEY_SHIFT_pads_shift_the_key_up_on_top_down_below_and_back_pressed_again()
    {
        // Deck 2's pads are on channel 10 (9 on the wire); KEY SHIFT's from 0x70.
        Press(9, 0x70 + 2);
        Assert.Equal(3, _b.KeyShift);
        Press(9, 0x70 + 4);
        Assert.Equal(-1, _b.KeyShift);
        Press(9, 0x70 + 4);
        Assert.Equal(0, _b.KeyShift);
    }

    [Fact]
    public void FX_ON_works_whichever_decks_CH_SELECT_was_last_set_to()
    {
        // Before the switch has said anything: both.
        Press(4, 0x47);
        Assert.True(_a.Effects.On && _b.Effects.On);
        Press(4, 0x47);
        Assert.False(_a.Effects.On || _b.Effects.On);

        // CH 2.
        Press(4, 0x11);
        Press(4, 0x47);
        Assert.False(_a.Effects.On);
        Assert.True(_b.Effects.On);

        // CH 1, and MASTER: the effect on the whole mix, leaving the decks' alone.
        Press(4, 0x10);
        Press(4, 0x47);
        Assert.True(_a.Effects.On);
        Press(4, 0x14);
        Press(4, 0x47);
        Assert.True(_engine.MasterEffects.On);
        Assert.True(_a.Effects.On && _b.Effects.On);
        Press(4, 0x47);
        Assert.False(_engine.MasterEffects.On);
    }
}
