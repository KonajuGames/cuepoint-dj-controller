using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// The headphones' settings, dropped down from the title bar's headphones button: what they hear, from the decks with
/// PFL on alone through a blend to the main mix alone, and how loud. Clicking anywhere else, the button again, or
/// <see cref="Close"/> (for Escape) puts it away. It covers the whole screen, invisibly, to catch that click, so add
/// it to the root after everything it should sit over. Call <see cref="Refresh"/> once a frame.
/// </summary>
sealed class HeadphonesPanel : Panel
{
    const float KNOB = 40;

    readonly AudioEngine _engine;
    // The box's background, under the controls.
    readonly Panel _box;
    readonly Label _heading;
    readonly Label _mixLabel;
    readonly Knob _mix;
    readonly Label _cueEnd;
    readonly Label _mainEnd;
    readonly Label _volumeLabel;
    readonly Knob _volume;
    readonly Label _note;

    // Where the box's top right corner goes: under the button that opened it.
    float2 _anchor;

    // A press on the box itself mustn't count as a click away from it.
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    public HeadphonesPanel(AudioEngine engine, Font small, Font body, Font bold, Images images)
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
        _heading = Add(new Label(bold, "HEADPHONES") { Color = Theme.Text });
        _mixLabel = Add(new Label(small, "CUE / MAIN") { Color = Theme.Muted });
        _mix = Add(
            new Knob(images.LargeKnob, images.Dot)
            {
                Diameter = KNOB,
                DefaultValue = 0,
                Value = engine.CueMix,
                FillColor = Theme.Accent,
                Tooltip = "What the headphones hear: the decks with PFL on, turned left; the main mix, turned right; or a blend. Double-click for the cue alone",
            }
        );
        _mix.Changed += knob => engine.CueMix = knob.Value;
        _cueEnd = Add(new Label(small, "CUE") { Color = Theme.Faint });
        _mainEnd = Add(new Label(small, "MAIN") { Color = Theme.Faint });
        _volumeLabel = Add(new Label(small, "VOLUME") { Color = Theme.Muted });
        _volume = Add(
            new Knob(images.LargeKnob, images.Dot)
            {
                Diameter = KNOB,
                DefaultValue = 0.8f,
                Value = engine.HeadphoneVolume,
                FillColor = Theme.Accent,
                Tooltip = "Headphone volume. Double-click for the usual level",
            }
        );
        _volume.Changed += knob => engine.HeadphoneVolume = knob.Value;
        _note = Add(new Label(small, "No headphones output: choose one in the options") { Color = Theme.Warning });

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
    /// Shows it with its top right corner at <paramref name="anchor"/>, or puts it away if it's showing.
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

    /// <summary>
    /// The blend knob, for tests.
    /// </summary>
    internal Knob Mix => _mix;

    internal Knob Volume => _volume;

    public void Refresh()
    {
        if (!_mix.IsDragging)
            _mix.Value = _engine.CueMix;
        if (!_volume.IsDragging)
            _volume.Value = _engine.HeadphoneVolume;
        // Without headphones there's nothing to hear these on, but they can still be set ready.
        _note.Visible = _engine.Headphones is null;
    }

    public override float2 Measure()
    {
        foreach (var child in Children)
            child.Measure();
        var columns = 2 * MathF.Max(KNOB + 2 * (_mainEnd.Size.X + 4), _mixLabel.Size.X) + 16;
        var height = _heading.Size.Y + 10 + _mixLabel.Size.Y + 4 + KNOB + (_note.Visible ? 10 + _note.Size.Y : 0);
        var inner = new float2(MathF.Max(columns, _note.Visible ? _note.Size.X : 0), height);
        _box.MinSize = inner + new float2(2 * _box.Padding, 2 * _box.Padding);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));

        var boxAt = new float2(Math.Clamp(_anchor.X - _box.Size.X, 4, MathF.Max(4, ScreenSize.X - _box.Size.X - 4)), _anchor.Y);
        _box.Arrange(boxAt);
        var left = boxAt.X + _box.Padding;
        var y = boxAt.Y + _box.Padding;
        var column = (_box.Size.X - 2 * _box.Padding - 16) / 2;
        Place(_heading, left, y);
        y += _heading.Size.Y + 10;

        // The blend on the left, CUE and MAIN at either end of its turn; the volume on the right.
        float Centred(Element child, float columnLeft) => columnLeft + (column - child.Size.X) / 2;
        var rightColumn = left + column + 16;
        Place(_mixLabel, Centred(_mixLabel, left), y);
        Place(_volumeLabel, Centred(_volumeLabel, rightColumn), y);
        y += _mixLabel.Size.Y + 4;
        Place(_mix, Centred(_mix, left), y);
        Place(_volume, Centred(_volume, rightColumn), y);
        // CUE and MAIN beside the ends of the blend's turn, at the bottom either side.
        var mixLeft = left + (column - KNOB) / 2;
        var endsY = y + KNOB - _cueEnd.Size.Y;
        Place(_cueEnd, mixLeft - 2 - _cueEnd.Size.X, endsY);
        Place(_mainEnd, mixLeft + KNOB + 2, endsY);
        y += KNOB + 10;
        if (_note.Visible)
            Place(_note, left, y);
    }
}
