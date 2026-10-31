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

        Row("Music library", body).Add(new Label(body, musicFolder) { Color = Theme.Text, MaxWidth = 340 });
        Row("Analysis saved in", body).Add(new Label(body, databasePath) { Color = Theme.Text, MaxWidth = 340 });

        _box.Add(new Label(bold, "Keyboard") { Color = Theme.Text });
        var keys = _box.Add(new Panel { Spacing = 3 });
        foreach (var (key, action) in new[]
        {
            ("1 / 2", "Load the selected song to deck A / B"),
            ("Z / X", "Cue / Play deck A"),
            ("N / M", "Cue / Play deck B"),
            ("Tab", "Next control"),
            ("Enter", "Press, submit or load"),
            ("Esc", "Leave a box, or close this"),
        })
        {
            var row = keys.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            row.Add(new Panel { MinSize = new float2(60, 0) }).Add(new Label(body, key) { Color = Theme.Text });
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
