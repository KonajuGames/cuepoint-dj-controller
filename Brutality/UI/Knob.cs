using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// A rotary control, as on a mixer: a cap with a pointer, turning through <see cref="SWEEP_DEGREES"/> from
/// <see cref="Min"/> at the bottom left to <see cref="Max"/> at the bottom right, with an arc in
/// <see cref="FillColor"/> from <see cref="FillFrom"/> (the middle, for an EQ) round to the value. Drag up to turn
/// it up and down to turn it down, wherever on it the drag starts; Shift or Ctrl turns it finely. The mouse wheel
/// nudges it, and double-clicking puts it back to <see cref="DefaultValue"/>. With focus, the arrows nudge it, Page
/// Up/Down move a tenth, and Home/End go to the ends. <see cref="Changed"/> fires only for those, not when code sets
/// <see cref="Value"/>, as a <see cref="Slider"/>'s does.
/// </summary>
sealed class Knob : Element
{
    const long DOUBLE_CLICK_MS = 500;

    /// <summary>
    /// How far round it turns from one end to the other, in degrees.
    /// </summary>
    public const float SWEEP_DEGREES = 270;

    /// <summary>
    /// How far a drag goes from one end to the other, in pixels, and how much further with Shift or Ctrl.
    /// </summary>
    const float DRAG_PIXELS = 160;

    const float FINE = 5;

    /// <summary>
    /// How much of the way from one end to the other a notch of the mouse wheel turns it.
    /// </summary>
    const float WHEEL_STEP = 0.02f;

    readonly Image _cap;
    readonly Image _dot;

    public Knob(Image cap, Image dot)
    {
        _cap = cap;
        _dot = dot;
    }

    public float Min { get; set; }
    public float Max { get; set; } = 1;

    /// <summary>
    /// Where a double-click puts it back to, if anywhere: flat, for an EQ.
    /// </summary>
    public float? DefaultValue { get; set; }

    /// <summary>
    /// Where the arc starts, if not from <see cref="Min"/>: the middle of a control that cuts one way and boosts the
    /// other, so it fills out from there towards the value.
    /// </summary>
    public float? FillFrom { get; set; }

    /// <summary>
    /// Across the whole control, arc and all; the cap is in the middle.
    /// </summary>
    public float Diameter { get; set; } = 28;

    public float4 FillColor { get; set; } = Theme.Accent;

    /// <summary>
    /// The arc's colour when the value's at <see cref="Min"/>, such as a band killed; the fill colour if null.
    /// </summary>
    public float4? MinColor { get; set; }

    public float4 TrackColor { get; set; } = Colors.Hex(0xFFFFFF, 0.13f);
    public float4 PointerColor { get; set; } = Colors.White;
    public float4 FocusColor { get; set; } = Theme.Focus;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The user turned it, by mouse, wheel or keyboard. Not raised when code sets <see cref="Value"/>.
    /// </summary>
    public event Action<Knob>? Changed;

    float _value;
    long _lastClickTime;

    // Where the drag's turned it to, before clamping, so turning past an end and back picks up where it left it; and
    // where the pointer was last.
    float? _dragValue;
    float _lastY;

    public float Value
    {
        get => _value;
        set => _value = Math.Clamp(value, Math.Min(Min, Max), Math.Max(Min, Max));
    }

    /// <summary>
    /// It's being turned with the mouse, so whatever it mirrors shouldn't overwrite <see cref="Value"/> just now.
    /// </summary>
    public bool IsDragging => IsPressed && _dragValue is not null;

    public override bool IsInteractive => Enabled;
    public override bool IsFocusable => Enabled;

    float Fraction => Max == Min ? 0 : (_value - Min) / (Max - Min);

    public override float2 Measure() => Size = new float2(Diameter, Diameter);

    /// <summary>
    /// The angle for <paramref name="fraction"/> of the way round, in radians clockwise from straight up.
    /// </summary>
    static float AngleOf(float fraction) => (fraction - 0.5f) * SWEEP_DEGREES * MathF.PI / 180;

