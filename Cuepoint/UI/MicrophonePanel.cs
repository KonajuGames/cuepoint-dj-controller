using Brutal.Numerics;
using Cuepoint.Audio;

namespace Cuepoint.UI;

/// <summary>
/// The microphone, dropped down from the title bar's MIC button: ON, its level, and talkover. Clicking anywhere else,
/// the button again, or <see cref="Close"/> (for Escape) puts it away. It covers the whole screen, invisibly, to catch
/// that click, so add it to the root after everything it should sit over. Call <see cref="Refresh"/> once a frame.
/// </summary>
sealed class MicrophonePanel : Panel
{
    const float LEVEL_LENGTH = 120;

    readonly AudioEngine _engine;
    readonly Panel _box;
    readonly Label _heading;
    readonly Button _on;
    readonly Label _levelLabel;
    readonly Slider _level;
    readonly Checkbox _talkover;
    readonly Label _note;

    float2 _anchor;
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    public MicrophonePanel(AudioEngine engine, Font small, Font body, Font bold)
    {
        _engine = engine;
        Visible = false;
        _box = Add(
            new Panel
            {
                Padding = 12,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
            }
        );
        _heading = Add(new Label(bold, "MICROPHONE") { Color = Theme.Text });
        _on = Add(new Button(bold, "ON") { MinSize = new float2(52, 26), Tooltip = "Put the microphone into the main mix, or take it out" });
        _on.Clicked += _ => engine.MicrophoneOn = !engine.MicrophoneOn;
        _levelLabel = Add(new Label(small, "LEVEL") { Color = Theme.Muted });
        _level = Add(
            new Slider
            {
                Length = LEVEL_LENGTH,
                Thickness = 14,
                TrackThickness = 3,
                HandleLength = 8,
                DefaultValue = 0.7f,
                Value = engine.MicrophoneLevel,
                ShowFill = true,
                FillColor = Theme.Accent,
                Tooltip = "How loud the microphone is in the mix. Double-click for the usual level",
            }
        );
        _level.Changed += slider =>
        {
            engine.MicrophoneLevel = slider.Value;
            LevelChanged?.Invoke(slider.Value);
        };
        _talkover = Add(
            new Checkbox(body, "Talkover")
            {
                Checked = engine.Talkover,
                Tooltip = "Turn the music down while the microphone's spoken into, and back up once it's quiet",
            }
        );
        _talkover.Changed += box =>
        {
            engine.Talkover = box.Checked;
            TalkoverChanged?.Invoke(box.Checked);
        };
        _note = Add(new Label(small, "No microphone: choose one in the options") { Color = Theme.Warning });

        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    /// <summary>
    /// The talkover box was changed, so it can be kept.
    /// </summary>
    public event Action<bool>? TalkoverChanged;

    /// <summary>
    /// The level was moved, so it can be kept.
    /// </summary>
    public event Action<float>? LevelChanged;

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override void SecondaryClick(float2 point)
    {
        if (!_box.Contains(point))
            Close();
    }

    /// <summary>
    /// Shows it with its top left corner at <paramref name="anchor"/>, or puts it away if it's showing.
    /// </summary>
    public void Toggle(float2 anchor)
    {
        if (Visible)
        {
            Close();
            return;
        }
        _anchor = anchor;
        Refresh();
        Visible = true;
    }

    public void Close() => Visible = false;

    // Escape puts it away while one of its own controls has focus, too.
    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        Close();
        return true;
    }

    internal Button OnButton => _on;

    public void Refresh()
    {
        var on = _engine.MicrophoneOn;
        _on.Background = on ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Theme.Control;
        _on.HoverBackground = on ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Theme.ControlHover;
        _on.TextColor = on ? Theme.Text : Theme.Muted;
        if (!_level.IsDragging)
            _level.Value = _engine.MicrophoneLevel;
        _talkover.Checked = _engine.Talkover;
        _note.Visible = _engine.MicrophoneDevice is null;
    }

    public override float2 Measure()
    {
        foreach (var child in Children)
            child.Measure();
        var row = _levelLabel.Size.X + 8 + LEVEL_LENGTH + 12 + _on.Size.X;
        var width = MathF.Max(MathF.Max(row, _heading.Size.X), _note.Visible ? _note.Size.X : 0);
        var height = _heading.Size.Y + 10 + _on.Size.Y + 10 + _talkover.Size.Y + (_note.Visible ? 10 + _note.Size.Y : 0);
        _box.MinSize = new float2(width, height) + new float2(2 * _box.Padding, 2 * _box.Padding);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));

        var boxAt = new float2(Math.Clamp(_anchor.X, 4, MathF.Max(4, ScreenSize.X - _box.Size.X - 4)), _anchor.Y);
        _box.Arrange(boxAt);
        var left = boxAt.X + _box.Padding;
        var right = boxAt.X + _box.Size.X - _box.Padding;
        var y = boxAt.Y + _box.Padding;
        Place(_heading, left, y);
        y += _heading.Size.Y + 10;
        Place(_levelLabel, left, y + (_on.Size.Y - _levelLabel.Size.Y) / 2);
        Place(_level, left + _levelLabel.Size.X + 8, y + (_on.Size.Y - _level.Size.Y) / 2);
        Place(_on, right - _on.Size.X, y);
        y += _on.Size.Y + 10;
        Place(_talkover, left, y);
        y += _talkover.Size.Y + 10;
        if (_note.Visible)
            Place(_note, left, y);
    }
}
