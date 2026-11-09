using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// A deck's controls and read-out, in the deck's colour: its letter, what's loaded, the tempo where it's playing and
/// its key, how far in it is and how long's left, the whole song's waveform to see and seek in with its hotcues
/// marked, Cue and Play, a loop of so many beats to start and stop, halve and double, a pad for each hotcue (press an
/// empty one to set it where the deck is, a set one to jump there, and Shift-click or right-click one to clear it),
/// and the tempo fader: down is faster, as on a turntable, double-click puts it back to zero, the button under it
/// changes its range, and KEY under that keeps the song's key whatever the fader does. The tempo and key shown are
/// what's coming out, fader and all.
/// <para>
/// The controls you reach for while mixing (the tempo fader, the tempo and key, the loop) sit on the side nearest
/// the mixer, and the song's details, Cue and Play on the outer side. A deck to the right of the mixer is
/// <see cref="Mirrored"/>, so its layout is the left deck's reflected. The waveform still runs left to right, as
/// time does, and the pads still count 1 to 8.
/// </para>
/// Sized by whoever lays it out, with <see cref="Width"/>. Call <see cref="Refresh"/> once a frame.
/// </summary>
sealed class DeckPanel : Panel
{
    public const float HEIGHT = 184;
    const float PAD = 10;

    readonly Deck _deck;
    readonly float4 _color;
    readonly Font _bold;
    readonly Badge _badge;
    readonly Label _title;
    readonly Label _artist;
    readonly Label _bpm;
    readonly Label _key;
    readonly Label _elapsed;
    readonly Label _remaining;

    /// <summary>
    /// How near its end a song's warned of, in seconds, and how fast the warning flashes.
    /// </summary>
    public const double END_WARNING_SECONDS = 30;

    const double END_FLASH_SECONDS = 0.6;
    readonly Label _gain;
    readonly Label _cuePoint;
    readonly Label _loading;
    readonly WaveformBar _overview;
    readonly Button _cue;
    readonly Button _playPause;
    readonly Button _sync;
    readonly List<HotcuePad> _pads = [];
    readonly Button _halveLoop;
    readonly Button _loop;
    readonly Button _doubleLoop;
    readonly Label _tempoReadout;
    readonly Slider _tempoFader;
    readonly Button _tempoRange;
    readonly Button _keyLock;
    readonly Button _keyDown;
    readonly Button _keyMatch;
    readonly Button _keyUp;
    readonly Button _slip;
    readonly Button _memorySet;
    readonly Button _memoryPrevious;
    readonly Button _memoryNext;

    // The effect: which one, its length in beats, how much, and on or off.
    readonly Dropdown<DeckEffect> _fxKind;
    readonly Button _fxShorter;
    readonly Label _fxBeats;
    readonly Button _fxLonger;
    readonly Slider _fxLevel;
    readonly Button _fxOn;
    readonly RollPads? _roll;
    readonly JumpPads? _jump;

    /// <summary>
    /// What the pads do: hotcues, or one of the modes there are for this deck.
    /// </summary>
    enum PadMode
    {
        Hotcues,
        Roll,
        Jump,
        Grid,
    }

    readonly Dropdown<PadMode>? _padMode;

    // The buttons' key hints, hidden while the deck's too narrow for them.
    readonly List<(Button Button, string Hint)> _hints = [];

    // Too narrow for the buttons as they are: shorter, without their key hints (compact); and narrower still, in two
    // rows, which makes the deck taller.
    bool _compact;
    bool _twoRows;
    bool _fitted;
    readonly Button _quantize;
    readonly Button _loopIn;
    readonly Button _loopOut;
    readonly GridEditor? _grid;
    readonly Images? _images;

    /// <summary>
    /// The width of the tempo fader's column down the right-hand side, and the space before it.
    /// </summary>
    const float TEMPO_COLUMN = 48;

    // SLIP, and the key shift's buttons below it, beside the tempo and key.
    const float KEY_SHIFT_WIDTH = 66;

    // The memory cue's buttons, beside those.
    const float MEMORY_WIDTH = 46;
    const float TEMPO_GAP = 10;

    const float PAD_GAP = 4;
    const float PAD_HEIGHT = 28;
    const float ROW_GAP = 6;

    float _width = 480;

    /// <summary>
    /// How wide the deck's laid out. Too narrow for its buttons in a row, they're made shorter, then go in two rows,
    /// the deck taller by one (see <see cref="Height"/>).
    /// </summary>
    public float Width
    {
        get => _width;
        set
        {
            if (value == _width && _fitted)
                return;
            _width = value;
            Fit();
        }
    }

    /// <summary>
    /// How tall the deck is: <see cref="HEIGHT"/>, or more with its buttons in two rows.
    /// </summary>
    public float Height => _twoRows ? HEIGHT + EXTRA_ROW : HEIGHT;

    /// <summary>
    /// The overview's colours: the deck's, or three bands, as the scrolling waveforms are.
    /// </summary>
    public WaveformColours WaveformColours
    {
        set => _overview.Colours = value;
    }

    /// <summary>
    /// Where the pad mode list opens.
    /// </summary>
    public ContextMenu? Menu
    {
        set
        {
            if (_padMode is not null)
                _padMode.Menu = value;
            _fxKind.Menu = value;
        }
    }

    /// <summary>
    /// What a second row of buttons adds to the deck's height.
    /// </summary>
    const float EXTRA_ROW = 36;

    /// <summary>
    /// Whether the buttons are shorter, and in two rows, for testing.
    /// </summary>
    internal (bool Compact, bool TwoRows) Fitting => (_compact, _twoRows);

    /// <summary>
    /// Laid out as a reflection, for a deck to the right of the mixer: tempo fader on the left, details on the
    /// right.
    /// </summary>
    public bool Mirrored { get; set; }

    public Deck Deck => _deck;

    /// <summary>
    /// A song was dragged from the list and dropped on the deck, to load.
    /// </summary>
    public event Action<DeckPanel, Song>? SongDropped;

