using Brutality.Music;
using Brutality.Audio;

namespace Brutality.Midi;

/// <summary>
/// Works the decks and mixer from MIDI controllers. Each connected device uses its own saved mapping, or the built-in
/// one for its model (<see cref="ControllerPresets"/>), and lights its buttons to match the app: playing, cued,
/// synced, looping, hotcues set. Call <see cref="Update"/> every frame on the UI thread, where everything it does
/// happens.
///
/// To bind a control, set <see cref="Learning"/> to its ID: the next button pressed or knob moved on any controller
/// is bound to it, and that controller's mapping is saved.
/// </summary>
sealed class MidiControls : IDisposable
{
    /// <summary>
    /// How far a jog wheel step moves a paused deck, in seconds, and Shift's fast search.
    /// </summary>
    public const double SEEK_PER_STEP = 0.0025;
    public const double SEARCH_PER_STEP = 0.25;

    /// <summary>
    /// How much a jog wheel step bends a playing deck's speed, and how quickly the bend fades once the wheel stops,
    /// as a time constant in seconds.
    /// </summary>
    public const double BEND_PER_STEP = 0.004;
    const double BEND_FADE_SECONDS = 0.12;

    /// <summary>
    /// How long the short and long backspins take to spin down, in seconds.
    /// </summary>
    public const double SHORT_BACKSPIN_SECONDS = 0.8;
    public const double LONG_BACKSPIN_SECONDS = 2;

    /// <summary>
    /// How near the middle a centred knob counts as the middle, as a fraction of its half-travel.
    /// </summary>
    const float CENTRE_DETENT = 0.01f;

    readonly MidiDevices _devices;
    readonly IReadOnlyList<Deck> _decks;
    readonly AudioEngine _engine;
    readonly string _folder;
    readonly Dictionary<string, ControlInfo> _controls;
    readonly Dictionary<MidiDevice, ControllerMapping> _mappings = [];

    // Fourteen-bit controls' coarse halves, waiting for their fine halves, and whether a fine half has ever come
    // (a control learned as fourteen-bit that only sends seven still works).
    readonly Dictionary<(MidiDevice, MidiBinding), (int Coarse, bool FineSeen)> _coarse = [];

    // The lights as last sent, to send only changes.
    readonly Dictionary<(MidiDevice, MidiBinding), bool> _lights = [];

    // Messages that aren't bound, said once each.
    readonly HashSet<(MidiDevice, MidiKind?, int, int)> _unmapped = [];

    readonly Dictionary<Deck, double> _bends = [];

    public MidiControls(MidiDevices devices, IReadOnlyList<Deck> decks, AudioEngine engine, string mappingFolder)
    {
        _devices = devices;
        _decks = decks;
        _engine = engine;
        _folder = mappingFolder;
        _controls = ControlCatalog.For(decks.Select(deck => deck.Name)).ToDictionary(control => control.Id);
        devices.Connected += OnConnected;
        devices.Disconnected += OnDisconnected;
    }

    public IReadOnlyCollection<ControlInfo> Controls => _controls.Values;

    /// <summary>
    /// What the controllers are doing, worth a line on the status bar.
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// A deck's load button, to load the selected song there.
    /// </summary>
    public event Action<Deck>? LoadRequested;

    /// <summary>
    /// The browse knob turned, by this many steps (down the list for positive), and with Shift for folders.
    /// </summary>
    public event Action<int>? Browse;
    public event Action<int>? BrowseFolders;
    public event Action? BrowsePressed;

    /// <summary>
    /// A deck's pad was pressed (with Shift, as the controller sends it, if <c>shifted</c>): return true to take it,
    /// as when the pads are fixing the beat grid, or false to leave it to work the hotcue.
    /// </summary>
    public Func<Deck, int, bool, bool>? PadPressed { get; set; }

    /// <summary>
    /// A deck's pad was let go, for a pad that's held (a loop roll).
    /// </summary>
    public Action<Deck, int>? PadReleased { get; set; }

    /// <summary>
    /// A deck's roll mode button was pressed, to turn ROLL mode on or off.
    /// </summary>
    public event Action<Deck>? RollModePressed;

    /// <summary>
    /// A deck's hotcue mode button was pressed, to put the pads back to hotcues.
    /// </summary>
    public event Action<Deck>? HotcueModePressed;

    public Func<Deck, bool>? IsRollMode { get; set; }

    /// <summary>
    /// A deck's beat jump mode button was pressed, to put the pads in JUMP mode; and whether that deck's in it, for
    /// its light.
    /// </summary>
    public event Action<Deck>? JumpModePressed;

