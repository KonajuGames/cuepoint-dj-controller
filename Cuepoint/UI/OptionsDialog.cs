using Brutal.Numerics;
using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.UI;

/// <summary>
/// The options, over the screen dimmed behind them: automatic gain, how much of the song the scrolling waveforms
/// show, where the library and its analysis live, and a way to the keyboard shortcuts. Clicking outside it, Close, or Escape
/// closes it. Sized to the screen by whoever lays it out, with <see cref="ScreenSize"/>; on a screen too short for it
/// all, it scrolls.
/// </summary>
sealed class OptionsDialog : Panel
{
    public const float MIN_WAVEFORM_SECONDS = 4;
    public const float MAX_WAVEFORM_SECONDS = 30;

    /// <summary>
    /// The least room left between the box and the window's edges.
    /// </summary>
    const float MARGIN = 16;

    readonly AudioEngine _engine;
    readonly ScrollPanel _box;
    readonly Panel _content;

    /// <summary>
    /// What had focus at the last layout, so the box scrolls to a control only as focus moves to it, and doesn't fight
    /// the wheel after.
    /// </summary>
    Element? _lastFocused;

    readonly Panel _tabStrip;
    readonly Font _tabFont;
    readonly List<(Button Tab, Panel Page)> _pages = [];
    Panel _page = null!;
    int _tab;

    /// <summary>
    /// The names of the tabs, in order.
    /// </summary>
    public IReadOnlyList<string> Tabs => _pages.Select(page => page.Tab.Text).ToList();

    /// <summary>
    /// Which tab's showing, by its place; kept while the options are closed, so they open where they were left.
    /// </summary>
    public int Tab
    {
        get => _tab;
        set
        {
            _tab = Math.Clamp(value, 0, _pages.Count - 1);
            for (var i = 0; i < _pages.Count; i++)
            {
                _pages[i].Tab.Lit = i == _tab;
                _pages[i].Page.Visible = i == _tab;
            }
        }
    }

    /// <summary>
    /// The tab a setting's on, by the name on its row; -1 if there's none by that name.
    /// </summary>
    public int TabOf(string setting) =>
        _pages.FindIndex(page => page.Page.Children.OfType<Panel>().Any(row => row.Children.FirstOrDefault() is Panel { Children: [Label { Text: var name }, ..] } && name == setting));
    readonly Checkbox _autoGain;
    readonly Checkbox _limiter;
    readonly Dropdown<CrossfaderCurve> _crossfaderCurve;

    /// <summary>
    /// Whether a song can't be loaded onto a deck that's playing into the main mix.
    /// </summary>
    public Checkbox ProtectPlayingDecks { get; }

    /// <summary>
    /// Whether songs not analysed yet are analysed while nothing's playing.
    /// </summary>
    public Checkbox AnalyseWhenIdle { get; }

    /// <summary>
    /// Another tempo range was chosen: the slowest tempo of the octave songs are shown in.
    /// </summary>
    public event Action<double>? TempoRangeChanged;

    readonly Dropdown<double> _tempoRange;

    public Checkbox AutoCue { get; }

    public float2 ScreenSize { get; set; }

    /// <summary>
    /// How many seconds of song the scrolling waveforms show across, as set here.
    /// </summary>
    public Slider WaveformSeconds { get; }

    /// <summary>
    /// How the scrolling waveforms are coloured, whether they show the beat grid, and whether they're tall, as chosen
    /// here; any change is raised as <see cref="WaveformsChanged"/>.
    /// </summary>
    public Dropdown<WaveformColours> WaveformColours { get; }

    public Checkbox WaveformBeatGrid { get; }
    public Checkbox TallWaveforms { get; }

    public event Action? WaveformsChanged;

    readonly Label _waveformSeconds;

    public event Action? Closed;

    /// <summary>
    /// Show the shortcuts was clicked.
    /// </summary>
    public event Action? ShortcutsClicked;

    /// <summary>
    /// Another look was chosen, to take on the next time the app starts.
    /// </summary>
    public event Action<ThemeKind>? LookChosen;

