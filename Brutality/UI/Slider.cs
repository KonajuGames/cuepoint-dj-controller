using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

enum Orientation
{
    Horizontal,

    /// <summary>
    /// Bottom is <see cref="Slider.Min"/> and top is <see cref="Slider.Max"/>, as on a mixer's channel faders.
    /// </summary>
    Vertical,
}

/// <summary>
/// A value picked by position along a track: faders, a crossfader, a song's progress. Click to jump there, drag the
/// handle (from wherever it was grabbed), and double-click to go back to <see cref="DefaultValue"/>. With focus, the
/// arrows nudge it, Page Up/Down move a tenth, and Home/End go to the ends. <see cref="Changed"/> fires only for
/// those, not when code sets <see cref="Value"/>, so a slider can mirror something that also changes by itself.
/// </summary>
class Slider : Element
{
    const long DOUBLE_CLICK_MS = 500;

    public Orientation Orientation { get; set; } = Orientation.Horizontal;
    public float Min { get; set; }
    public float Max { get; set; } = 1;

    /// <summary>
    /// Where a double-click puts it back to, if anywhere: full volume, the crossfader's middle.
    /// </summary>
    public float? DefaultValue { get; set; }

    /// <summary>
    /// The length along the track, in pixels; it measures to this by <see cref="Thickness"/>.
    /// </summary>
    public float Length { get; set; } = 160;

    public float Thickness { get; set; } = 18;
    public float TrackThickness { get; set; } = 4;
    public float HandleLength { get; set; } = 10;

    /// <summary>
    /// Draws the track filled up to the value, as a progress bar does.
    /// </summary>
    public bool ShowFill { get; set; }

    /// <summary>
    /// Whether to draw a handle. A progress bar may not want one.
    /// </summary>
    public bool ShowHandle { get; set; } = true;

    /// <summary>
    /// Values marked across the track, such as the crossfader's middle or a cue point.
    /// </summary>
    public List<float> Marks { get; } = [];

    public float4 TrackColor { get; set; } = Colors.Hex(0xFFFFFF, 0.12f);
    public float4 FillColor { get; set; } = Colors.Hex(0xEB7828);
    public float4 HandleColor { get; set; } = Colors.Hex(0xE6E6EE);
    public float4 HandleHoverColor { get; set; } = Colors.White;
    public float4 MarkColor { get; set; } = Colors.Hex(0xFFFFFF, 0.45f);
    public float4 FocusColor { get; set; } = Colors.Hex(0x7AB8FF);

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The user moved it, by mouse or keyboard. Not raised when code sets <see cref="Value"/>.
    /// </summary>
    public event Action<Slider>? Changed;

    float _value;
    float? _grab;
    long _lastClickTime;

    public float Value
    {
        get => _value;
        set => _value = Math.Clamp(value, Math.Min(Min, Max), Math.Max(Min, Max));
    }

    /// <summary>
    /// The handle is being dragged, so whatever the slider mirrors shouldn't overwrite <see cref="Value"/> just now.
    /// </summary>
    public bool IsDragging => IsPressed && _grab is not null;

    public override bool IsInteractive => Enabled;
    public override bool IsFocusable => Enabled;

    protected bool IsVertical => Orientation == Orientation.Vertical;

    protected float Fraction => Max == Min ? 0 : (_value - Min) / (Max - Min);

    public override float2 Measure() => Size = IsVertical ? new float2(Thickness, Length) : new float2(Length, Thickness);

    /// <summary>
    /// How far along the handle can travel, in pixels: the length less the handle's own.
    /// </summary>
    protected float Travel => MathF.Max(1, Length - HandleLength);

    /// <summary>
    /// Pixels from the start of the track (top-left, or bottom for vertical) to the handle's leading edge for
    /// <paramref name="fraction"/>.
    /// </summary>
    protected float OffsetOf(float fraction) => fraction * Travel;

    /// <summary>
    /// How far along the track <paramref name="point"/> is, in pixels, measured the way values increase.
    /// </summary>
    float Along(float2 point) =>
        IsVertical ? ScreenPosition.Y + Size.Y - point.Y : point.X - ScreenPosition.X;