    /// <param name="padKeys">The keys that press the hotcue pads, in order, to label them with.</param>
    /// <param name="keys">The keys that press its buttons, to label them with.</param>
    /// <param name="grid">Fixes the deck's beat grid by hand, with the pads in GRID mode; no GRID mode if null.</param>
    public DeckPanel(
        Deck deck,
        Font small,
        Font body,
        Font bold,
        Font big,
        IReadOnlyList<string>? padKeys = null,
        DeckKeyHints? keys = null,
        GridEditor? grid = null,
        Images? images = null,
        RollPads? roll = null,
        JumpPads? jump = null
    )
    {
        _images = images;
        _roll = roll;
        _jump = jump;
        _grid = grid;
        _deck = deck;
        _color = Theme.DeckColor(deck.Name);
        _bold = bold;
        Background = Theme.Panel;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;

        _badge = Add(new Badge(bold, deck.Name, _color, Theme.TitleBar, 30));
        _title = Add(new Label(bold) { Color = Theme.Text });
        _artist = Add(new Label(body) { Color = Theme.Muted });
        _bpm = Add(new Label(big) { Color = Theme.Text });
        _key = Add(new Label(bold));
        _elapsed = Add(new Label(body) { Color = Theme.Text });
        _remaining = Add(new Label(body) { Color = _color });
        _gain = Add(new Label(body) { Color = Theme.Muted });
        _cuePoint = Add(new Label(body) { Color = Theme.Muted });
        _loading = Add(new Label(body) { Color = Theme.Muted });

        // The song's waveform, coloured as it plays, with a thin playhead and a mark at the cue point; click or drag
        // to seek.
        _overview = Add(
            new WaveformBar
            {
                Thickness = 20,
                TrackThickness = 6,
                HandleLength = 2,
                ShowFill = true,
                FillColor = Theme.Fade(_color, 0.6f),
                PlayedColor = _color,
                UnplayedColor = Colors.Hex(0xFFFFFF, 0.3f),
                HandleColor = Colors.White,
                FocusColor = Theme.Focus,
                FlagFont = small,
            }
        );
        _overview.Changed += slider => _deck.Seek(TimeSpan.FromSeconds(slider.Value));
        _overview.Tooltip = "The whole song: click or drag to jump there";

        // Cue acts on press and release rather than click, since holding it previews from the cue point.
        _cue = Add(
            new Button(bold, "CUE")
            {
                MinSize = new float2(64, 30),
                FocusColor = Theme.Focus,
                Tooltip = "Playing, back to the cue point. Paused, sets it here, playing from it while held",
            }
        );
        _cue.PressStarted += _ => _deck.CueDown();
        _cue.PressEnded += _ => _deck.CueUp();

        _playPause = Add(new Button(bold, "PLAY") { MinSize = new float2(84, 30), FocusColor = Theme.Focus, Tooltip = "Play or pause" });
        _playPause.Clicked += _ => _deck.TogglePlay();

        // Sync matches the other deck's tempo, and its beats if both are playing; it stays lit while they match.
        _sync = Add(
            new Button(bold, "SYNC")
            {
                MinSize = new float2(64, 30),
                FocusColor = Theme.Focus,
                Tooltip = "Match the other deck's tempo, and its beats if both are playing; lit while they match",
            }
        );
        _sync.Clicked += _ => _deck.Sync();

        // The loop: halve it, start or stop it (its size on the button), double it.
        _halveLoop = Add(new Button(bold, "") { Icon = images?.TriangleLeft, MinSize = new float2(26, 30), FocusColor = Theme.Focus, Tooltip = "Halve the loop" });
        _halveLoop.Clicked += _ => _deck.HalveLoop();
        _loop = Add(
            new Button(bold, "LOOP 4")
            {
                MinSize = new float2(84, 30),
                FocusColor = Theme.Focus,
                Tooltip = "Loop this many beats from here, in time with the beat grid; again to let it go",
            }
        );
        _loop.Clicked += _ => _deck.ToggleLoop();
        _doubleLoop = Add(new Button(bold, "") { Icon = images?.TriangleRight, MinSize = new float2(26, 30), FocusColor = Theme.Focus, Tooltip = "Double the loop" });
        _doubleLoop.Clicked += _ => _deck.DoubleLoop();

        // Quantise: lit while hotcues and the cue point go on the beat, and jumps to them stay in time.
        _quantize = Add(
            new Button(bold, "Q")
            {
                MinSize = new float2(30, 30),
                FocusColor = Theme.Focus,
                Tooltip = "Quantise: hotcues and the cue point go on the nearest beat, and jumping to a hotcue while playing stays in time",
            }
        );
        _quantize.Clicked += _ => _deck.Quantize = !_deck.Quantize;

        // Slip: lit while a scratch, a held hotcue or a loop drops back in where the song would have got to.
        _slip = Add(
            new Button(small, "SLIP")
            {
                MinSize = new float2(KEY_SHIFT_WIDTH, 22),
                Padding = 3,
                FocusColor = Theme.Focus,
                Tooltip = "Slip: a scratch, a hotcue held down or a loop lasts only while it's held or looping, then the song carries on from where it would have got to",
            }
        );
        _slip.Clicked += _ => _deck.Slip = !_deck.Slip;

        // Memory cues: MEM adds one where the deck is or takes away the one it's at; the arrows go between them.
        _memorySet = Add(
            new Button(small, "MEM")
            {
                MinSize = new float2(MEMORY_WIDTH, 22),
                Padding = 3,
                FocusColor = Theme.Focus,
                Tooltip = "Memory cue: add one here, or take away the one the deck is at. They show red on the overview",
            }
        );
        _memorySet.Clicked += _ => _deck.ToggleMemoryCue();
        _memoryPrevious = Add(
            new Button(small, "")
            {
                Icon = images?.TriangleLeft,
                MinSize = new float2((MEMORY_WIDTH - 4) / 2, 20),
                Padding = 2,
                FocusColor = Theme.Focus,
                Tooltip = "Go to the memory cue before here, and make it the cue point",
            }
        );
        _memoryPrevious.Clicked += _ => _deck.JumpMemoryCue(-1);
        _memoryNext = Add(
            new Button(small, "")
            {
                Icon = images?.TriangleRight,
                MinSize = new float2((MEMORY_WIDTH - 4) / 2, 20),
                Padding = 2,
                FocusColor = Theme.Focus,
                Tooltip = "Go to the memory cue after here, and make it the cue point",
            }
        );
        _memoryNext.Clicked += _ => _deck.JumpMemoryCue(1);

        // A loop set by hand: IN marks its start, OUT its end, looping straight back.
        _loopIn = Add(
            new Button(small, "IN")
            {
                MinSize = new float2(34, 30),
                FocusColor = Theme.Focus,
                Tooltip = "Loop in: mark where a loop starts, on the beat with Q lit",
            }
        );
        _loopIn.Clicked += _ => _deck.LoopIn();
        _loopOut = Add(
            new Button(small, "OUT")
            {
                MinSize = new float2(34, 30),
                FocusColor = Theme.Focus,
                Tooltip = "Loop out: loop from loop in to here. While it loops, moves its end to here",
            }
        );
        _loopOut.Clicked += _ => _deck.LoopOut();

        // The effect, beside the times: chosen from a list, its length in beats (for echo and flanger) stepped shorter
        // or longer, how much of it there is, and FX to switch it on and off.
        _fxKind = Add(
            new Dropdown<DeckEffect>(small)
            {
                Items = () => Enum.GetValues<DeckEffect>(),
                Text = kind => kind switch
                {
                    DeckEffect.Echo => "ECHO",
                    DeckEffect.Reverb => "REVERB",
                    _ => "FLANGER",
                },
                Width = 78,
                Padding = 3,
                Tooltip = "The deck's effect: an echo in time with the beat, reverb, or a flanger sweeping in time",
            }
        );
        _fxKind.Chosen += kind => _deck.Effects.Kind = kind;
        _fxShorter = Add(
            new Button(small, "")
            {
                Icon = images?.TriangleLeft,
                MinSize = new float2(18, 20),
                Padding = 2,
                FocusColor = Theme.Focus,
                Tooltip = "Fewer beats: closer echoes, or a faster sweep",
            }
        );
        _fxShorter.Clicked += _ => _deck.Effects.StepBeats(-1);
        _fxBeats = Add(new Label(small) { Color = Theme.Text });
        _fxLonger = Add(
            new Button(small, "")
            {
                Icon = images?.TriangleRight,
                MinSize = new float2(18, 20),
                Padding = 2,
                FocusColor = Theme.Focus,
                Tooltip = "More beats: echoes further apart, or a slower sweep",
            }
        );
        _fxLonger.Clicked += _ => _deck.Effects.StepBeats(1);
        _fxLevel = Add(
            new Slider
            {
                Length = FX_LEVEL_LENGTH,
                Thickness = 14,
                TrackThickness = 3,
                HandleLength = 8,
                DefaultValue = 0.6f,
                Value = deck.Effects.Level,
                ShowFill = true,
                FillColor = Theme.Accent,
                FocusColor = Theme.Focus,
                Tooltip = "How much of the effect: the echoes' or reverb's level, or the flanger's depth. Double-click for the usual amount",
            }
        );
        _fxLevel.Changed += slider => _deck.Effects.Level = slider.Value;
        _fxOn = Add(
            new Button(small, "FX")
            {
                MinSize = new float2(34, 20),
                Padding = 3,
                FocusColor = Theme.Focus,
                Tooltip = "The effect on or off. Echo and reverb ring on as they fade when it's switched off",
            }
        );
        _fxOn.Clicked += _ => _deck.Effects.On = !_deck.Effects.On;

        // The tempo fader runs from +range at the bottom to -range at the top, so pulling it down speeds up.
        _tempoReadout = Add(new Label(small) { Color = Theme.Muted });
        _tempoFader = Add(
            new Slider
            {
                Orientation = Orientation.Vertical,
                Min = (float)deck.TempoRange,
                Max = -(float)deck.TempoRange,
                Value = (float)deck.Tempo,
                DefaultValue = 0,
                Thickness = 22,
                HandleLength = 10,
                FocusColor = Theme.Focus,
                Tooltip = "Tempo: down is faster, as on a turntable. Double-click to put it back to 0",
            }
        );
        _tempoFader.Marks.Add(0);
        _tempoFader.Changed += slider => _deck.Tempo = slider.Value;
        _tempoRange = Add(
            new Button(small, "±8%")
            {
                MinSize = new float2(TEMPO_COLUMN, 22),
                Padding = 3,
                FocusColor = Theme.Focus,
                Tooltip = "How far the tempo fader goes: ±8%, ±16% or ±50%",
            }
        );
        _keyLock = Add(
            new Button(small, "KEY")
            {
                MinSize = new float2(TEMPO_COLUMN, 22),
                Padding = 3,
                FocusColor = Theme.Focus,
                Tooltip = "Key lock: keeps the song in its own key whatever its tempo",
            }
        );
        _keyLock.Clicked += _ => _deck.KeyLocked = !_deck.KeyLocked;

        // The key down or up a semitone, the tempo left alone; a right-click on either puts it back.
        _keyDown = Add(
            new Button(small, "-")
            {
                MinSize = new float2(KEY_BUTTON_WIDTH, 20),
                Padding = 2,
                FocusColor = Theme.Focus,
                Tooltip = "Key shift: down a semitone, the tempo left alone. Right-click to put the key back",
            }
        );
        _keyUp = Add(
            new Button(small, "+")
            {
                MinSize = new float2(KEY_BUTTON_WIDTH, 20),
                Padding = 2,
                FocusColor = Theme.Focus,
                Tooltip = "Key shift: up a semitone, the tempo left alone. Right-click to put the key back",
            }
        );
        _keyDown.Clicked += _ => _deck.KeyShift--;
        _keyUp.Clicked += _ => _deck.KeyShift++;
        _keyDown.SecondaryClicked += _ => _deck.KeyShift = 0;
        _keyUp.SecondaryClicked += _ => _deck.KeyShift = 0;

        // Key match, between them: the key moved to the other deck's, lit while the two match.
        _keyMatch = Add(
            new Button(small, "=")
            {
                MinSize = new float2(KEY_BUTTON_WIDTH, 20),
                Padding = 2,
                FocusColor = Theme.Focus,
                Tooltip = "Key match: shift the key as little as it takes to mix with the other deck's (its key, or its relative major or minor), with key lock on. Lit while they match",
            }
        );
        _keyMatch.Clicked += _ => _deck.KeyMatch();
        _keyMatch.SecondaryClicked += _ => _deck.KeyShift = 0;
        _tempoRange.Clicked += _ =>
        {
            var ranges = Deck.TempoRanges;
            var next = ranges[(ranges.ToList().IndexOf(_deck.TempoRange) + 1) % ranges.Count];
            _deck.TempoRange = next;
        };

        if (keys is not null)
        {
            foreach (var (button, hint) in new[] { (_cue, keys.Cue), (_playPause, keys.Play), (_sync, keys.Sync), (_halveLoop, keys.HalveLoop), (_loop, keys.Loop), (_doubleLoop, keys.DoubleLoop), (_keyLock, keys.KeyLock) })
            {
                (button.KeyHint, button.HintFont) = (hint, small);
                if (button != _keyLock)
                    _hints.Add((button, hint));
            }
        }

        // What the pads do, chosen from a list: hotcues, loop rolls held as long as each pad is, beat jumps, or tools
        // for fixing the beat grid by hand. Only the modes there are for this deck are listed.
        if (grid is not null || roll is not null || jump is not null)
        {
            List<PadMode> modes = [PadMode.Hotcues];
            if (roll is not null)
                modes.Add(PadMode.Roll);
            if (jump is not null)
                modes.Add(PadMode.Jump);
            if (grid is not null)
                modes.Add(PadMode.Grid);
            _padMode = Add(
                new Dropdown<PadMode>(bold)
                {
                    Items = () => modes,
                    Text = mode => (mode, _compact) switch
                    {
                        (PadMode.Hotcues, false) => "HOT CUE",
                        (PadMode.Hotcues, true) => "CUES",
                        (PadMode.Roll, _) => "ROLL",
                        (PadMode.Jump, _) => "JUMP",
                        _ => "GRID",
                    },
                    Width = 96,
                    Tooltip = "What the pads do: hotcues, loop rolls held as long as each pad is, beat jumps back and on staying in time, or tools for fixing the beat grid by hand",
                }
            );
            _padMode.Chosen += mode =>
            {
                // Each turned on turns the others off; hotcues is all of them off.
                if (mode == PadMode.Roll && roll is not null)
                    roll.Active = true;
                else if (mode == PadMode.Jump && jump is not null)
                    jump.Active = true;
                else if (mode == PadMode.Grid && grid is not null)
                    grid.Active = true;
                else
                {
                    if (roll is not null)
                        roll.Active = false;
                    if (jump is not null)
                        jump.Active = false;
                    if (grid is not null)
                        grid.Active = false;
                }
            };
        }

        for (var i = 0; i < Deck.HOTCUE_COUNT; i++)
        {
            var index = i;
            var pad = Add(
                new HotcuePad(bold, i + 1)
                {
                    Color = Theme.HotcueColor(i),
                    FocusColor = Theme.Focus,
                    KeyHint = padKeys is not null && i < padKeys.Count ? padKeys[i] : null,
                    HintFont = small,
                }
            );
            // In GRID mode each pad is a tool; Shift (or a right-click) makes a nudge a fine one. In ROLL mode each
            // rolls a loop while it's held.
            pad.Triggered += _ =>
            {
                if (_grid is { Active: true })
                    _grid.Pad(index);
                else if (_roll is { Active: true })
                    _roll.Down(index);
                else if (_jump is { Active: true })
                    _jump.Down(index);
                else
                    _deck.HotcueDown(index);
            };
            pad.PressEnded += _ =>
            {
                _roll?.Up(index);
                // Under slip, a hotcue held lasts until it's let go.
                _deck.HotcueUp(index);
            };
            pad.Deleted += _ =>
            {
                if (_grid is { Active: true })
                    _grid.Pad(index, fine: true);
                else
                    _deck.DeleteHotcue(index);
            };
            _pads.Add(pad);
        }

        Refresh();
    }