    /// <summary>
    /// Another screen size was chosen: a share of the designed size, or null for Windows' own for the screen.
    /// </summary>
    public event Action<float?>? ScreenSizeChosen;

    readonly Dropdown<float?> _screenSize;

    /// <summary>
    /// The screen size chosen, to show it.
    /// </summary>
    public float? ChosenScreenSize
    {
        get => _screenSize.Selected;
        set
        {
            _screenSize.Selected = value;
            ShowRestartNote();
        }
    }

    /// <summary>
    /// Windows' own scale for the screen, to say what "As Windows has it" is.
    /// </summary>
    public float WindowsScale { get; set; } = 1;

    /// <summary>
    /// The scale the screen's drawn at now, taken on as the app started: a size chosen that comes out different
    /// says Cuepoint needs starting again to show it.
    /// </summary>
    public float RunningScale { get; set; } = 1;

    /// <summary>
    /// The biggest scale the screen has room for, the whole app still fitting on it.
    /// </summary>
    public float LargestScale { get; set; } = float.MaxValue;

    /// <summary>
    /// The scale a screen size chosen is drawn at: as chosen, unless the screen hasn't room for it.
    /// </summary>
    public float Drawn(float? chosen) => ScreenScale.Use(chosen, WindowsScale, LargestScale);

    readonly Label _restartNote;

    void ShowRestartNote() => _restartNote.Visible = Drawn(_screenSize.Selected) != RunningScale;

    /// <summary>
    /// Change keys was clicked.
    /// </summary>
    public event Action? ChangeKeysClicked;

    /// <summary>
    /// Back up or Restore was clicked, for the settings.
    /// </summary>
    public event Action? BackUpClicked;

    public event Action? RestoreClicked;

    /// <summary>
    /// The headphones were moved to another device, or turned off; the new device's name, or null.
    /// </summary>
    public event Action<string?>? HeadphonesChanged;

    /// <summary>
    /// Another microphone was chosen, or none.
    /// </summary>
    public event Action<string?>? MicrophoneChanged;

    /// <summary>
    /// The main output was moved to another device (null for the system's default).
    /// </summary>
    public event Action<string?>? OutputChanged;

    readonly Dropdown<string?> _output;
    readonly Dropdown<int?> _bufferSize;
    readonly Label _latency;

    /// <summary>
    /// The output's buffer size was changed here.
    /// </summary>
    public event Action? BufferSizeChanged;

    readonly Dropdown<string?> _headphones;
    readonly Dropdown<string?> _booth;
    readonly Dropdown<string?> _microphone;
    readonly Slider _boothVolume;

    /// <summary>
    /// The booth output was moved to another device, or turned off; the one chosen, or null.
    /// </summary>
    public event Action<string?>? BoothChanged;
    readonly Label _controller;
    readonly Checkbox _lighting;
    readonly Checkbox _largeListText;

    ContextMenu? _menu;

    /// <summary>
    /// Where the dialog's dropdowns open their lists, over everything: every one of them, so none is left unable to.
    /// </summary>
    public ContextMenu? Menu
    {
        get => _menu;
        set
        {
            _menu = value;
            foreach (var dropdown in Within(_content).OfType<IOpensMenu>())
                dropdown.Menu = value;
        }
    }

    /// <summary>
    /// <paramref name="element"/> and everything in it, however deep.
    /// </summary>
    static IEnumerable<Element> Within(Element element) =>
        element is Panel panel ? panel.Children.SelectMany(Within).Prepend(element) : [element];
    readonly Label _lightingStatus;
    readonly Label _musicFolder;
    readonly Label _recordingsFolder;

    /// <summary>
    /// Whether the folder list and song list are in large text, to show.
    /// </summary>
    public Func<bool>? LargeListText { get; set; }

    /// <summary>
    /// The large text on/off button was clicked.
    /// </summary>
    public event Action? LargeListTextToggled;

    /// <summary>
    /// Whether lighting software is followed by OS2L, and what's connected, to show.
    /// </summary>
    public Func<bool>? LightingEnabled { get; set; }
    public Func<string>? LightingStatus { get; set; }

