using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// The options, over the screen dimmed behind them: automatic gain, how much of the song the scrolling waveforms
/// show, where the library and its analysis live, and the keyboard shortcuts. Clicking outside it, Close, or Escape
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
    readonly Checkbox _autoGain;
    readonly Checkbox _limiter;

    /// <summary>
    /// Whether a song can't be loaded onto a deck that's playing into the main mix.
    /// </summary>
    public Checkbox ProtectPlayingDecks { get; }

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
    /// The headphones were moved to another device, or turned off; the new device's name, or null.
    /// </summary>
    public event Action<string?>? HeadphonesChanged;

    /// <summary>
    /// The main output was moved to another device (null for the system's default).
    /// </summary>
    public event Action<string?>? OutputChanged;

    readonly Dropdown<string?> _output;

    readonly Dropdown<string?> _headphones;
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
        Background = Colors.Hex(0x000000, 0.75f);

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

        var gainRow = Row("Automatic gain", body);
        _autoGain = gainRow.Add(new Checkbox(body));
        _autoGain.Changed += box => engine.AutoGain = box.Checked;
        _content.Add(new Label(body, "Plays every song at about the same loudness, from its analysis.") { Color = Theme.Muted });

        var protectRow = Row("Protect playing decks", body);
        ProtectPlayingDecks = protectRow.Add(
            new Checkbox(body, "Don't load over a deck being heard")
            {
                Checked = true,
                Tooltip = "A song can't be loaded onto a deck that's playing into the main mix. A deck with its fader down, or only in the headphones, can still be loaded",
            }
        );

        var limiterRow = Row("Master limiter", body);
        _limiter = limiterRow.Add(new Checkbox(body));
        _limiter.Changed += box =>
        {
            engine.Limiter.Enabled = box.Checked;
            MasterLimiterToggled?.Invoke();
        };
        _content.Add(
            new Label(body, "Keeps the mix from clipping when the decks are loud together, turning it down smoothly just before a peak.")
            {
                Color = Theme.Muted,
            }
        );

        // Every playback device there is now, to choose the main mix's from; the system's default first.
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
        _content.Add(
            new Label(body, "A second sound card, or a controller's own channels 3 and 4, to hear decks with PFL on before the room does.")
            {
                Color = Theme.Muted,
            }
        );

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
        _content.Add(new Label(body, "The mouse wheel over the waveforms zooms them too.") { Color = Theme.Muted });

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
        _content.Add(new Label(body, "Taller scrolling waveforms, to read from further away.") { Color = Theme.Muted });

        var textRow = Row("Large list text", body);
        _largeListText = textRow.Add(new Checkbox(body));
        _largeListText.Changed += _ => LargeListTextToggled?.Invoke();
        _content.Add(new Label(body, "Bigger text in the folder list and song list, to read in the dark or without glasses.") { Color = Theme.Muted });

        var controllerRow = Row("Controller", body);
        _controller = controllerRow.Add(new Label(body) { Color = Theme.Text, MaxWidth = 200 });
        var map = controllerRow.Add(new Button(bold, "Map controls") { FocusColor = Theme.Focus });
        map.Clicked += _ => MapControllerClicked?.Invoke();

        var lightingRow = Row("Lighting (OS2L)", body);
        _lighting = lightingRow.Add(new Checkbox(body));
        _lighting.Changed += _ => LightingToggled?.Invoke();
        _lightingStatus = lightingRow.Add(new Label(body) { Color = Theme.Text, MaxWidth = 280 });
        var addressRow = Row("Lighting address", body);
        LightingAddress = addressRow.Add(new TextBox(body) { Width = 220, Placeholder = "Found by itself; or host:port", FocusColor = Theme.Focus });
        LightingAddress.Submitted += box => LightingAddressChanged?.Invoke(box.Text.Trim());
        _content.Add(
            new Label(body, "Sends beats and each deck's play state to lighting software such as SoundSwitch, QLC+ or sLight.") { Color = Theme.Muted }
        );

        _musicFolder = Row("Music library", body).Add(new Label(body, musicFolder) { Color = Theme.Text, MaxWidth = 340 });
        _content.Add(new Label(body, "Right-click a folder in the folder list to make it the music library.") { Color = Theme.Muted });
        Row("Analysis saved in", body).Add(new Label(body, databasePath) { Color = Theme.Text, MaxWidth = 340 });
        _recordingsFolder = Row("Recordings saved in", body).Add(new Label(body) { Color = Theme.Text, MaxWidth = 340 });

        _content.Add(new Label(bold, "Keyboard") { Color = Theme.Text });
        var keys = _content.Add(new Panel { Spacing = 3 });
        foreach (var (key, action) in new[]
        {
            ("1 / 2", "Load the selected song to deck A / B"),
            ("Ctrl + Z", "Undo the last hot cue or beat grid change"),
            ("Z X C V", "Deck A: cue, play, sync, loop"),
            ("N M , .", "Deck B: cue, play, sync, loop"),
            ("3 4 / 9 0", "Halve / double the loop, deck A / B"),
            ("5 6 / 7 8", "Tempo down / up (hold); both together reset it"),
            ("Shift + those", "Volume down / up (hold)"),
            ("T / Y", "Headphones for deck A / B"),
            ("G / H", "Key lock deck A / B"),
            ("F1 F2 F3", "Kill A's low, mid, high; F9 F10 F11 for B"),
            ("F4 / F12", "Reset deck A / B's EQ and filter"),
            ("F5 F6 / F7 F8", "Filter A / B toward low-pass / high-pass (hold)"),
            ("[ ]  B", "Crossfader left, right (hold); B centres it"),
            ("Ctrl", "With a held key, moves a quarter as fast"),
            ("Q W E R", "Hotcues 1 to 4 on deck A; A S D F for 5 to 8"),
            ("U I O P", "Hotcues 1 to 4 on deck B; J K L ; for 5 to 8"),
            ("Shift", "With a hotcue key, or clicking a pad, clears it"),
            ("GRID", "Pads and their keys fix the beat grid; Shift nudges finely"),
            ("Tab / Enter / Esc", "Next control / press / leave a box or close this"),
        })
        {
            var row = keys.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            row.Add(new Panel { MinSize = new float2(130, 0) }).Add(new Label(body, key) { Color = Theme.Text });
            row.Add(new Label(body, action) { Color = Theme.Muted });
        }

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

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    /// <summary>
    /// A row with <paramref name="name"/> in a column wide enough that every row's controls line up.
    /// </summary>
    Panel Row(string name, Font font)
    {
        var row = _content.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
        row.Add(new Panel { MinSize = new float2(140, 0) }).Add(new Label(font, name) { Color = Theme.Muted });
        return row;
    }

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public void Open() => Visible = true;

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
        _output.Selected = _engine.OutputDevice;
        _headphones.Selected = _engine.HeadphoneDevice;
        _controller.Text = ControllerStatus?.Invoke() ?? "None connected";
        var lighting = LightingEnabled?.Invoke() ?? false;
        _lighting.Checked = lighting;
        _lightingStatus.Text = lighting ? LightingStatus?.Invoke() ?? "" : "";
        _largeListText.Checked = LargeListText?.Invoke() ?? false;

        // Never past the window's edges, scrolling instead.
        _box.MaxWidth = MathF.Max(0, ScreenSize.X - 2 * MARGIN);
        _box.MaxHeight = MathF.Max(0, ScreenSize.Y - 2 * MARGIN);
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