    /// <summary>
    /// The point <paramref name="radius"/> out from the middle at <paramref name="angle"/>.
    /// </summary>
    float2 At(float angle, float radius)
    {
        var centre = ScreenPosition + Size / 2;
        return centre + new float2(MathF.Sin(angle) * radius, -MathF.Cos(angle) * radius);
    }

    void Dot(Batch batch, float2 at, float size, float4 color) =>
        batch.AddImage(_dot, at - new float2(size / 2, size / 2), color, new float2(size, size));

    public override void Draw(Batch batch)
    {
        // The arc round the outside: its whole sweep faintly, then the part from where it fills from to the value.
        var radius = Diameter / 2 - 1.5f;
        var dotSize = 3f;
        var steps = Math.Max(8, (int)(radius * SWEEP_DEGREES * MathF.PI / 180 / 1.5f));
        var from = FillFrom is { } origin && Max != Min ? Math.Clamp((origin - Min) / (Max - Min), 0, 1) : 0;
        var (low, high) = (MathF.Min(from, Fraction), MathF.Max(from, Fraction));
        var fill = MinColor is { } atMin && _value <= Min ? atMin : FillColor;
        for (var i = 0; i <= steps; i++)
        {
            var fraction = (float)i / steps;
            var lit = fraction >= low - 0.5f / steps && fraction <= high + 0.5f / steps && high > low;
            Dot(batch, At(AngleOf(fraction), radius), dotSize, Faded(lit ? fill : TrackColor));
        }
        // A killed band shows its arc all the way round in its colour, so it reads at a glance.
        if (MinColor is not null && _value <= Min)
            Dot(batch, At(AngleOf(0), radius), dotSize + 1, Faded(fill));

        // The cap, then its pointer from near the middle out to its edge.
        var capSize = new float2(_cap.Size.X, _cap.Size.Y);
        batch.AddImage(_cap, ScreenPosition + (Size - capSize) / 2, Faded(IsHovered || IsDragging ? new float4(1.25f, 1.25f, 1.25f, 1) : Colors.White));
        var angle = AngleOf(Fraction);
        var capRadius = capSize.X / 2;
        for (var r = capRadius * 0.3f; r <= capRadius - 1.5f; r += 0.75f)
            Dot(batch, At(angle, r), 2.2f, Faded(PointerColor));

        if (ShowFocus)
            batch.AddBorder(ScreenPosition - new float2(2, 2), Size + new float2(4, 4), 2, FocusColor);
    }

    float4 Faded(float4 color) => Enabled ? color : color * new float4(1, 1, 1, 0.4f);

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        var now = Environment.TickCount64;
        var isDoubleClick = now - _lastClickTime <= DOUBLE_CLICK_MS;
        _lastClickTime = isDoubleClick ? 0 : now;
        if (isDoubleClick && DefaultValue is { } reset)
        {
            _dragValue = null;
            SetByUser(reset);
            return;
        }
        (_dragValue, _lastY, _fine) = (_value, point.Y, (modifiers & (Modifiers.Shift | Modifiers.Control)) != 0);
    }

    // Whether the drag is a fine one: Shift or Ctrl held as it started.
    bool _fine;

    public override void PointerDrag(float2 point)
    {
        if (_dragValue is not { } dragged)
            return;
        var moved = _lastY - point.Y;
        _lastY = point.Y;
        var next = dragged + moved / (DRAG_PIXELS * (_fine ? FINE : 1)) * (Max - Min);
        _dragValue = Math.Clamp(next, Math.Min(Min, Max), Math.Max(Min, Max));
        SetByUser(_dragValue.Value);
    }

    public override bool Wheel(float2 notches)
    {
        if (!Enabled || notches.Y == 0)
            return false;
        SetByUser(_value + notches.Y * WHEEL_STEP * (Max - Min));
        return true;
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