    /// <summary>
    /// What a pad does in GRID mode, at the playhead.
    /// </summary>
    static string GridTooltip(GridTool tool) =>
        tool switch
        {
            GridTool.SetBeat => "Pin the nearest beat to the playhead: one pin moves the grid, two or more set its tempo. Shift takes out the nearest pin",
            GridTool.Downbeat => "Make the beat at the playhead the first of its bar",
            GridTool.NudgeEarlier => "Move the grid earlier; Shift for a finer step",
            GridTool.NudgeLater => "Move the grid later; Shift for a finer step",
            GridTool.Halve => "Halve the grid's tempo",
            GridTool.Double => "Double the grid's tempo",
            GridTool.Steady => "One steady tempo in place of a drifting one, keeping the beat at the playhead",
            _ => "Put the grid back as it was analysed",
        };

    public void Refresh()
    {
        RefreshTempo();
        if (_padMode is not null)
        {
            var mode = _grid is { Active: true } ? PadMode.Grid
                : _roll is { Active: true } ? PadMode.Roll
                : _jump is { Active: true } ? PadMode.Jump
                : PadMode.Hotcues;
            _padMode.Selected = mode;
            var (tint, amount) = mode switch
            {
                PadMode.Grid => (Theme.Accent, 0.6f),
                PadMode.Roll => (Theme.Loop, 0.5f),
                PadMode.Jump => (Theme.Focus, 0.5f),
                _ => (Theme.PanelRaised, 0f),
            };
            _padMode.Background = amount > 0 ? Theme.Mix(Theme.PanelRaised, tint, amount) : Colors.Hex(0x2A2A35);
            _padMode.HoverBackground = amount > 0 ? Theme.Mix(Theme.PanelRaised, tint, amount + 0.15f) : Colors.Hex(0x3A3A48);
            _padMode.ArrowColor = amount > 0 ? Colors.White : Theme.Muted;
        }
        var quantized = _deck.Quantize;
        _quantize.Background = quantized ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
        _quantize.HoverBackground = quantized ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
        _quantize.TextColor = quantized ? Colors.White : Theme.Muted;
        var slip = _deck.Slip;
        _slip.Background = slip ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
        _slip.HoverBackground = slip ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
        _slip.TextColor = slip ? Colors.White : Theme.Muted;

        var atMemoryCue = _deck.IsAtMemoryCue;
        _memorySet.Enabled = _deck.IsLoaded;
        _memorySet.Background = atMemoryCue ? Theme.Mix(Theme.PanelRaised, Theme.Warning, 0.6f) : Colors.Hex(0x2A2A35);
        _memorySet.HoverBackground = atMemoryCue ? Theme.Mix(Theme.PanelRaised, Theme.Warning, 0.75f) : Colors.Hex(0x3A3A48);
        _memorySet.TextColor = atMemoryCue ? Colors.White : Theme.Muted;
        _memoryPrevious.Enabled = _memoryNext.Enabled = _deck.MemoryCues.Count > 0;

        var fx = _deck.Effects;
        _fxKind.Selected = fx.Kind;
        _fxBeats.Text = fx.HasBeats ? FormatFxBeats(fx.Beats) : "";
        _fxShorter.Enabled = _fxLonger.Enabled = fx.HasBeats;
        if (!_fxLevel.IsDragging)
            _fxLevel.Value = fx.Level;
        _fxOn.Background = fx.On ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
        _fxOn.HoverBackground = fx.On ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
        _fxOn.TextColor = fx.On ? Colors.White : Theme.Muted;
        _loading.Text = _deck.LoadingName is { } loading ? $"Loading {loading}…" : "";

        if (!_deck.IsLoaded)
        {
            _title.Text = _deck.IsLoading ? "Loading…" : "No track loaded";
            _artist.Text = "";
            _bpm.Text = "";
            _key.Text = "";
            _elapsed.Text = "";
            _remaining.Text = "";
            _overview.WarningFrom = null;
            _gain.Text = "";
            _cuePoint.Text = "";
            _cue.Enabled = false;
            _playPause.Enabled = false;
            _playPause.Text = "PLAY";
            _sync.Enabled = false;
            _overview.Enabled = false;
            _overview.Max = 1;
            _overview.Value = 0;
            _overview.Marks.Clear();
            _overview.Waveform = null;
            _overview.Detail = null;
            _overview.MemoryCues = [];
            _overview.Grid = null;
            _overview.Flags = [];
            _overview.Region = null;
            _overview.Pins = [];
            foreach (var pad in _pads)
                (pad.Enabled, pad.IsSet) = (false, false);
            RefreshLoop();
            return;
        }

        _title.Text = _deck.Title;
        _artist.Text = _deck.Artist;

        var position = _deck.Position;
        _elapsed.Text = $"{Format(position)} / {Format(_deck.Duration)}";
        _remaining.Text = $"-{Format(_deck.Duration - position)}";

        // The song's last moments: the time left, and the end of the waveform, go red, flashing while it plays.
        var ending = _deck.Duration.TotalSeconds > END_WARNING_SECONDS && (_deck.Duration - position).TotalSeconds <= END_WARNING_SECONDS;
        var flashOn = !_deck.IsPlaying || Clock.Seconds() % END_FLASH_SECONDS < END_FLASH_SECONDS / 2;
        _remaining.Color = !ending ? _color : flashOn ? Theme.Warning : Theme.Text;
        _overview.WarningFrom = ending && flashOn ? (float)(_deck.Duration.TotalSeconds - END_WARNING_SECONDS) : null;
        _cuePoint.Text = $"Cue {Format(_deck.CuePoint)}";

        // The tempo where the deck is, which moves on through a song whose tempo varies; "~" says it does.
        if (_deck.Analysis is { } analysis)
        {
            // As heard: the fader speeds the tempo up and, past about 3%, moves the key a semitone or more.
            var key = _deck.Key ?? analysis.Key;
            _bpm.Text = $"{(analysis.Grid.IsVariable ? "~" : "")}{_deck.Bpm ?? analysis.Bpm:F2}";
            _key.Text = $"{key.Camelot}  {key.ShortName}{(_deck.KeyChange != 0 ? $"  ({_deck.KeyChange:+0;-0})" : "")}";
            _key.Color = Theme.CamelotColor(key.CamelotNumber);
            _gain.Text = $"Auto gain {analysis.AutoGainDb:+0.0;-0.0;0.0} dB";
        }
        else
        {
            _bpm.Text = "";
            _key.Text = "";
            _gain.Text = "Analysing…";
        }

        _overview.Enabled = true;
        _overview.Waveform = _deck.Waveform;
        _overview.Detail = _deck.Detail;
        _overview.MemoryCues = _deck.MemoryCues;
        _overview.Grid = _deck.Analysis?.Grid;
        _overview.Pins = _grid?.Pins ?? [];
        _overview.Max = (float)_deck.Duration.TotalSeconds;
        // While it's being dragged the slider leads and the deck follows, so don't pull it back.
        if (!_overview.IsDragging)
            _overview.Value = (float)position.TotalSeconds;
        _overview.Marks.Clear();
        _overview.Marks.Add((float)_deck.CuePoint.TotalSeconds);

        var flags = new List<(float, float4, string)>();
        var gridMode = _grid is { Active: true };
        var rollMode = !gridMode && _roll is { Active: true };
        var jumpMode = !gridMode && !rollMode && _jump is { Active: true };
        for (var i = 0; i < _pads.Count; i++)
        {
            var hotcue = _deck.Hotcues[i];
            (_pads[i].Enabled, _pads[i].IsSet) = (true, rollMode ? _roll!.IsRolling(i) : hotcue is not null && !gridMode && !jumpMode);
            _pads[i].Text = gridMode ? GridEditor.Label(GridEditor.Pads[i]) : rollMode ? RollPads.Label(i) : jumpMode ? JumpPads.Label(i) : null;
            var detail = gridMode || rollMode || jumpMode ? null : _deck.DetailOf(i);
            if (detail?.Name is { } name)
                _pads[i].Text = name;
            var direction = gridMode ? GridEditor.Direction(GridEditor.Pads[i]) : jumpMode ? JumpPads.Direction(i) : 0;
            _pads[i].Icon = direction < 0 ? _images?.TriangleLeft : direction > 0 ? _images?.TriangleRight : null;
            _pads[i].IconAfter = direction > 0;
            _pads[i].Color = gridMode ? Theme.Accent : rollMode ? Theme.Loop : jumpMode ? Theme.Focus : detail?.Color is { } color ? Colors.Hex(color) : Theme.HotcueColor(i);
            var jumpBeats = Math.Abs(JumpPads.Sizes[i]);
            _pads[i].Tooltip =
                gridMode ? GridTooltip(GridEditor.Pads[i])
                : rollMode ? $"Loops {RollPads.Label(i)} {(RollPads.Sizes[i] > 1 ? "beats" : "beat")} while held, then carries on from where the song would have got to"
                : jumpMode ? $"Jump {jumpBeats:0} {(jumpBeats == 1 ? "beat" : "beats")} {(JumpPads.Sizes[i] < 0 ? "back" : "on")}, staying in time; a loop goes with it"
                : detail?.LoopEnd is not null ? $"Hot loop {i + 1}{(detail.Name is { } loopName ? $", {loopName}" : "")}: jump to it and loop. Shift-click or right-click to clear it"
                : hotcue is not null ? $"Hotcue {i + 1}{(detail?.Name is { } cueName ? $", {cueName}" : "")}: jump to it. Shift-click or right-click to clear it"
                : $"Hotcue {i + 1}: set it here";
            if (hotcue is { } at)
                flags.Add(((float)at.TotalSeconds, Theme.HotcueColor(i), (i + 1).ToString()));
        }
        _overview.Flags = flags;
        _overview.Region = _deck.Loop is { } loop ? ((float)loop.Start.TotalSeconds, (float)loop.End.TotalSeconds) : null;
        _overview.Ghost = _deck.SlipPosition is { } slipped ? (float)slipped.TotalSeconds : null;
        RefreshLoop();

        _cue.Enabled = true;
        _playPause.Enabled = true;
        // A cue preview is playing, but Play would latch it rather than pause, so it still offers Play.
        var playing = _deck.IsPlaying && !_deck.IsCuePreviewing;
        _playPause.Text = playing ? "PAUSE" : "PLAY";
        _playPause.Background = playing ? Theme.Mix(Theme.PanelRaised, _color, 0.55f) : Colors.Hex(0x2A2A35);
        _playPause.HoverBackground = playing ? Theme.Mix(Theme.PanelRaised, _color, 0.7f) : Colors.Hex(0x3A3A48);

        _sync.Enabled = _deck.CanSync;
        var inSync = _sync.Enabled && _deck.IsInSync;
        _sync.Background = inSync ? Theme.Mix(Theme.PanelRaised, _color, 0.55f) : Colors.Hex(0x2A2A35);
        _sync.HoverBackground = inSync ? Theme.Mix(Theme.PanelRaised, _color, 0.7f) : Colors.Hex(0x3A3A48);
    }

