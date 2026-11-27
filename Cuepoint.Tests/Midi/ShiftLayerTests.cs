using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Midi;

namespace Cuepoint.Tests.Midi;

/// <summary>
/// A pretend controller that sends the same messages with Shift held as without, leaving Shift to the app: its Shift
/// on note 0x3F, and KEY LOCK's note 0x1A working slip with Shift held. Its browse buttons, 0x50 and 0x51, step
/// through the song list, and with Shift through the folders.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class ShiftLayerTests : IDisposable
{
    const string DEVICE = "Test Controller";
    const int SHIFT = 0x3F;
    const int KEYLOCK = 0x1A;
    const int ROLL = 0x30;
    const int PLAY = 0x0B;
    const int NEXT = 0x50;
    const int PREV = 0x51;

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
    readonly Deck _a;
    double _now;

    public ShiftLayerTests()
    {
        Silence(_folder["song.wav"]);
        _a = _engine.CreateDeck("A");
        _a.Load(_folder["song.wav"]);
        _a.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 30), -14, 0.5f, new MusicalKey(9, true));

        var mapping = new ControllerMapping { Device = DEVICE };
        mapping.Bindings.AddRange(
            [
                Note("shift", SHIFT),
                Note("A.keylock", KEYLOCK),
                Note("A.slip", KEYLOCK, shifted: true),
                Note("A.roll5", ROLL, shifted: true),
                Note("A.play", PLAY),
                Note("browse.next", NEXT),
                Note("browse.prev", PREV),
                Note("browse.folders.next", NEXT, shifted: true),
                Note("browse.folders.prev", PREV, shifted: true),
            ]
        );
        Assert.Null(mapping.Save(ControllerMapping.PathFor(_folder["controllers"], DEVICE)));

        _devices = new MidiDevices(_ports);
        _controls = new MidiControls(_devices, [_a], _engine, _folder["controllers"]);
        Update();
    }

    static MidiBinding Note(string control, int note, bool shifted = false) =>
        new() { Control = control, Kind = MidiKind.Note, Channel = 0, Number = note, Shifted = shifted };

    public void Dispose()
    {
        _controls.Dispose();
        _devices.Dispose();
        _a.Dispose();
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

    void Update() => _controls.Update(_now += 0.01, 0.01);

    void Down(int note)
    {
        _ports.Received!(MidiMessage.Note(0, note, 0x7F));
        Update();
    }

    void Up(int note)
    {
        _ports.Received!(MidiMessage.Note(0, note, 0x00));
        Update();
    }

    void Press(int note)
    {
        Down(note);
        Up(note);
    }

    // The last light sent for a note: on, off, or null if none was.
    bool? Light(int note) => _ports.Sent.LastOrDefault(message => message.Kind == MidiKind.Note && message.Number == note) is { Kind: not null } sent ? sent.Value > 0 : null;

    [Fact]
    public void Browse_buttons_step_through_the_songs_and_with_Shift_the_folders()
    {
        var songs = new List<int>();
        var folders = new List<int>();
        _controls.Browse += songs.Add;
        _controls.BrowseFolders += folders.Add;

        Press(NEXT);
        Press(PREV);
        Down(SHIFT);
        Press(PREV);
        Up(SHIFT);

        Assert.Equal([1, -1], songs);
        Assert.Equal([-1], folders);
    }

    [Fact]
    public void Without_Shift_a_button_works_its_unshifted_binding()
    {
        Press(KEYLOCK);
        Assert.True(_a.KeyLocked);
        Assert.False(_a.Slip);
    }

    [Fact]
    public void With_Shift_held_the_same_button_works_its_shifted_binding_instead()
    {
        Down(SHIFT);
        Press(KEYLOCK);
        Up(SHIFT);
        Assert.True(_a.Slip);
        Assert.False(_a.KeyLocked);

        // Let go, it's back to the unshifted binding.
        Press(KEYLOCK);
        Assert.True(_a.KeyLocked);
        Assert.True(_a.Slip);
    }

    [Fact]
    public void A_button_with_nothing_bound_with_Shift_works_as_without()
    {
        Down(SHIFT);
        Press(PLAY);
        Assert.True(_a.IsPlaying);
    }

    [Fact]
    public void A_shifted_only_button_does_nothing_without_Shift()
    {
        _a.Play();
        Press(ROLL);
        Assert.Null(_a.RollBeats);
    }

    [Fact]
    public void A_held_button_is_let_go_of_in_the_layer_it_was_pressed_in_even_after_Shift_is()
    {
        _a.Play();
        Down(SHIFT);
        Down(ROLL);
        Assert.Equal(1, _a.RollBeats);

        Up(SHIFT);
        Up(ROLL);
        Assert.Null(_a.RollBeats);
    }

    [Fact]
    public void A_control_learned_with_Shift_held_works_only_with_Shift()
    {
        _controls.Learning = "A.censor";
        // Shift works while learning, rather than being what's learned.
        Down(SHIFT);
        Press(PLAY);
        Up(SHIFT);

        var mapping = _controls.MappingFor(_devices.Devices.Single())!;
        var learned = mapping.Bindings.Single(binding => binding.Control == "A.censor");
        Assert.True(learned.Shifted);
        Assert.Equal(PLAY, learned.Number);
        // Play keeps its own binding, as the two are in different layers.
        Assert.Contains(mapping.Bindings, binding => binding.Control == "A.play" && !binding.Shifted && binding.Number == PLAY);
        Assert.Null(_controls.Learning);
    }

    [Fact]
    public void Shift_itself_can_be_learned()
    {
        _controls.Learning = "shift";
        Press(0x40);
        var shifts = _controls.MappingFor(_devices.Devices.Single())!.Bindings.Where(binding => binding.Control == "shift").ToList();
        Assert.Equal(0x40, Assert.Single(shifts).Number);
        Assert.False(shifts[0].Shifted);
    }

    [Fact]
    public void A_button_shows_the_light_of_the_layer_Shift_has_it_in()
    {
        _a.KeyLocked = true;
        Update();
        Assert.True(Light(KEYLOCK));

        // Slip's off, so with Shift held the button goes dark, and back on when Shift's let go.
        Down(SHIFT);
        Assert.False(Light(KEYLOCK));
        Up(SHIFT);
        Assert.True(Light(KEYLOCK));
    }

    [Fact]
    public void Learning_a_message_with_Shift_leaves_the_same_message_without_it_alone()
    {
        var mapping = new ControllerMapping { Device = DEVICE };
        mapping.Bind(Note("A.keylock", KEYLOCK));
        mapping.Bind(Note("A.slip", KEYLOCK, shifted: true));
        Assert.Equal(2, mapping.Bindings.Count);

        // The same message in the same layer still takes the binding over.
        mapping.Bind(Note("A.vocals", KEYLOCK, shifted: true));
        Assert.Equal(["A.keylock", "A.vocals"], mapping.Bindings.Select(binding => binding.Control).Order());
    }

    [Fact]
    public void A_shifted_binding_learned_over_a_preset_leaves_the_presets_unshifted_one()
    {
        var preset = new ControllerMapping { Device = DEVICE, Bindings = [Note("A.keylock", KEYLOCK)] };
        var saved = new ControllerMapping { Device = DEVICE };
        saved.Bind(Note("A.slip", KEYLOCK, shifted: true));

        var mapping = ControllerMapping.Over(preset, saved);
        Assert.Equal(2, mapping.Bindings.Count);
        Assert.Contains(mapping.Bindings, binding => binding.Control == "A.keylock" && !binding.Shifted);
    }

    [Fact]
    public void Shifted_is_saved_and_read_back_and_left_out_when_it_isnt()
    {
        var path = _folder["shifted.json"];
        var mapping = new ControllerMapping { Device = DEVICE, Bindings = [Note("A.slip", KEYLOCK, shifted: true), Note("A.play", PLAY)] };
        Assert.Null(mapping.Save(path));
        Assert.Single(File.ReadAllLines(path), line => line.Contains("shifted"));

        var read = ControllerMapping.Load(path)!;
        Assert.True(read.Bindings.Single(binding => binding.Control == "A.slip").Shifted);
        Assert.False(read.Bindings.Single(binding => binding.Control == "A.play").Shifted);
        Assert.Equal("Shift + Note 26, ch 1", read.Bindings[0].ToString());
    }
}