    /// <summary>
    /// The lighting on/off button was clicked.
    /// </summary>
    public event Action? LightingToggled;

    /// <summary>
    /// The master limiter was turned on or off here.
    /// </summary>
    public event Action? MasterLimiterToggled;

    /// <summary>
    /// The crossfader's curve was changed here.
    /// </summary>
    public event Action? CrossfaderCurveChanged;

    /// <summary>
    /// A lighting address was entered (empty for none).
    /// </summary>
    public event Action<string>? LightingAddressChanged;

    /// <summary>
    /// Where lighting software is reached directly, as host:port.
    /// </summary>
    public TextBox LightingAddress { get; }

    /// <summary>
    /// What's said about connected controllers; none if null.
    /// </summary>
    public Func<string?>? ControllerStatus { get; set; }

    /// <summary>
    /// The MIDI outputs clock could go out of, and the one it does; none if null, as off Windows.
    /// </summary>
    public Func<IReadOnlyList<string>>? MidiClockOutputs { get; set; }

    public Func<string?>? MidiClockOutput { get; set; }

    /// <summary>
    /// Another output, or none (null), was chosen for MIDI clock.
    /// </summary>
    public event Action<string?>? MidiClockChanged;

    readonly Dropdown<string?> _midiClock;

    /// <summary>
    /// Map controls was clicked, to open the controller's mapping.
    /// </summary>
    public event Action? MapControllerClicked;

    /// <summary>
    /// The folder the music library is made from, as shown.
    /// </summary>
    public string MusicFolder
    {
        get => _musicFolder.Text;
        set => _musicFolder.Text = value;
    }

    /// <summary>
    /// The folder recordings of the mix go in, as shown.
    /// </summary>
    public string RecordingsFolder
    {
        get => _recordingsFolder.Text;
        set => _recordingsFolder.Text = value;
    }

