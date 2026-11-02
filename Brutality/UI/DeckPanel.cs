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
    public const float HEIGHT = 200;
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
    readonly Button? _gridMode;
    readonly RollPads? _roll;
    readonly Button? _rollMode;
    readonly JumpPads? _jump;
    readonly Button? _jumpMode;
    readonly Button _quantize;
    readonly Button _loopIn;
    readonly Button _loopOut;
    readonly GridEditor? _grid;
    readonly Images? _images;

    /// <summary>
    /// The width of the tempo fader's column down the right-hand side, and the space before it.
    /// </summary>
    const float TEMPO_COLUMN = 48;
    const float TEMPO_GAP = 10;

    const float PAD_GAP = 4;
    const float PAD_HEIGHT = 28;
    const float ROW_GAP = 6;

    public float Width { get; set; } = 480;

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
                Thickness = 36,
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
        _tempoRange.Clicked += _ =>
        {
            var ranges = Deck.TempoRanges;
            var next = ranges[(ranges.ToList().IndexOf(_deck.TempoRange) + 1) % ranges.Count];
            _deck.TempoRange = next;
        };

        if (keys is not null)
        {
            foreach (var (button, hint) in new[] { (_cue, keys.Cue), (_playPause, keys.Play), (_sync, keys.Sync), (_halveLoop, keys.HalveLoop), (_loop, keys.Loop), (_doubleLoop, keys.DoubleLoop), (_keyLock, keys.KeyLock) })
                (button.KeyHint, button.HintFont) = (hint, small);
        }

        // GRID turns the pads into tools for fixing the beat grid by hand, and back.
        if (grid is not null)
        {
            _gridMode = Add(
                new Button(bold, "GRID") { MinSize = new float2(56, 30), FocusColor = Theme.Focus, Tooltip = "Pads become tools for fixing the beat grid by hand" }
            );
            _gridMode.Clicked += _ => grid.Active = !grid.Active;
        }

        // ROLL turns the pads into loop rolls, held for as long as each pad is, and back.
        if (roll is not null)
        {
            _rollMode = Add(
                new Button(bold, "ROLL") { MinSize = new float2(56, 30), FocusColor = Theme.Focus, Tooltip = "Pads become loop rolls, each looping while it's held" }
            );
            _rollMode.Clicked += _ => roll.Active = !roll.Active;
        }

        // JUMP turns the pads into beat jumps, back and on by a beat up to eight bars, and back.
        if (jump is not null)
        {
            _jumpMode = Add(
                new Button(bold, "JUMP") { MinSize = new float2(56, 30), FocusColor = Theme.Focus, Tooltip = "Pads jump back or on through the song by beats, staying in time" }
            );
            _jumpMode.Clicked += _ => jump.Active = !jump.Active;
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
            pad.PressEnded += _ => _roll?.Up(index);
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
        if (_gridMode is not null && _grid is not null)
        {
            _gridMode.Background = _grid.Active ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
            _gridMode.HoverBackground = _grid.Active ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
        }
        if (_rollMode is not null && _roll is not null)
        {
            _rollMode.Background = _roll.Active ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.5f) : Colors.Hex(0x2A2A35);
            _rollMode.HoverBackground = _roll.Active ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.65f) : Colors.Hex(0x3A3A48);
        }
        if (_jumpMode is not null && _jump is not null)
        {
            _jumpMode.Background = _jump.Active ? Theme.Mix(Theme.PanelRaised, Theme.Focus, 0.5f) : Colors.Hex(0x2A2A35);
            _jumpMode.HoverBackground = _jump.Active ? Theme.Mix(Theme.PanelRaised, Theme.Focus, 0.65f) : Colors.Hex(0x3A3A48);
        }
        var quantized = _deck.Quantize;
        _quantize.Background = quantized ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Colors.Hex(0x2A2A35);
        _quantize.HoverBackground = quantized ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Colors.Hex(0x3A3A48);
        _quantize.TextColor = quantized ? Colors.White : Theme.Muted;
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
            _key.Text = $"{key.Camelot}  {key.ShortName}{(_deck.KeyShift != 0 ? $"  ({_deck.KeyShift:+0;-0})" : "")}";
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
            var direction = gridMode ? GridEditor.Direction(GridEditor.Pads[i]) : jumpMode ? JumpPads.Direction(i) : 0;
            _pads[i].Icon = direction < 0 ? _images?.TriangleLeft : direction > 0 ? _images?.TriangleRight : null;
            _pads[i].IconAfter = direction > 0;
            _pads[i].Color = gridMode ? Theme.Accent : rollMode ? Theme.Loop : jumpMode ? Theme.Focus : Theme.HotcueColor(i);
            var jumpBeats = Math.Abs(JumpPads.Sizes[i]);
            _pads[i].Tooltip =
                gridMode ? GridTooltip(GridEditor.Pads[i])
                : rollMode ? $"Loops {RollPads.Label(i)} {(RollPads.Sizes[i] > 1 ? "beats" : "beat")} while held, then carries on from where the song would have got to"
                : jumpMode ? $"Jump {jumpBeats:0} {(jumpBeats == 1 ? "beat" : "beats")} {(JumpPads.Sizes[i] < 0 ? "back" : "on")}, staying in time; a loop goes with it"
                : hotcue is not null ? $"Hotcue {i + 1}: jump to it. Shift-click or right-click to clear it"
                : $"Hotcue {i + 1}: set it here";
            if (hotcue is { } at)
                flags.Add(((float)at.TotalSeconds, Theme.HotcueColor(i), (i + 1).ToString()));
        }
        _overview.Flags = flags;
        _overview.Region = _deck.Loop is { } loop ? ((float)loop.Start.TotalSeconds, (float)loop.End.TotalSeconds) : null;
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
    }

    void RefreshLoop()
    {
        // A loop set by hand says how long it is, if there's a beat grid to say it by.
        _loop.Text = !_deck.IsManualLoop ? $"LOOP {FormatBeats(_deck.LoopBeats)}"
            : _deck.LoopLengthBeats is { } beats ? $"LOOP {FormatLength(beats)}"
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

    public override float2 Measure()
    {
        // The title gets what the tempo and key on the right leave it.
        _bpm.Measure();
        _key.Measure();
        var right = MathF.Max(MathF.Max(_bpm.Size.X, _key.Size.X), 90);
        var content = Width - 2 * PAD - TEMPO_COLUMN - TEMPO_GAP;
        _title.MaxWidth = _artist.MaxWidth = MathF.Max(40, content - 40 - right - 16);
        _overview.Length = content;
        // The pads share the width between them; the loading line gets what the buttons either side leave it.
        var padWidth = MathF.Floor((Width - 2 * PAD - (_pads.Count - 1) * PAD_GAP) / _pads.Count);
        foreach (var pad in _pads)
            pad.FixedSize = new float2(padWidth, PAD_HEIGHT);
        _loading.MaxWidth = MathF.Max(0, content - 64 - 6 - 84 - 6 - 64 - 24 - (30 + 6 + 34 + 4 + 34 + 6 + 26 + 4 + 84 + 4 + 26) - 12);

        // The fader fills its column between the readout above and the range button below, down to the transport.
        _tempoReadout.Measure();
        _tempoRange.Measure();
        var columnHeight = HEIGHT - 2 * PAD - PAD_HEIGHT - ROW_GAP;
        _keyLock.Measure();
        _tempoFader.Length = columnHeight - _tempoReadout.Size.Y - 4 - _tempoRange.Size.Y - 4 - _keyLock.Size.Y - 6;

        foreach (var child in Children)
            child.Measure();
        return Size = new float2(Width, HEIGHT);
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

        var timesY = top + 52;
        Place(_elapsed, Outer(0, _elapsed), timesY);
        Place(_remaining, Outer(_elapsed.Size.X + 12, _remaining), timesY);
        Place(_cuePoint, Outer(_elapsed.Size.X + 12 + _remaining.Size.X + 16, _cuePoint), timesY);
        Place(_gain, Inner(0, _gain), timesY);

        Place(_overview, contentLeft, timesY + _elapsed.Size.Y + 4);

        // From the bottom: the hotcue pads across the whole width, in order, and above them the transport and the
        // loop.
        var padsY = position.Y + HEIGHT - PAD - PAD_HEIGHT;
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
        if (_gridMode is not null)
        {
            Place(_gridMode, Outer(transport + 12, _gridMode), buttonsY);
            transport += 12 + _gridMode.Size.X;
        }
        if (_rollMode is not null)
        {
            Place(_rollMode, Outer(transport + 6, _rollMode), buttonsY);
            transport += 6 + _rollMode.Size.X;
        }
        if (_jumpMode is not null)
        {
            Place(_jumpMode, Outer(transport + 6, _jumpMode), buttonsY);
            transport += 6 + _jumpMode.Size.X;
        }
        Place(_loading, Outer(transport + 12, _loading), buttonsY + (_cue.Size.Y - _loading.Size.Y) / 2);

        // The loop's buttons keep their order, halve on the left, whichever side the group is on.
        var loopWidth = _halveLoop.Size.X + 4 + _loop.Size.X + 4 + _doubleLoop.Size.X;
        var loopLeft = Mirrored ? contentLeft : contentRight - loopWidth;
        Place(_halveLoop, loopLeft, buttonsY);
        Place(_loop, loopLeft + _halveLoop.Size.X + 4, buttonsY);
        Place(_doubleLoop, loopLeft + _halveLoop.Size.X + 4 + _loop.Size.X + 4, buttonsY);
        // Beside the loop, on the side nearer the transport: loop in and out, IN before OUT either way, then quantise.
        var inOutWidth = _loopIn.Size.X + 4 + _loopOut.Size.X;
        var inLeft = Mirrored ? loopLeft + loopWidth + 6 : loopLeft - 6 - inOutWidth;
        Place(_loopIn, inLeft, buttonsY);
        Place(_loopOut, inLeft + _loopIn.Size.X + 4, buttonsY);
        Place(_quantize, Mirrored ? inLeft + inOutWidth + 6 : inLeft - 6 - _quantize.Size.X, buttonsY);

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