    (float2 Position, float2 Size) HandleRect()
    {
        var offset = OffsetOf(Fraction);
        return IsVertical
            ? (new float2(ScreenPosition.X, ScreenPosition.Y + Size.Y - offset - HandleLength), new float2(Size.X, HandleLength))
            : (new float2(ScreenPosition.X + offset, ScreenPosition.Y), new float2(HandleLength, Size.Y));
    }

    public override void Draw(Batch batch)
    {
        var half = HandleLength / 2;
        DrawTrack(batch);

        foreach (var mark in Marks)
        {
            if (Max == Min)
                continue;
            var at = OffsetOf(Math.Clamp((mark - Min) / (Max - Min), 0, 1)) + half;
            if (IsVertical)
                batch.AddRect(new float2(ScreenPosition.X + 2, ScreenPosition.Y + Size.Y - at - 1), new float2(Size.X - 4, 2), Faded(MarkColor));
            else
                batch.AddRect(new float2(ScreenPosition.X + at - 1, ScreenPosition.Y + 2), new float2(2, Size.Y - 4), Faded(MarkColor));
        }

        if (ShowHandle)
        {
            var (position, size) = HandleRect();
            batch.AddRect(position, size, Faded(IsHovered || IsDragging ? HandleHoverColor : HandleColor));
        }

        if (ShowFocus)
            batch.AddBorder(ScreenPosition - new float2(2, 2), Size + new float2(4, 4), 2, FocusColor);
    }

    /// <summary>
    /// Colours are drawn faded while disabled.
    /// </summary>
    protected float4 Faded(float4 color) => Enabled ? color : color * new float4(1, 1, 1, 0.4f);

    /// <summary>
    /// The track and its fill, under the marks and handle. Override to draw something else there, such as a
    /// waveform.
    /// </summary>
    protected virtual void DrawTrack(Batch batch)
    {
        // The track runs centre to centre of the handle's travel, so its ends line up with the handle at Min and Max.
        var half = HandleLength / 2;
        var trackStart = IsVertical
            ? new float2(ScreenPosition.X + (Size.X - TrackThickness) / 2, ScreenPosition.Y + half)
            : new float2(ScreenPosition.X + half, ScreenPosition.Y + (Size.Y - TrackThickness) / 2);
        var trackSize = IsVertical ? new float2(TrackThickness, Travel) : new float2(Travel, TrackThickness);
        batch.AddRect(trackStart, trackSize, Faded(TrackColor));

        if (ShowFill)
        {
            var fill = OffsetOf(Fraction);
            if (IsVertical)
                batch.AddRect(trackStart + new float2(0, Travel - fill), new float2(TrackThickness, fill), Faded(FillColor));
            else
                batch.AddRect(trackStart, new float2(fill, TrackThickness), Faded(FillColor));
        }
    }

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        var now = Environment.TickCount64;
        var isDoubleClick = now - _lastClickTime <= DOUBLE_CLICK_MS;
        _lastClickTime = isDoubleClick ? 0 : now;

        if (isDoubleClick && DefaultValue is { } reset)
        {
            _grab = null;
            SetByUser(reset);
            return;
        }

        // On the handle, keep hold of it where it was grabbed; elsewhere, jump so the handle centres on the click.
        var along = Along(point);
        var handleStart = OffsetOf(Fraction);
        var onHandle = ShowHandle && along >= handleStart && along <= handleStart + HandleLength;
        _grab = onHandle ? along - handleStart : HandleLength / 2;
        PointerDrag(point);
    }

    public override void PointerDrag(float2 point)
    {
        if (_grab is not { } grab)
            return;
        var fraction = Math.Clamp((Along(point) - grab) / Travel, 0, 1);
        SetByUser(Min + fraction * (Max - Min));
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        var step = (Max - Min) / 100;
        float? target = key switch
        {
            Key.Left or Key.Down => _value - step,
            Key.Right or Key.Up => _value + step,
            Key.PageDown => _value - step * 10,
            Key.PageUp => _value + step * 10,
            Key.Home => Min,
            Key.End => Max,
            _ => null,
        };
        if (target is not { } value)
            return false;
        SetByUser(value);
        return true;
    }

    void SetByUser(float value)
    {
        var before = _value;
        Value = value;
        if (_value != before)
            Changed?.Invoke(this);
    }
}