    public OptionsDialog(AudioEngine engine, string musicFolder, string databasePath, Font body, Font bold, Font heading)
    {
        _engine = engine;
        Visible = false;
        // Blending is in linear light, where black at 55% only takes off about a third, so it takes more to dim.
        Background = Theme.Scrim(0.75f);

        _box = Add(
            new ScrollPanel
            {
                Padding = 20,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(520, 0),
            }
        );
        _content = _box.Add(new Panel { Spacing = 14 });
        _content.Add(new Label(heading, "Options") { Color = Theme.Text });
        _tabStrip = _content.Add(new Panel { Direction = Direction.Horizontal, Spacing = 2 });
        _tabFont = bold;

        // Each page of settings behind a tab, so what goes together is together: what plays, the sound cards, what's
        // shown, what controls it from outside, and where things are kept.
        void Page(string title)
        {
            var page = _content.Add(new Panel { Spacing = 14, Visible = _pages.Count == 0 });
            var index = _pages.Count;
            var tab = _tabStrip.Add(
                new Button(_tabFont, title)
                {
                    Padding = 7,
                    Background = Theme.Control,
                    HoverBackground = Theme.ControlHover,
                    LitBackground = Theme.Accent,
                    FocusColor = Theme.Focus,
                    Lit = index == 0,
                }
            );
            tab.Clicked += _ => Tab = index;
            _pages.Add((tab, page));
            _page = page;
        }

        Page("Mixing");
        var gainRow = Row("Automatic gain", body);
        _autoGain = gainRow.Add(new Checkbox(body));
        _autoGain.Changed += box => engine.AutoGain = box.Checked;
        _page.Add(new Label(body, "Plays every song at about the same loudness, from its analysis.") { Color = Theme.Muted });

        var protectRow = Row("Protect playing decks", body);
        ProtectPlayingDecks = protectRow.Add(
            new Checkbox(body, "Don't load over a deck being heard")
            {
                Checked = true,
                Tooltip = "A song can't be loaded onto a deck that's playing into the main mix. A deck with its fader down, or only in the headphones, can still be loaded",
            }
        );

        var autoCueRow = Row("Auto cue", body);
        AutoCue = autoCueRow.Add(
            new Checkbox(body, "Cue new songs where the music starts")
            {
                Checked = true,
                Tooltip = "A song without a cue point of its own loads cued to its first sound, on the nearest beat, rather than to the silence before it. A cue point set by hand, or from Rekordbox, comes first",
            }
        );

        var tempoRow = Row("Tempo range", body);
        _tempoRange = tempoRow.Add(
            new Dropdown<double>(body)
            {
                Width = 160,
                Items = () => TempoRange.Choices,
                Text = TempoRange.Name,
                Selected = TempoRange.Min,
                Tooltip = "Songs' tempos are doubled or halved into this range, beat grid and all: 88–176 shows drum & bass at 174 rather than 87, 70–140 shows hip-hop at 90 rather than 180. A grid fixed by hand, or from other DJ software, is left as it is",
            }
        );
        _tempoRange.Chosen += min => TempoRangeChanged?.Invoke(min);
        _page.Add(new Label(body, "The octave analysed tempos are shown in; nothing's analysed again.") { Color = Theme.Muted });

        var limiterRow = Row("Master limiter", body);
        _limiter = limiterRow.Add(new Checkbox(body));
        _limiter.Changed += box =>
        {
            engine.Limiter.Enabled = box.Checked;
            MasterLimiterToggled?.Invoke();
        };
        _page.Add(
            new Label(body, "Keeps the mix from clipping when the decks are loud together, turning it down smoothly just before a peak.")
            {
                Color = Theme.Muted,
            }
        );

        var curveRow = Row("Crossfader curve", body);
        _crossfaderCurve = curveRow.Add(
            new Dropdown<CrossfaderCurve>(body)
            {
                Width = 300,
                Items = () => [CrossfaderCurve.Smooth, CrossfaderCurve.Sharp],
                Text = curve => curve == CrossfaderCurve.Sharp ? "Sharp cut, for scratching" : "Smooth blend, for mixing",
                Selected = engine.CrossfaderCurve,
            }
        );
        _crossfaderCurve.Chosen += curve =>
        {
            engine.CrossfaderCurve = curve;
            CrossfaderCurveChanged?.Invoke();
        };
        _page.Add(
            new Label(body, "Smooth fades one deck into the other across the whole fader. Sharp keeps both up until the very end, then cuts.")
            {
                Color = Theme.Muted,
            }
        );

        // Every playback device there is now, to choose the main mix's from; the system's default first.
        Page("Sound");
        var outputRow = Row("Main output", body);
        _output = outputRow.Add(
            new Dropdown<string?>(body)
            {
                Width = 300,
                Items = () => [null, .. AudioEngine.PlaybackDevices().Select(device => device.Name)],
                Text = name => name ?? "System default",
            }
        );
        _output.Chosen += name =>
        {
            engine.UseOutputDevice(name);
            OutputChanged?.Invoke(engine.OutputDevice);
        };

        // And the headphones', or none. The main output's own device puts them on its channels 3 and 4, if it has
        // them, as a DJ controller's sound card does.
        var phonesRow = Row("Headphones", body);
        _headphones = phonesRow.Add(
            new Dropdown<string?>(body)
            {
                Width = 300,
                Items = () => [null, .. AudioEngine.PlaybackDevices().Select(device => device.Name)],
                Text = name => name is null ? "Off" : name == engine.OutputDevice ? $"{name} (channels 3-4)" : name,
            }
        );
        _headphones.Chosen += name =>
        {
            engine.UseHeadphoneDevice(name);
            HeadphonesChanged?.Invoke(engine.HeadphoneDevice);
        };
        _page.Add(
            new Label(body, "A second sound card, or a controller's own channels 3 and 4, to hear decks with PFL on before the room does.")
            {
                Color = Theme.Muted,
            }
        );

        // The microphone: a recording device, mixed into the main mix from the title bar's MIC.
        var micRow = Row("Microphone", body);
        _microphone = micRow.Add(
            new Dropdown<string?>(body)
            {
                Width = 300,
                Items = () => [null, .. AudioEngine.RecordingDevices().Select(device => device.Name)],
                Text = name => name ?? "None",
            }
        );
        _microphone.Chosen += name =>
        {
            engine.UseMicrophoneDevice(name);
            MicrophoneChanged?.Invoke(engine.ChosenMicrophoneDevice);
        };
        _page.Add(
            new Label(body, "A microphone or a sound card's input, to speak over the music. MIC on the title bar puts it in the mix.")
            {
                Color = Theme.Muted,
            }
        );

        // And the booth's: the main mix again on another sound card, for monitors in the DJ booth, at its own volume.
        var boothRow = Row("Booth output", body);
        _booth = boothRow.Add(
            new Dropdown<string?>(body)
            {
                Width = 300,
                Items = () => [null, .. AudioEngine.PlaybackDevices().Select(device => device.Name).Where(name => name != engine.OutputDevice)],
                Text = name => name ?? "Off",
            }
        );
        _booth.Chosen += name =>
        {
            engine.UseBoothDevice(name);
            BoothChanged?.Invoke(engine.ChosenBoothDevice);
        };
        var boothVolumeRow = Row("Booth volume", body);
        _boothVolume = boothVolumeRow.Add(
            new Slider
            {
                Length = 300,
                Thickness = 18,
                HandleLength = 10,
                DefaultValue = 0.8f,
                Value = engine.BoothVolume,
                ShowFill = true,
                FocusColor = Theme.Focus,
                Tooltip = "How loud the booth monitors are, whatever the master volume. Double-click for the usual level",
            }
        );
        _boothVolume.Changed += slider => engine.BoothVolume = slider.Value;
        _page.Add(
            new Label(body, "The main mix again, before the master volume, on a sound card of its own for monitors in the DJ booth.")
            {
                Color = Theme.Muted,
            }
        );

        var bufferRow = Row("Buffer size", body);
        _bufferSize = bufferRow.Add(
            new Dropdown<int?>(body)
            {
                Width = 300,
                Items = () => [null, .. AudioEngine.BufferSizes.Select(frames => (int?)frames)],
                Text = frames =>
                    frames is { } n ? $"{n} samples ({AudioOutput.Milliseconds(n, AudioOutput.RATE):0.#} ms)" : "The driver's own",
                Selected = engine.BufferFrames,
            }
        );
        _bufferSize.Chosen += frames =>
        {
            engine.UseBufferSize(frames);
            BufferSizeChanged?.Invoke();
        };
        _latency = Row("Latency", body).Add(new Label(body) { Color = Theme.Text, MaxWidth = 380 });
        _page.Add(
            new Label(body, "Smaller feels tighter under your hands when scratching; larger stops crackles on a busy laptop. A driver that keeps its own size gets the rest mixed ahead, which can make it larger but not smaller.")
            {
                Color = Theme.Muted,
            }
        );

        Page("Screen");
        var zoomRow = Row("Waveform shows", body);
        WaveformSeconds = zoomRow.Add(
            new Slider
            {
                Min = MIN_WAVEFORM_SECONDS,
                Max = MAX_WAVEFORM_SECONDS,
                Value = 12,
                DefaultValue = 12,
                Length = 220,
                FillColor = Theme.Accent,
                ShowFill = true,
                FocusColor = Theme.Focus,
            }
        );
        _waveformSeconds = zoomRow.Add(new Label(body) { Color = Theme.Text });
        _page.Add(new Label(body, "The mouse wheel over the waveforms zooms them too.") { Color = Theme.Muted });

        var coloursRow = Row("Waveform colours", body);
        WaveformColours = coloursRow.Add(
            new Dropdown<WaveformColours>(body)
            {
                Width = 300,
                Items = () => [UI.WaveformColours.Deck, UI.WaveformColours.ThreeBand],
                Text = colours =>
                    colours == UI.WaveformColours.ThreeBand ? "Three bands: bass red, mids green, treble blue" : "Deck colour, brighter for treble",
                Selected = UI.WaveformColours.Deck,
            }
        );
        WaveformColours.Chosen += _ => WaveformsChanged?.Invoke();

        var gridRow = Row("Beat grid", body);
        WaveformBeatGrid = gridRow.Add(new Checkbox(body, "On the scrolling waveforms") { Checked = true });
        WaveformBeatGrid.Changed += _ => WaveformsChanged?.Invoke();

        var tallRow = Row("Tall waveforms", body);
        TallWaveforms = tallRow.Add(new Checkbox(body));
        TallWaveforms.Changed += _ => WaveformsChanged?.Invoke();
        _page.Add(new Label(body, "Taller scrolling waveforms, to read from further away.") { Color = Theme.Muted });

        // The look, which the screen takes on as it's built, so a new one shows the next time the app starts.
        var lookRow = Row("Look", body);
        var look = lookRow.Add(
            new Dropdown<ThemeKind>(body)
            {
                Width = 300,
                Items = () => [ThemeKind.Dark, ThemeKind.Light, ThemeKind.HighContrast],
                Text = Theme.NameOf,
                Selected = Theme.Kind,
            }
        );
        look.Chosen += kind => LookChosen?.Invoke(kind);
        _page.Add(
            new Label(body, "Dark for dark rooms, Light for daylight, High contrast for bright rooms. It changes the next time Cuepoint starts.")
            {
                Color = Theme.Muted,
            }
        );

        // How big everything's drawn, also taken on as the app starts.
        var screenRow = Row("Screen size", body);
        _screenSize = screenRow.Add(
            new Dropdown<float?>(body)
            {
                Width = 300,
                Items = () => [null, .. ScreenScale.Choices.Select(choice => (float?)choice)],
                Text = scale => ScreenScale.Name(scale, WindowsScale, LargestScale),
            }
        );
        _restartNote = screenRow.Add(new Label(body, "Restart Cuepoint to see it") { Color = Theme.Warning, Visible = false });
        _screenSize.Chosen += scale =>
        {
            ShowRestartNote();
            ScreenSizeChosen?.Invoke(scale);
        };
        _page.Add(
            new Label(body, "Everything drawn bigger, for a 4K screen or one seen from further away. It changes the next time Cuepoint starts.")
            {
                Color = Theme.Muted,
            }
        );

        var textRow = Row("Large list text", body);
        _largeListText = textRow.Add(new Checkbox(body));
        _largeListText.Changed += _ => LargeListTextToggled?.Invoke();
        _page.Add(new Label(body, "Bigger text in the folder list and song list, to read in the dark or without glasses.") { Color = Theme.Muted });

        Page("Controls");
        var keysRow = Row("Keyboard", body);
        var showKeys = keysRow.Add(new Button(body, "Show the shortcuts") { FocusColor = Theme.Focus });
        showKeys.Clicked += _ => ShortcutsClicked?.Invoke();
        var changeKeys = keysRow.Add(new Button(body, "Change keys...") { FocusColor = Theme.Focus });
        changeKeys.Clicked += _ => ChangeKeysClicked?.Invoke();
        keysRow.Add(new Label(body, "or press ? at any time") { Color = Theme.Muted });

        var controllerRow = Row("Controller", body);
        _controller = controllerRow.Add(new Label(body) { Color = Theme.Text, MaxWidth = 200 });
        var map = controllerRow.Add(new Button(bold, "Map controls") { FocusColor = Theme.Focus });
        map.Clicked += _ => MapControllerClicked?.Invoke();

        var clockRow = Row("MIDI clock out", body);
        _midiClock = clockRow.Add(
            new Dropdown<string?>(body)
            {
                Width = 300,
                Items = () => [null, .. MidiClockOutputs?.Invoke() ?? []],
                Text = name => name ?? "None",
            }
        );
        _midiClock.Chosen += name => MidiClockChanged?.Invoke(name);
        _page.Add(
            new Label(body, "The tempo of the deck leading the mix, for drum machines, effects units and lighting desks: started on its next downbeat.")
            {
                Color = Theme.Muted,
            }
        );

        var lightingRow = Row("Lighting (OS2L)", body);
        _lighting = lightingRow.Add(new Checkbox(body));
        _lighting.Changed += _ => LightingToggled?.Invoke();
        _lightingStatus = lightingRow.Add(new Label(body) { Color = Theme.Text, MaxWidth = 280 });
        var addressRow = Row("Lighting address", body);
        LightingAddress = addressRow.Add(new TextBox(body) { Width = 220, Placeholder = "Found by itself; or host:port", FocusColor = Theme.Focus });
        LightingAddress.Submitted += box => LightingAddressChanged?.Invoke(box.Text.Trim());
        _page.Add(
            new Label(body, "Sends beats and each deck's play state to lighting software such as SoundSwitch, QLC+ or sLight.") { Color = Theme.Muted }
        );

        Page("Library");
        var idleRow = Row("Analyse when idle", body);
        AnalyseWhenIdle = idleRow.Add(
            new Checkbox(body, "While nothing's playing")
            {
                Checked = true,
                Tooltip = "Songs in the list you're looking at, then the music library, are analysed one at a time while no deck is playing, so finding them by tempo or key works without scrolling to them first",
            }
        );

        _musicFolder = Row("Music library", body).Add(new Label(body, musicFolder) { Color = Theme.Text, MaxWidth = 340 });
        _page.Add(new Label(body, "Right-click a folder in the folder list to make it the music library.") { Color = Theme.Muted });
        Row("Analysis saved in", body).Add(new Label(body, databasePath) { Color = Theme.Text, MaxWidth = 340 });
        _recordingsFolder = Row("Recordings saved in", body).Add(new Label(body) { Color = Theme.Text, MaxWidth = 340 });

        var backupRow = Row("Settings", body);
        var backUp = backupRow.Add(new Button(body, "Back up...") { FocusColor = Theme.Focus });
        backUp.Clicked += _ => BackUpClicked?.Invoke();
        var restore = backupRow.Add(new Button(body, "Restore...") { FocusColor = Theme.Focus });
        restore.Clicked += _ => RestoreClicked?.Invoke();
        _page.Add(
            new Label(body, "Everything set up here, the keys and controller mappings and virtual folders too, in one file.")
            {
                Color = Theme.Muted,
            }
        );

        // What this is and which build: to say when reporting something, or to check an update took.
        Page("About");
        _page.Add(new Label(bold, AppInfo.Title) { Color = Theme.Text });
        if (AppInfo.Description.Length > 0)
            _page.Add(new Label(body, AppInfo.Description) { Color = Theme.Muted, MaxWidth = 480, Wrap = true });
        Row("Version", body).Add(new Label(body, AppInfo.ShortVersion) { Color = Theme.Text });
        if (AppInfo.Commit.Length > 0)
            Row("Built from", body).Add(new Label(body, $"commit {AppInfo.Commit}") { Color = Theme.Text });
        Row("Runs on", body).Add(new Label(body, AppInfo.Runtime) { Color = Theme.Text, MaxWidth = 340 });
        if (AppInfo.Company.Length > 0)
            Row("Published by", body).Add(new Label(body, AppInfo.Company) { Color = Theme.Text });
        if (AppInfo.License.Length > 0)
            Row("Licence", body).Add(new Label(body, AppInfo.License) { Color = Theme.Text });
        if (AppInfo.RepositoryUrl.Length > 0)
        {
            var websiteRow = Row("Website", body);
            // Without its https://, which it doesn't need to be read and which would push it past the box.
            websiteRow.Add(new Label(body, AppInfo.RepositoryUrl.Replace("https://", "")) { Color = Theme.Text, MaxWidth = 340 });
            var open = websiteRow.Add(new Button(body, "Open") { FocusColor = Theme.Focus, Tooltip = "Opens the website in your browser" });
            open.Clicked += _ => OpenWebsite();
        }
        if (AppInfo.Copyright.Length > 0)
            _page.Add(new Label(body, AppInfo.Copyright) { Color = Theme.Muted });

        var close = _content.Add(new Button(bold, "Close") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        close.Clicked += _ => Close();

        // A click on the dimmed screen around it closes it too; one on the box's own empty space doesn't.
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    bool _pressedInBox;

    // In the browser Windows opens links with; a failure to start one isn't worth more than nothing happening.
    static void OpenWebsite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    /// <summary>
    /// A row with <paramref name="name"/> in a column wide enough that every row's controls line up.
    /// </summary>
    Panel Row(string name, Font font)
    {
        var row = _page.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
        row.Add(new Panel { MinSize = new float2(140, 0) }).Add(new Label(font, name) { Color = Theme.Muted });
        return row;
    }

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public void Open() => Visible = true;

    // Escape puts it away while one of its own controls has focus too, such as a tab just clicked.
    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        Close();
        return true;
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        Closed?.Invoke();
    }

    public override float2 Measure()
    {
        _autoGain.Checked = _engine.AutoGain;
        // The mouse wheel can change it too, so it's said afresh each time.
        _waveformSeconds.Text = $"{WaveformSeconds.Value:F0} seconds";
        _limiter.Checked = _engine.Limiter.Enabled;
        _crossfaderCurve.Selected = _engine.CrossfaderCurve;
        _output.Selected = _engine.OutputDevice;
        _headphones.Selected = _engine.HeadphoneDevice;
        _booth.Selected = _engine.ChosenBoothDevice;
        _microphone.Selected = _engine.ChosenMicrophoneDevice;
        _tempoRange.Selected = TempoRange.Min;
        if (!_boothVolume.IsDragging)
            _boothVolume.Value = _engine.BoothVolume;
        _bufferSize.Selected = _engine.BufferFrames;
        _latency.Text = LatencyText(_engine.OutputBuffer, _engine.OutputMixAhead);
        _controller.Text = ControllerStatus?.Invoke() ?? "None connected";
        _midiClock.Selected = MidiClockOutput?.Invoke();
        var lighting = LightingEnabled?.Invoke() ?? false;
        _lighting.Checked = lighting;
        _lightingStatus.Text = lighting ? LightingStatus?.Invoke() ?? "" : "";
        _largeListText.Checked = LargeListText?.Invoke() ?? false;

        // Never past the window's edges, scrolling instead.
        _box.MaxWidth = MathF.Max(0, ScreenSize.X - 2 * MARGIN);
        _box.MaxHeight = MathF.Max(0, ScreenSize.Y - 2 * MARGIN);
        // The box keeps one size whichever tab's showing, that of the largest page, so it doesn't jump about.
        var largest = new float2(0, 0);
        foreach (var (_, page) in _pages)
        {
            page.MinSize = new float2(0, 0);
            var size = page.Measure();
            largest = new float2(MathF.Max(largest.X, size.X), MathF.Max(largest.Y, size.Y));
        }
        foreach (var (_, page) in _pages)
            page.MinSize = largest;
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 2)));

        // A control tabbed to out of view is scrolled to, with a little of what's around it.
        var focused = FocusedIn(_content);
        if (focused is not null && focused != _lastFocused)
        {
            var top = focused.ScreenPosition.Y - _content.ScreenPosition.Y;
            _box.ScrollToShow(top - MARGIN, top + focused.Size.Y + MARGIN);
        }
        _lastFocused = focused;
    }

    /// <summary>
    /// What the output's buffer comes to, as said under the buffer size: the device's own, and what's mixed ahead of it
    /// (in frames at <see cref="AudioOutput.RATE"/>) to make up the size asked for.
    /// </summary>
    internal static string LatencyText((int Frames, int Rate)? buffer, int mixAheadFrames = 0)
    {
        if (buffer is not var (frames, rate))
            return "No output open";
        var device = AudioOutput.Milliseconds(frames, rate);
        var deviceText = $"{frames} samples at {rate / 1000.0:0.#} kHz";
        if (mixAheadFrames <= 0)
            return $"{device:0.0} ms a buffer ({deviceText})";
        var ahead = AudioOutput.Milliseconds(mixAheadFrames, AudioOutput.RATE);
        return $"{device + ahead:0.0} ms a buffer (the device's {device:0.0} ms, {deviceText}, and {ahead:0.0} ms mixed ahead)";
    }

    static Element? FocusedIn(Element element)
    {
        if (element.IsFocused)
            return element;
        foreach (var child in element.VisibleChildren)
        {
            if (FocusedIn(child) is { } focused)
                return focused;
        }
        return null;
    }
}
