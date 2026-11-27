using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

/// <summary>
/// The controls many controllers have that Cuepoint's built-in mappings don't use (kills, quantize, a needle strip,
/// pitch bend buttons, reverse, the headphone knobs, sampler pads of their own, set cue, stutter, search buttons and
/// beat here), from a pretend controller with each on a note or control of its own.
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
        ("sampler2.load", 0x13),
        ("A.cue.set", 0x07),
        ("A.stutter", 0x08),
        ("A.search.back", 0x09),
        ("A.search.forward", 0x0A),
        ("A.grid.beat", 0x0B),
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

    [Fact]
    public void Set_cue_puts_the_cue_point_where_the_deck_is_on_the_beat_without_moving_it()
    {
        var changed = 0;
        _a.CuePointChanged += _ => changed++;
        // 120 BPM, so beats are half a second apart: 5.2 seconds is nearest the beat at 5.
        _a.Seek(TimeSpan.FromSeconds(5.2));
        Press("A.cue.set");
        Assert.Equal(5, _a.CuePoint.TotalSeconds, 2);
        Assert.Equal(5.2, _a.Position.TotalSeconds, 2);
        Assert.False(_a.IsPlaying);
        Assert.Equal(1, changed);

        _a.Quantize = false;
        Press("A.cue.set");
        Assert.Equal(5.2, _a.CuePoint.TotalSeconds, 2);
    }

    [Fact]
    public void Stutter_goes_back_to_the_cue_point_and_plays_whether_playing_or_not()
    {
        _a.Seek(TimeSpan.FromSeconds(3));
        Press("A.cue.set");
        _a.Seek(TimeSpan.FromSeconds(10));

        Press("A.stutter");
        Assert.True(_a.IsPlaying);
        Assert.Equal(3, _a.Position.TotalSeconds, 1);

        _a.Seek(TimeSpan.FromSeconds(12));
        Press("A.stutter");
        Assert.True(_a.IsPlaying);
        Assert.Equal(3, _a.Position.TotalSeconds, 1);
    }

    [Fact]
    public void A_search_button_moves_through_the_song_while_its_held()
    {
        _a.Seek(TimeSpan.FromSeconds(10));
        Down("A.search.forward");
        Update(0.5);
        // Half a second held, and the frame it went down in, at eight seconds of song a second.
        Assert.InRange(_a.Position.TotalSeconds, 10 + 0.5 * MidiControls.SEARCH_BUTTON_SPEED, 10 + 0.51 * MidiControls.SEARCH_BUTTON_SPEED + 0.01);
        Up("A.search.forward");
        var after = _a.Position;
        Update(0.5);
        Assert.Equal(after, _a.Position);

        Down("A.search.back");
        Update(0.5);
        Assert.True(_a.Position < after - TimeSpan.FromSeconds(3));
        Up("A.search.back");
    }

    [Fact]
    public void Beat_here_and_a_sampler_pads_load_button_go_to_the_app()
    {
        var beats = new List<Deck>();
        var loads = new List<int>();
        _controls.SetBeatPressed += beats.Add;
        _controls.SamplerLoadRequested += loads.Add;

        Press("A.grid.beat");
        Press("sampler2.load");

        Assert.Equal([_a], beats);
        Assert.Equal([1], loads);
    }
}
