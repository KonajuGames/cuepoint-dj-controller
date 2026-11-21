using Brutal.Numerics;
using Cuepoint.Audio;

namespace Cuepoint.UI;

/// <summary>
/// The effect on the whole mix, dropped down from the title bar's FX button: which effect, its length in beats, how
/// much of it, and ON. It works as a deck's does, timed by the deck leading the mix. Clicking anywhere else, the button
/// again, or <see cref="Close"/> (for Escape) puts it away. It covers the whole screen, invisibly, to catch that click,
/// so add it to the root after everything it should sit over, and before the menu its list opens on. Call
/// <see cref="Refresh"/> once a frame.
/// </summary>
sealed class MasterEffectsPanel : Panel
{
    const float LEVEL_LENGTH = 120;

    readonly DeckEffects _effects;
    readonly Panel _box;
    readonly Label _heading;
    readonly Dropdown<DeckEffect> _kind;
    readonly Button _shorter;
    readonly Label _beats;
    readonly Button _longer;
    readonly Label _levelLabel;
    readonly Slider _level;
    readonly Button _on;
    readonly Label _note;

    // Where the box's top left corner goes: under the button that opened it.
    float2 _anchor;

    // A press on the box itself mustn't count as a click away from it.
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    /// <summary>
    /// Where the effect list opens.
    /// </summary>
    public ContextMenu? Menu
    {
        set => _kind.Menu = value;
    }

    public MasterEffectsPanel(DeckEffects effects, Font small, Font body, Font bold, Images images)
    {
        _effects = effects;
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
        _heading = Add(new Label(bold, "MASTER FX") { Color = Theme.Text });
        _kind = Add(
            new Dropdown<DeckEffect>(small)
            {
                Items = () => Enum.GetValues<DeckEffect>(),
                Text = DeckPanel.EffectName,
                Selected = effects.Kind,
                Width = 90,
                Padding = 4,
                Tooltip = "The effect on the whole mix: an echo in time with the beat, reverb, a flanger sweeping in time, a gate chopping the mix in time, or a bit-crusher",
            }
        );
        _kind.Chosen += kind => effects.Kind = kind;
        _shorter = Add(
            new Button(small, "")
            {
                Icon = images.TriangleLeft,
                MinSize = new float2(20, 22),
                Padding = 2,
                Tooltip = "Fewer beats: closer echoes, a faster sweep or a quicker gate",
            }
        );
        _shorter.Clicked += _ => effects.StepBeats(-1);
        _beats = Add(new Label(body) { Color = Theme.Text });
        _longer = Add(
            new Button(small, "")
            {
                Icon = images.TriangleRight,
                MinSize = new float2(20, 22),
                Padding = 2,
                Tooltip = "More beats: echoes further apart, a slower sweep or a slower gate",
            }
        );
        _longer.Clicked += _ => effects.StepBeats(1);
        _levelLabel = Add(new Label(small, "LEVEL") { Color = Theme.Muted });
        _level = Add(
            new Slider
            {
                Length = LEVEL_LENGTH,
                Thickness = 14,
                TrackThickness = 3,
                HandleLength = 8,
                DefaultValue = 0.6f,
                Value = effects.Level,
                ShowFill = true,
                FillColor = Theme.Accent,
                Tooltip = "How much of the effect. Double-click for the usual amount",
            }
        );
        _level.Changed += slider => effects.Level = slider.Value;
        _on = Add(new Button(bold, "ON") { MinSize = new float2(52, 26), Tooltip = "Put the effect on the whole mix, or take it off" });
        _on.Clicked += _ => effects.On = !effects.On;
        _note = Add(new Label(small, "Timed by the deck leading the mix") { Color = Theme.Faint });

        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

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

    internal Dropdown<DeckEffect> Kind => _kind;

    public void Refresh()
    {
        _kind.Selected = _effects.Kind;
        _beats.Text = _effects.HasBeats ? DeckPanel.FormatFxBeats(_effects.Beats) : "–";
        _shorter.Enabled = _longer.Enabled = _effects.HasBeats;
        if (!_level.IsDragging)
            _level.Value = _effects.Level;
        var on = _effects.On;
        _on.Background = on ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Theme.Control;
        _on.HoverBackground = on ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Theme.ControlHover;
        _on.TextColor = on ? Theme.Text : Theme.Muted;
    }

    /// <summary>
    /// The room the beats take between their arrows, wide enough for "1/16".
    /// </summary>
    const float BEATS_WIDTH = 40;

    public override float2 Measure()
    {
        foreach (var child in Children)
            child.Measure();
        var first = _kind.Size.X + 12 + _shorter.Size.X + BEATS_WIDTH + _longer.Size.X;
        var second = _levelLabel.Size.X + 8 + LEVEL_LENGTH + 12 + _on.Size.X;
        var width = MathF.Max(MathF.Max(first, second), MathF.Max(_heading.Size.X, _note.Size.X));
        var height = _heading.Size.Y + 10 + MathF.Max(_kind.Size.Y, _shorter.Size.Y) + 10 + _on.Size.Y + 8 + _note.Size.Y;
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

        // The effect, then its length in beats between arrows.
        var row = MathF.Max(_kind.Size.Y, _shorter.Size.Y);
        Place(_kind, left, y + (row - _kind.Size.Y) / 2);
        var x = left + _kind.Size.X + 12;
        Place(_shorter, x, y + (row - _shorter.Size.Y) / 2);
        x += _shorter.Size.X;
        Place(_beats, x + (BEATS_WIDTH - _beats.Size.X) / 2, y + (row - _beats.Size.Y) / 2);
        Place(_longer, x + BEATS_WIDTH, y + (row - _longer.Size.Y) / 2);
        y += row + 10;

        // How much, and ON at the right.
        Place(_levelLabel, left, y + (_on.Size.Y - _levelLabel.Size.Y) / 2);
        Place(_level, left + _levelLabel.Size.X + 8, y + (_on.Size.Y - _level.Size.Y) / 2);
        Place(_on, right - _on.Size.X, y);
        y += _on.Size.Y + 8;
        Place(_note, left, y);
    }
}