    public Func<Deck, bool>? IsJumpMode { get; set; }

    /// <summary>
    /// A deck's grid mode button was pressed, and whether that deck's grid mode is on, for its light.
    /// </summary>
    public event Action<Deck>? GridModePressed;

    public Func<Deck, bool>? IsGridMode { get; set; }

    /// <summary>
    /// The control the next message will be bound to, if learning; null otherwise.
    /// </summary>
    public string? Learning { get; set; }

    /// <summary>
    /// A control was bound by learning, on a device.
    /// </summary>
    public event Action<MidiDevice, MidiBinding>? Learned;

    public ControllerMapping? MappingFor(MidiDevice device) => _mappings.GetValueOrDefault(device);

    void OnConnected(MidiDevice device)
    {
        // What was changed by hand, over the built-in mapping if there is one, so controls added to it since still come.
        var saved = ControllerMapping.Load(ControllerMapping.PathFor(_folder, device.Name));
        var preset = ControllerPresets.For(device.Name);
        var mapping = saved is not null && preset is not null ? ControllerMapping.Over(preset, saved) : saved ?? preset;
        _mappings[device] = mapping ?? new ControllerMapping { Device = device.Name };
        Log?.Invoke(
            mapping is null ? $"Controller {device.Name} connected, with nothing mapped yet: bind its controls in the options"
            : $"Controller {device.Name} connected"
        );
    }

    void OnDisconnected(MidiDevice device)
    {
        _mappings.Remove(device);
        foreach (var key in _lights.Keys.Where(key => key.Item1 == device).ToList())
            _lights.Remove(key);
        Log?.Invoke($"Controller {device.Name} disconnected");
    }

