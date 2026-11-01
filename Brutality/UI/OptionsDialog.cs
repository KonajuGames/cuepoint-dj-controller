using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// The options, over the screen dimmed behind them: automatic gain, how much of the song the scrolling waveforms
/// show, where the library and its analysis live, and the keyboard shortcuts. Clicking outside it, Close, or Escape
/// closes it. Sized to the screen by whoever lays it out, with <see cref="ScreenSize"/>.
/// </summary>
sealed class OptionsDialog : Panel
{
    public const float MIN_WAVEFORM_SECONDS = 4;
    public const float MAX_WAVEFORM_SECONDS = 30;

    readonly AudioEngine _engine;
    readonly Panel _box;
    readonly Button _autoGain;

    public float2 ScreenSize { get; set; }

    /// <summary>
    /// How many seconds of song the scrolling waveforms show across, as set here.
    /// </summary>
    public Slider WaveformSeconds { get; }

    public event Action? Closed;

    /// <summary>
    /// The headphones were moved to another device, or turned off; the new device's name, or null.
    /// </summary>
    public event Action<string?>? HeadphonesChanged;

    /// <summary>
    /// The main output was moved to another device (null for the system's default).
    /// </summary>
    public event Action<string?>? OutputChanged;

    readonly Button _output;

    readonly Button _headphones;
    readonly Label _controller;
    readonly Button _lighting;
    readonly Label _lightingStatus;

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

    public OptionsDialog(AudioEngine engine, string musicFolder, string databasePath, Font body, Font bold, Font heading)
    {
        _engine = engine;
        Visible = false;
        // Blending is in linear light, where black at 55% only takes off about a third, so it takes more to dim.
        Background = Colors.Hex(0x000000, 0.75f);

        _box = Add(
            new Panel
            {
                Padding = 20,
                Spacing = 14,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(520, 0),
            }
        );
        _box.Add(new Label(heading, "Options") { Color = Theme.Text });

        var gainRow = Row("Automatic gain", body);
        _autoGain = gainRow.Add(new Button(bold, "On") { MinSize = new float2(64, 0), FocusColor = Theme.Focus });
        _autoGain.Clicked += _ => engine.AutoGain = !engine.AutoGain;
        _box.Add(new Label(body, "Plays every song at about the same loudness, from its analysis.") { Color = Theme.Muted });

        // Each click moves the main mix on to the next playback device, and after the last, back to the default.
        var outputRow = Row("Main output", body);
        _output = outputRow.Add(new Button(bold, "") { MinSize = new float2(64, 0), FocusColor = Theme.Focus });
        _output.Clicked += _ =>
        {
            var names = AudioEngine.PlaybackDevices().Select(device => device.Name).ToList();
            var at = engine.OutputDevice is { } current ? names.IndexOf(current) : -1;
            var moved = false;
            for (var i = at + 1; i < names.Count && !moved; i++)
                moved = engine.UseOutputDevice(names[i]);
            if (!moved)
                engine.UseOutputDevice(null);
            OutputChanged?.Invoke(engine.OutputDevice);
        };

        // Each click moves the headphones on to the next playback device, and after the last, off. The main output's
        // own device is offered too if it has channels 3 and 4 to put them on, as a DJ controller's sound card does.
        var phonesRow = Row("Headphones", body);
        _headphones = phonesRow.Add(new Button(bold, "Off") { MinSize = new float2(64, 0), FocusColor = Theme.Focus });
        _headphones.Clicked += _ =>
        {
            var names = AudioEngine.PlaybackDevices().Select(device => device.Name).ToList();
            var at = engine.HeadphoneDevice is { } current ? names.IndexOf(current) : -1;
            string? next = null;
            for (var i = at + 1; i < names.Count; i++)
            {
                if (engine.UseHeadphoneDevice(names[i]))
                {
                    next = names[i];
                    break;
                }
            }
            if (next is null)
                engine.UseHeadphoneDevice(null);
            HeadphonesChanged?.Invoke(engine.HeadphoneDevice);
        };
        _box.Add(
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
        var seconds = zoomRow.Add(new Label(body) { Color = Theme.Text });
        WaveformSeconds.Changed += slider => seconds.Text = $"{slider.Value:F0} seconds";
        seconds.Text = $"{WaveformSeconds.Value:F0} seconds";

        var controllerRow = Row("Controller", body);
        _controller = controllerRow.Add(new Label(body) { Color = Theme.Text, MaxWidth = 200 });
        var map = controllerRow.Add(new Button(bold, "Map controls") { FocusColor = Theme.Focus });
        map.Clicked += _ => MapControllerClicked?.Invoke();

        var lightingRow = Row("Lighting (OS2L)", body);
        _lighting = lightingRow.Add(new Button(bold, "On") { MinSize = new float2(64, 0), FocusColor = Theme.Focus });
        _lighting.Clicked += _ => LightingToggled?.Invoke();
        _lightingStatus = lightingRow.Add(new Label(body) { Color = Theme.Text, MaxWidth = 280 });
        var addressRow = Row("Lighting address", body);
        LightingAddress = addressRow.Add(new TextBox(body) { Width = 220, Placeholder = "Found by itself; or host:port", FocusColor = Theme.Focus });
        LightingAddress.Submitted += box => LightingAddressChanged?.Invoke(box.Text.Trim());
        _box.Add(
            new Label(body, "Sends beats and each deck's play state to lighting software such as SoundSwitch, QLC+ or sLight.") { Color = Theme.Muted }
        );

        Row("Music library", body).Add(new Label(body, musicFolder) { Color = Theme.Text, MaxWidth = 340 });
        Row("Analysis saved in", body).Add(new Label(body, databasePath) { Color = Theme.Text, MaxWidth = 340 });

        _box.Add(new Label(bold, "Keyboard") { Color = Theme.Text });
        var keys = _box.Add(new Panel { Spacing = 3 });
        foreach (var (key, action) in new[]
        {
            ("1 / 2", "Load the selected song to deck A / B"),
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
            ("Tab / Enter / Esc", "Next control / press / leave a box or close this"),
        })
        {
            var row = keys.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            row.Add(new Panel { MinSize = new float2(130, 0) }).Add(new Label(body, key) { Color = Theme.Text });
            row.Add(new Label(body, action) { Color = Theme.Muted });
        }

        var close = _box.Add(new Button(bold, "Close") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        close.Clicked += _ => Close();

        // A click on the dimmed screen around it closes it too.
        Clicked += _ => Close();
    }

    /// <summary>
    /// A row with <paramref name="name"/> in a column wide enough that every row's controls line up.
    /// </summary>
    Panel Row(string name, Font font)
    {
        var row = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
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
        _autoGain.Text = _engine.AutoGain ? "On" : "Off";
        _output.Text = _engine.OutputDevice ?? "System default";
        _headphones.Text = _engine.HeadphoneDevice is { } phones ? _engine.HeadphonesOnOutput ? $"{phones} (channels 3-4)" : phones : "Off";
        _controller.Text = ControllerStatus?.Invoke() ?? "None connected";
        var lighting = LightingEnabled?.Invoke() ?? false;
        _lighting.Text = lighting ? "On" : "Off";
        _lighting.Background = lighting ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.55f) : Colors.Hex(0x2A2A35);
        _lightingStatus.Text = lighting ? LightingStatus?.Invoke() ?? "" : "";
        _autoGain.Background = _engine.AutoGain ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.55f) : Colors.Hex(0x2A2A35);

        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 2)));
    }
}