    /// <summary>
    /// The tempo fader follows the deck, which keyboard or code may have moved, except while it's being dragged.
    /// </summary>
    void RefreshTempo()
    {
        _tempoFader.Min = (float)_deck.TempoRange;
        _tempoFader.Max = -(float)_deck.TempoRange;
        if (!_tempoFader.IsDragging)
            _tempoFader.Value = (float)_deck.Tempo;
        _tempoReadout.Text = $"{_deck.Tempo * 100:+0.0;-0.0;0.0}%";
        _tempoReadout.Color = _deck.Tempo == 0 ? Theme.Muted : Theme.Text;
        _tempoRange.Text = $"±{_deck.TempoRange * 100:0}%";

        // Lit while the key is held, whatever the fader does.
        var locked = _deck.KeyLocked;
        _keyLock.Background = locked ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
        _keyLock.HoverBackground = locked ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
        _keyLock.TextColor = locked ? Colors.White : Theme.Muted;

        // Lit on the side the key's been shifted to.
        foreach (var (button, lit) in new[] { (_keyDown, _deck.KeyShift < 0), (_keyUp, _deck.KeyShift > 0) })
        {
            button.Background = lit ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
            button.HoverBackground = lit ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
            button.TextColor = lit ? Colors.White : Theme.Muted;
        }
        _keyDown.Enabled = _deck.KeyShift > -Deck.MAX_KEY_SHIFT;
        _keyUp.Enabled = _deck.KeyShift < Deck.MAX_KEY_SHIFT;
        var canMatch = _deck.CanKeyMatch;
        var matched = canMatch && _deck.IsKeyMatched;
        _keyMatch.Enabled = canMatch;
        _keyMatch.Background = matched ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
        _keyMatch.HoverBackground = matched ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
        _keyMatch.TextColor = matched ? Colors.White : Theme.Muted;
    }