    /// <summary>
    /// Puts a device back on its built-in mapping, forgetting what was learned, and deletes its saved one.
    /// </summary>
    public void ResetMapping(MidiDevice device)
    {
        try
        {
            File.Delete(ControllerMapping.PathFor(_folder, device.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"Couldn't delete the mapping for {device.Name}: {e.Message}");
        }
        _mappings[device] = ControllerPresets.For(device.Name) ?? new ControllerMapping { Device = device.Name };
        ForgetLights(device);
    }

    public void Unbind(MidiDevice device, string control)
    {
        if (!_mappings.TryGetValue(device, out var mapping))
            return;
        mapping.Unbind(control);
        SaveMapping(device, mapping);
        ForgetLights(device);
    }

    void ForgetLights(MidiDevice device)
    {
        foreach (var key in _lights.Keys.Where(key => key.Item1 == device).ToList())
        {
            // Lights a mapping no longer has go out.
            if (key.Item1.CanSend && _lights[key])
                device.Send(LightMessage(key.Item2, false));
            _lights.Remove(key);
        }
    }

    void SaveMapping(MidiDevice device, ControllerMapping mapping)
    {
        if (mapping.Save(ControllerMapping.PathFor(_folder, device.Name)) is { } error)
            Log?.Invoke($"Couldn't save the mapping for {device.Name}: {error}");
    }

    /// <summary>
    /// Handles what came in since the last frame, lets go of jog bends, and updates lights.
    /// </summary>
    public void Update(double now, double seconds)
    {
        _devices.Update(now);
        while (_devices.TryRead(out var device, out var message))
            Receive(device, message);

        var fade = Math.Exp(-Math.Max(0, seconds) / BEND_FADE_SECONDS);
        foreach (var deck in _bends.Keys.ToList())
        {
            var bend = _bends[deck] * fade;
            if (Math.Abs(bend) < 1e-4)
                bend = 0;
            _bends[deck] = bend;
            deck.Bend = bend;
            if (bend == 0)
                _bends.Remove(deck);
        }

        _now = now;
        UpdateLights();
    }

    public void Receive(MidiDevice device, MidiMessage message)
    {
        if (message.Kind is null)
            return;
        if (!_mappings.TryGetValue(device, out var mapping))
            return;

        if (Learning is { } learning && _controls.TryGetValue(learning, out var learnedControl))
        {
            // A release isn't what to learn from: the press before it was.
            if (message.Kind == MidiKind.Note && !message.IsPressed)
                return;
            Learn(device, mapping, learnedControl, message);
            return;
        }

        var bound = false;
        foreach (var binding in mapping.Bindings)
        {
            if (!binding.Matches(message) || !_controls.TryGetValue(binding.Control, out var control))
                continue;
            bound = true;
            Act(device, binding, control, message);
        }

        if (!bound && _unmapped.Add((device, message.Kind, message.Channel, message.Number)) && (message.Kind != MidiKind.Note || message.IsPressed))
            Log?.Invoke($"{device.Name}: {message} isn't mapped to anything");
    }

    void Learn(MidiDevice device, ControllerMapping mapping, ControlInfo control, MidiMessage message)
    {
        var binding = new MidiBinding
        {
            Control = control.Id,
            Kind = message.Kind!.Value,
            Channel = message.Channel,
            Number = message.Number,
            // Faders and knobs below 32 are assumed to send fine halves 32 above, as the MIDI standard has it; ones
            // that don't still work at seven bits.
            Fine = message.Kind == MidiKind.ControlChange && control.Kind is ControlKind.Fader or ControlKind.Knob && message.Number < 32 ? message.Number + 32 : null,
            // A first step of 1 to 15 forward or back reads as two's complement; anything around 64 as offset.
            Steps = message.Value is (>= 1 and <= 15) or (>= 113 and <= 127) ? StepEncoding.TwosComplement : StepEncoding.Offset64,
        };
        mapping.Bind(binding);
        SaveMapping(device, mapping);
        ForgetLights(device);
        Learning = null;
        Learned?.Invoke(device, binding);
        Log?.Invoke($"{control.Name} is now {binding} on {device.Name}");
    }

    void Act(MidiDevice device, MidiBinding binding, ControlInfo control, MidiMessage message)
    {
        var (deck, action) = Split(control.Id);
        switch (control.Kind)
        {
            case ControlKind.Button:
                var pressed = message.Kind == MidiKind.Note ? message.IsPressed : message.Value > 0;
                Press(deck, action, pressed);
                break;

            case ControlKind.Fader or ControlKind.Knob:
                if (Position(device, binding, message) is { } position)
                    Move(deck, action, binding.Invert ? 1 - position : position);
                break;

            case ControlKind.Encoder:
                var steps = binding.Steps == StepEncoding.TwosComplement
                    ? message.Value < 64 ? message.Value : message.Value - 128
                    : message.Value - 64;
                Turn(deck, action, binding.Invert ? -steps : steps);
                break;
        }
    }

    (Deck? Deck, string Action) Split(string id)
    {
        var dot = id.IndexOf('.');
        if (dot > 0 && _decks.FirstOrDefault(deck => deck.Name == id[..dot]) is { } deck)
            return (deck, id[(dot + 1)..]);
        return (null, id);
    }

    /// <summary>
    /// Where a fader or knob is, from 0 to 1, once this message says; null while waiting for the other half.
    /// </summary>
    float? Position(MidiDevice device, MidiBinding binding, MidiMessage message)
    {
        var key = (device, binding);
        _coarse.TryGetValue(key, out var state);
        if (message.Number == binding.Number)
        {
            _coarse[key] = (message.Value, state.FineSeen);
            return state.FineSeen ? null : message.Value / 127f;
        }
        _coarse[key] = (state.Coarse, true);
        return ((state.Coarse << 7) | message.Value) / 16383f;
    }

    void Press(Deck? deck, string action, bool pressed)
    {
        if (deck is null)
        {
            if (action == "browse.press" && pressed)
                BrowsePressed?.Invoke();
            return;
        }

        if (action == "cue")
        {
            if (pressed)
                deck.CueDown();
            else
                deck.CueUp();
            return;
        }

        // Touching the jog wheel holds the record, for scratching; letting go sets it free.
        if (action == "jog.touch")
        {
            deck.JogTouch(pressed);
            return;
        }

        // A beat jump pad: the controller sends its own notes for them in its beat jump mode.
        if (Jump(action) is { } jump)
        {
            if (pressed)
                deck.BeatJump(JumpPads.Sizes[jump]);
            return;
        }

        // A loop roll lasts as long as its pad's held.
        if (Roll(action) is { } roll)
        {
            if (pressed)
                deck.RollDown(RollPads.Sizes[roll]);
            else
                deck.RollUp(RollPads.Sizes[roll]);
            return;
        }
        if (!pressed)
        {
            if (Hotcue(action) is { } released && !action.EndsWith(".delete"))
            {
                PadReleased?.Invoke(deck, released);
                // Under slip, a hotcue held lasts until its pad's let go.
                deck.HotcueUp(released);
            }
            return;
        }

        switch (action)
        {
            case "play":
                deck.TogglePlay();
                break;
            case "sync":
                deck.Sync();
                break;
            case "load":
                LoadRequested?.Invoke(deck);
                break;
            case "pfl":
                deck.Headphones = !deck.Headphones;
                break;
            case "keylock":
                deck.KeyLocked = !deck.KeyLocked;
                break;
            case "slip":
                deck.Slip = !deck.Slip;
                break;
            case "key.down":
                deck.KeyShift--;
                break;
            case "key.up":
                deck.KeyShift++;
                break;
            case "key.reset":
                deck.KeyShift = 0;
                break;
            case "key.match":
                deck.KeyMatch();
                break;
            case "fx.on":
                deck.Effects.On = !deck.Effects.On;
                break;
            case "fx.next":
                deck.Effects.Kind = (DeckEffect)(((int)deck.Effects.Kind + 1) % Enum.GetValues<DeckEffect>().Length);
                break;
            case "fx.shorter":
                deck.Effects.StepBeats(-1);
                break;
            case "fx.longer":
                deck.Effects.StepBeats(1);
                break;
            case "loop":
                deck.ToggleLoop();
                break;
            case "loop.halve":
                deck.HalveLoop();
                break;
            case "loop.double":
                deck.DoubleLoop();
                break;
            case "loop.exit":
                deck.ExitLoop();
                break;
            case "loop.in":
                deck.LoopIn();
                break;
            case "loop.out":
                deck.LoopOut();
                break;
            case "reloop":
                deck.Reloop();
                break;
            case "loop.beats1" or "loop.beats2" or "loop.beats4" or "loop.beats8":
                // A loop of that size: started if there isn't one, or the one going on made that size.
                deck.LoopBeats = int.Parse(action["loop.beats".Length..]);
                if (!deck.IsLooping)
                    deck.ToggleLoop();
                break;
            case "backspin.short":
                deck.Backspin(SHORT_BACKSPIN_SECONDS);
                break;
            case "backspin.long":
                deck.Backspin(LONG_BACKSPIN_SECONDS);
                break;
            case "fxfade":
                // The controller plays the effect itself, by sending the filter, fader and loop controls it works.
                break;
            case "tempo.reset":
                deck.Tempo = 0;
                break;
            case "grid":
                GridModePressed?.Invoke(deck);
                break;
            case "roll":
                RollModePressed?.Invoke(deck);
                break;
            case "hotcues":
                HotcueModePressed?.Invoke(deck);
                break;
            case "jump":
                JumpModePressed?.Invoke(deck);
                break;
            default:
                if (Hotcue(action) is { } hotcue)
                {
                    if (PadPressed?.Invoke(deck, hotcue, action.EndsWith(".delete")) == true)
                        break;
                    if (action.EndsWith(".delete"))
                        deck.DeleteHotcue(hotcue);
                    else
                        deck.HotcueDown(hotcue);
                }
                break;
        }
    }

    /// <summary>
    /// The loop roll pad, from 0, an action like "roll3" is for.
    /// </summary>
    static int? Roll(string action) =>
        action.StartsWith("roll") && int.TryParse(action["roll".Length..], out var number) && number >= 1 && number <= RollPads.Sizes.Count ? number - 1 : null;

    /// <summary>
    /// The beat jump pad, from 0, an action like "jump3" is for.
    /// </summary>
    static int? Jump(string action) =>
        action.StartsWith("jump") && int.TryParse(action["jump".Length..], out var number) && number >= 1 && number <= JumpPads.Sizes.Count ? number - 1 : null;

    /// <summary>
    /// The hotcue, from 0, an action like "hotcue3" or "hotcue3.delete" is for.
    /// </summary>
    static int? Hotcue(string action)
    {
        if (!action.StartsWith("hotcue"))
            return null;
        var digits = new string(action["hotcue".Length..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var number) && number >= 1 && number <= ControlCatalog.HOTCUES ? number - 1 : null;
    }

    void Move(Deck? deck, string action, float position)
    {
        if (deck is null)
        {
            if (action == "crossfader")
                _engine.Crossfader = position;
            return;
        }

        var centred = position * 2 - 1;
        if (MathF.Abs(centred) < CENTRE_DETENT)
            centred = 0;

        switch (action)
        {
            case "tempo":
                deck.Tempo = centred * deck.TempoRange;
                break;
            case "volume":
                deck.Volume = position;
                break;
            case "trim":
                deck.Trim = centred;
                break;
            case "eq.high":
                deck.Equalizer.High = centred;
                break;
            case "eq.mid":
                deck.Equalizer.Mid = centred;
                break;
            case "eq.low":
                deck.Equalizer.Low = centred;
                break;
            case "filter":
                deck.Equalizer.Filter = centred;
                break;
            case "fx.level":
                deck.Effects.Level = position;
                break;
        }
    }

    void Turn(Deck? deck, string action, int steps)
    {
        if (steps == 0)
            return;
        if (deck is null)
        {
            if (action == "browse")
                Browse?.Invoke(steps);
            else if (action == "browse.folders")
                BrowseFolders?.Invoke(steps);
            return;
        }

        switch (action)
        {
            // Playing, the wheel nudges the speed, as pushing a record does; stopped, it moves through the song.
            case "jog" when deck.IsPlaying:
                var bend = Math.Clamp(_bends.GetValueOrDefault(deck) + steps * BEND_PER_STEP, -Deck.MAX_BEND, Deck.MAX_BEND);
                _bends[deck] = bend;
                deck.Bend = bend;
                break;
            case "jog":
                deck.Seek(deck.Position + TimeSpan.FromSeconds(steps * SEEK_PER_STEP));
                break;
            case "search":
                deck.Seek(deck.Position + TimeSpan.FromSeconds(steps * SEARCH_PER_STEP));
                break;
            case "scratch":
                deck.Scratch(steps);
                break;
        }
    }

    /// <summary>
    /// How long a flashing light takes to go on and off once, in seconds.
    /// </summary>
    public const double FLASH_SECONDS = 1;

    double _now;

    /// <summary>
    /// Whether a flashing light is in the on half of its flash now.
    /// </summary>
    bool FlashOn => _now % FLASH_SECONDS < FLASH_SECONDS / 2;

    /// <summary>
    /// Whether a control's light should be on now.
    /// </summary>
    bool LightOn(string id)
    {
        var (deck, action) = Split(id);
        if (deck is null)
            return false;
        return action switch
        {
            // Steady while playing; flashing while a song's loaded and paused, ready to go.
            "play" => deck.IsPlaying || deck.IsLoaded && FlashOn,
            "cue" => deck.IsLoaded && !deck.IsPlaying,
            "sync" => deck.IsInSync,
            "pfl" => deck.Headphones,
            "keylock" => deck.KeyLocked,
            "slip" => deck.Slip,
            "fx.on" => deck.Effects.On,
            "key.match" => deck.CanKeyMatch && deck.IsKeyMatched,
            "loop" => deck.IsLooping,
            "grid" => IsGridMode?.Invoke(deck) == true,
            "roll" => IsRollMode?.Invoke(deck) == true,
            "jump" => IsJumpMode?.Invoke(deck) == true,
            "hotcues" => IsRollMode?.Invoke(deck) != true && IsGridMode?.Invoke(deck) != true && IsJumpMode?.Invoke(deck) != true,
            // The beat jump pads light while they're beat jumps, so the mode shows on the controller.
            _ when Jump(action) is not null => IsJumpMode?.Invoke(deck) == true,
            _ when Roll(action) is { } roll => deck.RollBeats == RollPads.Sizes[roll],
            // In ROLL mode a pad lights while it's rolling; otherwise while its hotcue's set.
            _ when Hotcue(action) is { } pad && IsRollMode?.Invoke(deck) == true => !action.EndsWith(".delete") && pad < RollPads.Sizes.Count && deck.RollBeats == RollPads.Sizes[pad],
            _ => Hotcue(action) is { } hotcue && hotcue < deck.Hotcues.Count && deck.Hotcues[hotcue] is not null,
        };
    }

    static MidiMessage LightMessage(MidiBinding binding, bool on) =>
        binding.Kind == MidiKind.Note
            ? MidiMessage.Note(binding.Channel, binding.Number, on ? 127 : 0)
            : MidiMessage.Control(binding.Channel, binding.Number, on ? 127 : 0);

    void UpdateLights()
    {
        foreach (var (device, mapping) in _mappings)
        {
            if (!device.CanSend)
                continue;
            foreach (var binding in mapping.Bindings)
            {
                if (!_controls.TryGetValue(binding.Control, out var control) || !control.HasLight)
                    continue;
                var on = LightOn(control.Id);
                var key = (device, binding);
                if (_lights.TryGetValue(key, out var was) && was == on)
                    continue;
                _lights[key] = on;
                device.Send(LightMessage(binding, on));
            }
        }
    }

    /// <summary>
    /// Turns every light off, so the controller doesn't look busy once the app's gone.
    /// </summary>
    public void Dispose()
    {
        foreach (var ((device, binding), on) in _lights)
        {
            if (on)
                device.Send(LightMessage(binding, false));
        }
        _lights.Clear();
        _devices.Connected -= OnConnected;
        _devices.Disconnected -= OnDisconnected;
    }
}
