using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

/// <summary>
/// The controls many controllers have that Cuepoint's built-in mappings don't use (kills, quantize, a needle strip,
/// pitch bend buttons, reverse, the headphone knobs and sampler pads of their own), from a pretend controller with
/// each on a note or control of its own.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class ControllerExtrasTests : IDisposable
{
    const string DEVICE = "Test Controller";

    static readonly (string Control, int Note)[] Buttons =
    [
        ("A.eq.low.kill", 0x01),
        ("A.eq.high.kill", 0x02),
        ("A.quantize", 0x03),
        ("A.bend.up", 0x04),
        ("A.bend.down", 0x05),
        ("A.reverse", 0x06),
        ("sampler1", 0x10),
        ("sampler9", 0x11),
        ("sampler.stop", 0x12),
    ];

    static readonly (string Control, int Number)[] Faders = [("A.position", 0x20), ("headphones.mix", 0x21), ("headphones.volume", 0x22)];

    sealed class Ports : IMidiPorts
    {
        public Action<MidiMessage>? Received;
        public readonly List<MidiMessage> Sent = [];

        public IReadOnlyList<string> InputNames() => [DEVICE];
        public IReadOnlyList<string> OutputNames() => [DEVICE];

        public IDisposable? OpenInput(int index, Action<MidiMessage> received)
        {
            Received = received;
            return new Output(Sent);
        }

        public IMidiOutput? OpenOutput(int index) => new Output(Sent);

        sealed class Output(List<MidiMessage> sent) : IMidiOutput
        {
            public void Send(MidiMessage message) => sent.Add(message);

            public void Dispose() { }
        }
    }

    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Ports _ports = new();
    readonly MidiDevices _devices;
    readonly MidiControls _controls;
    readonly Sampler _sampler;
    readonly Deck _a;
    double _now;

    public ControllerExtrasTests()
    {
        Silence(_folder["song.wav"], 30);
        Silence(_folder["horn.wav"], 5);
        _a = _engine.CreateDeck("A");
        _a.Load(_folder["song.wav"]);
        _a.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 30), -14, 0.5f, new MusicalKey(9, true));
        _sampler = new Sampler(_engine);

        var mapping = new ControllerMapping { Device = DEVICE };
        foreach (var (control, note) in Buttons)
            mapping.Bindings.Add(new MidiBinding { Control = control, Kind = MidiKind.Note, Number = note });
        foreach (var (control, number) in Faders)
            mapping.Bindings.Add(new MidiBinding { Control = control, Kind = MidiKind.ControlChange, Number = number });
        Assert.Null(mapping.Save(ControllerMapping.PathFor(_folder["controllers"], DEVICE)));

        _devices = new MidiDevices(_ports);
        _controls = new MidiControls(_devices, [_a], _engine, _folder["controllers"]) { Sampler = _sampler };
        Update();
    }

    public void Dispose()
    {
        _controls.Dispose();
        _devices.Dispose();
        _sampler.Dispose();
        _a.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Silence(string path, double seconds)
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

    void Update(double seconds = 0.01) => _controls.Update(_now += seconds, seconds);

    static int NoteOf(string control) => Buttons.Single(button => button.Control == control).Note;

    void Down(string control)
    {
        _ports.Received!(MidiMessage.Note(0, NoteOf(control), 0x7F));
        Update();
    }

    void Up(string control)
    {
        _ports.Received!(MidiMessage.Note(0, NoteOf(control), 0x00));
        Update();
    }

    void Press(string control)
    {
        Down(control);
        Up(control);
    }

    void Slide(string control, int value)
    {
        _ports.Received!(MidiMessage.Control(0, Faders.Single(fader => fader.Control == control).Number, value));
        Update();
    }

    bool? Light(string control) =>
        _ports.Sent.LastOrDefault(message => message.Kind == MidiKind.Note && message.Number == NoteOf(control)) is { Kind: not null } sent ? sent.Value > 0 : null;

    void WaitUntilReady(int slot)
    {
        for (var i = 0; i < 200 && !_sampler.IsReady(slot); i++)
        {
            Thread.Sleep(10);
            _sampler.Update();
        }
        Assert.True(_sampler.IsReady(slot));
    }

    [Fact]
    public void A_kill_takes_its_band_out_and_pressed_again_puts_it_back_where_it_was()
    {
        _a.Equalizer.Low = 0.3f;
        Press("A.eq.low.kill");
        Assert.Equal(-1, _a.Equalizer.Low);
        Assert.True(Light("A.eq.low.kill"));
        Assert.False(Light("A.eq.high.kill"));

        Press("A.eq.low.kill");
        Assert.Equal(0.3f, _a.Equalizer.Low);
        Assert.False(Light("A.eq.low.kill"));
    }

    [Fact]
    public void A_band_killed_some_other_way_comes_back_to_the_middle()
    {
        _a.Equalizer.High = -1;
        Press("A.eq.high.kill");
        Assert.Equal(0, _a.Equalizer.High);
    }

    [Fact]
    public void Quantize_switches_on_and_off_and_shows_which()
    {
        Assert.True(_a.Quantize);
        Press("A.quantize");
        Assert.False(_a.Quantize);
        Assert.False(Light("A.quantize"));
        Press("A.quantize");
        Assert.True(_a.Quantize);
        Assert.True(Light("A.quantize"));
    }

    [Fact]
    public void A_needle_strip_moves_through_the_song()
    {
        Slide("A.position", 127);
        Assert.True(_a.Position > _a.Duration * 0.95);
        Slide("A.position", 0);
        Assert.True(_a.Position < TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void The_headphone_knobs_set_the_mix_and_volume()
    {
        Slide("headphones.mix", 127);
        Assert.Equal(1, _engine.CueMix);
        Slide("headphones.volume", 0);
        Assert.Equal(0, _engine.HeadphoneVolume);
    }

    [Fact]
    public void A_pitch_bend_button_bends_while_held_then_lets_go_gently()
    {
        _a.Play();
        Down("A.bend.up");
        Update(0.5);
        Assert.Equal(MidiControls.BEND_BUTTON, _a.Bend, 6);

        Up("A.bend.up");
        Assert.InRange(_a.Bend, 0.0001, MidiControls.BEND_BUTTON);
        for (var i = 0; i < 50; i++)
            Update(0.05);
        Assert.Equal(0, _a.Bend);

        Down("A.bend.down");
        Assert.Equal(-MidiControls.BEND_BUTTON, _a.Bend, 6);
    }

    [Fact]
    public void Reverse_plays_backwards_while_held_and_forwards_once_let_go()
    {
        _a.Play();
        Down("A.reverse");
        Assert.Equal(PlatterMode.Reverse, _a.Platter);
        Assert.False(_a.IsCensoring);

        Up("A.reverse");
        Assert.NotEqual(PlatterMode.Reverse, _a.Platter);
        Assert.True(_a.IsPlaying);
    }

    [Fact]
    public void Reverse_does_nothing_paused()
    {
        Down("A.reverse");
        Assert.Equal(PlatterMode.Off, _a.Platter);
    }

    [Fact]
    public void Sampler_pads_play_their_samples_whatever_the_decks_pads_are_doing_and_stop_together()
    {
        _sampler.Load(0, _folder["horn.wav"], "Horn");
        _sampler.Load(8, _folder["horn.wav"], "Horn B");
        WaitUntilReady(0);
        WaitUntilReady(8);

        Press("sampler1");
        Press("sampler9");
        Assert.True(_sampler.IsPlaying(0));
        Assert.True(_sampler.IsPlaying(8));
        Assert.True(Light("sampler1"));

        Press("sampler.stop");
        Assert.False(_sampler.IsPlaying(0));
        Assert.False(_sampler.IsPlaying(8));
        Assert.False(Light("sampler9"));
    }
}