    void RefreshLoop()
    {
        // A loop set by hand says how long it is, if there's a beat grid to say it by.
        var loopWord = _compact ? "" : "LOOP ";
        _loop.Text = !_deck.IsManualLoop ? $"{loopWord}{FormatBeats(_deck.LoopBeats)}"
            : _deck.LoopLengthBeats is { } beats ? $"{loopWord}{FormatLength(beats)}"
            : "LOOP";
        // Looping, the loop can always be halved, doubled and left, even one set by hand without a beat grid.
        var looping = _deck.IsLooping;
        var canLoop = _deck.CanLoop || looping;
        _loop.Enabled = canLoop;
        _halveLoop.Enabled = _doubleLoop.Enabled = canLoop;

        _loopIn.Enabled = _deck.IsLoaded;
        _loopOut.Enabled = _deck.IsLoaded && (_deck.LoopInPoint is not null || _deck.IsManualLoop);
        var marked = _deck.IsLoaded && _deck.LoopInPoint is not null;
        _loopIn.Background = marked ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.35f) : Colors.Hex(0x2A2A35);
        _loopIn.HoverBackground = marked ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.5f) : Colors.Hex(0x3A3A48);

        _loop.Background = looping ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.6f) : Colors.Hex(0x2A2A35);
        _loop.HoverBackground = looping ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.75f) : Colors.Hex(0x3A3A48);
        _loop.TextColor = looping ? Theme.TitleBar : Theme.Text;
    }

    /// <summary>
    /// An effect's length in beats: as <see cref="FormatBeats"/>, with three quarters as "3/4".
    /// </summary>
    static string FormatFxBeats(double beats) => beats == 0.75 ? "3/4" : FormatBeats(beats);

    /// <summary>
    /// The effect's level slider's length, and the room its beats take, wide enough for "1/16".
    /// </summary>
    const float FX_LEVEL_LENGTH = 48;

    const float FX_BEATS_WIDTH = 28;

    /// <summary>
    /// The key shift's buttons, three across under SLIP.
    /// </summary>
    const float KEY_BUTTON_WIDTH = (KEY_SHIFT_WIDTH - 6) / 3;

    /// <summary>
    /// A loop's length as DJs say it: "4", or "1/2" for half a beat.
    /// </summary>
    public static string FormatBeats(double beats) => beats >= 1 ? $"{beats:0}" : $"1/{Math.Round(1 / beats):0}";

    /// <summary>
    /// A loop set by hand's length in beats: as <see cref="FormatBeats"/> for a whole power of two, as a loop by
    /// beats would be, or else to a tenth of a beat, "3.5".
    /// </summary>
    public static string FormatLength(double beats)
    {
        var power = Math.Pow(2, Math.Round(Math.Log2(Math.Max(beats, 1.0 / 64))));
        return Math.Abs(beats - power) < 0.02 ? FormatBeats(power) : $"{beats:0.#}";
    }

    // The width of the transport and pad mode, from the outer edge, and of the loop, and of loop in and out with
    // quantise, from the inner edge, with the gaps between them as they're laid out.
    float TransportWidth => _cue.Size.X + 6 + _playPause.Size.X + 6 + _sync.Size.X + (_padMode is null ? 0 : 12 + _padMode.Size.X);
    float LoopWidth => _halveLoop.Size.X + 4 + _loop.Size.X + 4 + _doubleLoop.Size.X;
    float InOutWidth => _quantize.Size.X + 6 + _loopIn.Size.X + 4 + _loopOut.Size.X;

    float ContentWidth => Width - 2 * PAD - TEMPO_COLUMN - TEMPO_GAP;

    /// <summary>
    /// Makes the buttons shorter, then puts them in two rows, as far as the width needs.
    /// </summary>
    void Fit()
    {
        _fitted = true;
        foreach (var compact in new[] { false, true })
        {
            Compact(compact);
            if (TransportWidth + 12 + InOutWidth + 6 + LoopWidth <= ContentWidth)
            {
                _twoRows = false;
                return;
            }
        }
        _twoRows = true;
    }

    /// <summary>
    /// Shorter buttons without their key hints, or as they were, measured.
    /// </summary>
    void Compact(bool compact)
    {
        _compact = compact;
        foreach (var (button, hint) in _hints)
            button.KeyHint = compact ? null : hint;
        _cue.MinSize = new float2(compact ? 48 : 64, 30);
        _playPause.MinSize = new float2(compact ? 56 : 84, 30);
        _sync.MinSize = new float2(compact ? 52 : 64, 30);
        _loop.MinSize = new float2(compact ? 44 : 84, 30);
        if (_padMode is not null)
            _padMode.Width = compact ? 70 : 96;
        RefreshLoop();
        foreach (var element in new Element?[] { _cue, _playPause, _sync, _loop, _halveLoop, _doubleLoop, _quantize, _loopIn, _loopOut, _padMode })
            element?.Measure();
    }

    public override float2 Measure()
    {
        if (!_fitted)
            Fit();
        // The title gets what the tempo and key on the right leave it.
        _bpm.Measure();
        _key.Measure();
        var right = MathF.Max(MathF.Max(_bpm.Size.X, _key.Size.X), 90);
        var content = Width - 2 * PAD - TEMPO_COLUMN - TEMPO_GAP;
        _title.MaxWidth = _artist.MaxWidth = MathF.Max(40, content - 40 - right - 8 - KEY_SHIFT_WIDTH - 6 - MEMORY_WIDTH - 16);
        _overview.Length = content;
        // The pads share the width between them; the loading line gets what the buttons either side leave it.
        var padWidth = MathF.Floor((Width - 2 * PAD - (_pads.Count - 1) * PAD_GAP) / _pads.Count);
        foreach (var pad in _pads)
            pad.FixedSize = new float2(padWidth, PAD_HEIGHT);
        foreach (var element in new Element?[] { _cue, _playPause, _sync, _loop, _halveLoop, _doubleLoop, _quantize, _loopIn, _loopOut, _padMode })
            element?.Measure();
        _loading.MaxWidth = MathF.Max(0, _twoRows ? content - InOutWidth - 24 : content - TransportWidth - 12 - InOutWidth - 6 - LoopWidth - 24);

        // The fader fills its column between the readout above and the range button below, down to the transport.
        _tempoReadout.Measure();
        _tempoRange.Measure();
        var columnHeight = Height - 2 * PAD - PAD_HEIGHT - ROW_GAP;
        _keyLock.Measure();
        _tempoFader.Length = columnHeight - _tempoReadout.Size.Y - 4 - _tempoRange.Size.Y - 4 - _keyLock.Size.Y - 6;

        foreach (var child in Children)
            child.Measure();
        return Size = new float2(Width, Height);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var top = position.Y + PAD;

        // The tempo column is on the side nearest the mixer; the rest of the deck is the content beside it.
        var columnLeft = Mirrored ? position.X + PAD : position.X + Width - PAD - TEMPO_COLUMN;
        var contentLeft = Mirrored ? columnLeft + TEMPO_COLUMN + TEMPO_GAP : position.X + PAD;
        var contentRight = Mirrored ? position.X + Width - PAD : columnLeft - TEMPO_GAP;

        // Within the content, things go in from the outer edge (the song's details, the transport) or from the inner
        // edge beside the tempo column (its readouts, the loop), whichever side that is for this deck.
        float Outer(float offset, Element child) => Mirrored ? contentRight - offset - child.Size.X : contentLeft + offset;
        float Inner(float offset, Element child) => Mirrored ? contentLeft + offset : contentRight - offset - child.Size.X;
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));

        Place(_badge, Outer(0, _badge), top + 2);
        Place(_title, Outer(40, _title), top);
        Place(_artist, Outer(40, _artist), top + _title.Size.Y + 1);
        Place(_bpm, Inner(0, _bpm), top - 6);
        Place(_key, Inner(0, _key), top + _bpm.Size.Y - 6);

        // SLIP by the tempo, and the key shift's - and + (in that order either side) by the key.
        var right = MathF.Max(MathF.Max(_bpm.Size.X, _key.Size.X), 90);
        var shiftLeft = Mirrored ? contentLeft + right + 8 : contentRight - right - 8 - KEY_SHIFT_WIDTH;
        Place(_slip, shiftLeft, top);
        Place(_keyDown, shiftLeft, top + _slip.Size.Y + 4);
        Place(_keyMatch, shiftLeft + (KEY_SHIFT_WIDTH - _keyMatch.Size.X) / 2, top + _slip.Size.Y + 4);
        Place(_keyUp, shiftLeft + KEY_SHIFT_WIDTH - _keyUp.Size.X, top + _slip.Size.Y + 4);
        var memoryLeft = Mirrored ? shiftLeft + KEY_SHIFT_WIDTH + 6 : shiftLeft - 6 - MEMORY_WIDTH;
        Place(_memorySet, memoryLeft, top);
        Place(_memoryPrevious, memoryLeft, top + _slip.Size.Y + 4);
        Place(_memoryNext, memoryLeft + MEMORY_WIDTH - _memoryNext.Size.X, top + _slip.Size.Y + 4);

        var timesY = top + 52;
        Place(_elapsed, Outer(0, _elapsed), timesY);
        Place(_remaining, Outer(_elapsed.Size.X + 12, _remaining), timesY);

        // The effect's controls from the inner edge, in the same order either side; then, as room allows, the cue
        // point after the times, and the automatic gain beside the effect.
        var fxWidth = _fxKind.Size.X + 6 + _fxShorter.Size.X + 2 + FX_BEATS_WIDTH + 2 + _fxLonger.Size.X + 6 + _fxLevel.Size.X + 6 + _fxOn.Size.X;
        var fxX = Mirrored ? contentLeft : contentRight - fxWidth;
        float Centred(Element child) => timesY + (_elapsed.Size.Y - child.Size.Y) / 2;
        Place(_fxKind, fxX, Centred(_fxKind));
        fxX += _fxKind.Size.X + 6;
        Place(_fxShorter, fxX, Centred(_fxShorter));
        fxX += _fxShorter.Size.X + 2;
        Place(_fxBeats, fxX + (FX_BEATS_WIDTH - _fxBeats.Size.X) / 2, Centred(_fxBeats));
        fxX += FX_BEATS_WIDTH + 2;
        Place(_fxLonger, fxX, Centred(_fxLonger));
        fxX += _fxLonger.Size.X + 6;
        Place(_fxLevel, fxX, Centred(_fxLevel));
        fxX += _fxLevel.Size.X + 6;
        Place(_fxOn, fxX, Centred(_fxOn));

        var content = contentRight - contentLeft;
        var times = _elapsed.Size.X + 12 + _remaining.Size.X;
        _cuePoint.Visible = times + 16 + _cuePoint.Size.X + 12 + fxWidth <= content;
        if (_cuePoint.Visible)
            times += 16 + _cuePoint.Size.X;
        Place(_cuePoint, Outer(_elapsed.Size.X + 12 + _remaining.Size.X + 16, _cuePoint), timesY);
        _gain.Visible = times + 16 + _gain.Size.X + 12 + fxWidth <= content;
        Place(_gain, Inner(fxWidth + 12, _gain), timesY);

        Place(_overview, contentLeft, timesY + _elapsed.Size.Y + 4);

        // From the bottom: the hotcue pads across the whole width, in order, and above them the transport and the
        // loop.
        var padsY = position.Y + Height - PAD - PAD_HEIGHT;
        var x = position.X + PAD;
        foreach (var pad in _pads)
        {
            Place(pad, x, padsY);
            x += pad.Size.X + PAD_GAP;
        }

        var buttonsY = padsY - ROW_GAP - _cue.Size.Y;
        Place(_cue, Outer(0, _cue), buttonsY);
        Place(_playPause, Outer(_cue.Size.X + 6, _playPause), buttonsY);
        Place(_sync, Outer(_cue.Size.X + 6 + _playPause.Size.X + 6, _sync), buttonsY);
        var transport = _cue.Size.X + 6 + _playPause.Size.X + 6 + _sync.Size.X;
        if (_padMode is not null)
        {
            Place(_padMode, Outer(transport + 12, _padMode), buttonsY);
            transport += 12 + _padMode.Size.X;
        }

        // The loop's buttons keep their order, halve on the left, whichever side the group is on.
        var loopWidth = LoopWidth;
        var loopLeft = Mirrored ? contentLeft : contentRight - loopWidth;
        Place(_halveLoop, loopLeft, buttonsY);
        Place(_loop, loopLeft + _halveLoop.Size.X + 4, buttonsY);
        Place(_doubleLoop, loopLeft + _halveLoop.Size.X + 4 + _loop.Size.X + 4, buttonsY);

        // Loop in and out, IN before OUT either way, then quantise: beside the loop, on the side nearer the
        // transport; or, too narrow for that, in the row above, from the inner edge, with the loading line beside
        // them.
        var inOutRowY = _twoRows ? buttonsY - ROW_GAP - _cue.Size.Y : buttonsY;
        var inOutWidth = _loopIn.Size.X + 4 + _loopOut.Size.X;
        var inLeft = _twoRows ? (Mirrored ? contentLeft : contentRight - inOutWidth)
            : Mirrored ? loopLeft + loopWidth + 6 : loopLeft - 6 - inOutWidth;
        Place(_loopIn, inLeft, inOutRowY);
        Place(_loopOut, inLeft + _loopIn.Size.X + 4, inOutRowY);
        Place(_quantize, Mirrored ? inLeft + inOutWidth + 6 : inLeft - 6 - _quantize.Size.X, inOutRowY);
        if (_twoRows)
            Place(_loading, Outer(0, _loading), inOutRowY + (_cue.Size.Y - _loading.Size.Y) / 2);
        else
            Place(_loading, Outer(transport + 12, _loading), buttonsY + (_cue.Size.Y - _loading.Size.Y) / 2);

        Place(_tempoReadout, columnLeft + (TEMPO_COLUMN - _tempoReadout.Size.X) / 2, top - 2);
        var faderTop = top - 2 + _tempoReadout.Size.Y + 4;
        Place(_tempoFader, columnLeft + (TEMPO_COLUMN - _tempoFader.Size.X) / 2, faderTop);
        Place(_tempoRange, columnLeft, faderTop + _tempoFader.Size.Y + 6);
        Place(_keyLock, columnLeft, faderTop + _tempoFader.Size.Y + 6 + _tempoRange.Size.Y + 4);
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        // A strip of the deck's colour along the top, so which deck is which reads at a glance.
        batch.AddRect(ScreenPosition, new float2(Size.X, 2), _color);
        DrawChildren(batch);

        // A song dragged over the deck: tint it and say what letting go will do.
        if (IsDropTarget)
        {
            batch.AddRect(ScreenPosition, Size, Theme.Fade(Theme.Background, 0.75f));
            batch.AddRect(ScreenPosition, Size, Theme.Fade(_color, 0.15f));
            batch.AddBorder(ScreenPosition, Size, 2, _color);
            // Near the top, on a chip of the deck's colour, where the dragged song's label (below and right of the
            // pointer) is least likely to cover it.
            var text = $"Drop to load on deck {_deck.Name}";
            var size = _bold.Measure(text);
            var chip = size + new float2(24, 12);
            var at = ScreenPosition + new float2((Size.X - chip.X) / 2, 14);
            batch.AddRect(at, chip, _color);
            batch.AddText(_bold, text, at + new float2(12, 6), Colors.White);
        }
    }

    // Several songs dragged at once load the first.
    public override bool CanDrop(object payload, float2 point) => payload is IReadOnlyList<Song> { Count: > 0 };

    public override void Drop(object payload, float2 point)
    {
        if (payload is IReadOnlyList<Song> { Count: > 0 } songs)
            SongDropped?.Invoke(this, songs[0]);
    }

    static string Format(TimeSpan time) =>
        time < TimeSpan.Zero ? "0:00" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}
