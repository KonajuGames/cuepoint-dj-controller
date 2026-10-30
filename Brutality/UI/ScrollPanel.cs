using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// A vertical panel that grows to fit its children up to <see cref="MaxWidth"/> and <see cref="MaxHeight"/>, then
/// clips them and scrolls on whichever axis overflows. Each overflowing axis gets a scrollbar, on the right for
/// vertical and along the bottom for horizontal: scroll with the mouse wheel (a tilt wheel, a sideways touchpad swipe
/// or Shift+wheel for horizontal), drag a thumb, or click a track beside its thumb to move a page. Wheel and page
/// moves glide to where they're going (see <see cref="SmoothScrolling"/>); dragging a thumb follows the mouse exactly.
/// </summary>
sealed class ScrollPanel : Panel
{
    /// <summary>
    /// The widest the panel gets, border and padding included, before it scrolls sideways. Unlimited by default.
    /// </summary>
    public float MaxWidth { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// The tallest the panel gets, border and padding included, before it starts scrolling.
    /// </summary>
    public float MaxHeight { get; set; } = 200;

    /// <summary>
    /// Pixels moved per notch of a mouse wheel. Touchpads send fractions of a notch.
    /// </summary>
    public float ScrollStep { get; set; } = 48;

    /// <summary>
    /// While scrolled to the bottom, stay there as content is added, like a log or chat.
    /// </summary>
    public bool StickToEnd { get; set; }

    /// <summary>
    /// The scrollbars' thickness: width of the vertical one, height of the horizontal one.
    /// </summary>
    public float ScrollbarWidth { get; set; } = 6;

    /// <summary>
    /// How thick the strip that takes the mouse is, centred on each scrollbar; more than the bar so it's easy to grab.
    /// </summary>
    public float ScrollbarHitWidth { get; set; } = 12;

    public float4 ScrollbarTrack { get; set; } = UiColor.Hex(0xFFFFFF, 0.06f);
    public float4 ScrollbarThumb { get; set; } = UiColor.Hex(0xFFFFFF, 0.3f);
    public float4 ScrollbarThumbHover { get; set; } = UiColor.Hex(0xFFFFFF, 0.45f);
    public float4 ScrollbarThumbDragging { get; set; } = UiColor.Hex(0xFFFFFF, 0.6f);

    /// <summary>
    /// Animate wheel and page moves rather than jumping. On by default.
    /// </summary>
    public bool SmoothScrolling { get; set; } = true;

    /// <summary>
    /// How quickly a smooth scroll settles: the time to cover about two thirds of the remaining distance. Most of a
    /// move is done in three times this.
    /// </summary>
    public float SmoothScrollTime { get; set; } = 0.035f;

    /// <summary>
    /// How far the content is scrolled left (X) and up (Y), in pixels, right now; kept within
    /// <see cref="MaxScroll"/> by layout. Setting it jumps straight there; use <see cref="ScrollTo"/> to glide.
    /// </summary>
    public float2 ScrollOffset
    {
        get => _offset;
        set => _offset = _target = value;
    }

    /// <summary>
    /// Where a smooth scroll is heading; equal to <see cref="ScrollOffset"/> once it settles.
    /// </summary>
    public float2 ScrollTarget => _target;

    public bool IsAnimating => _offset != _target;

    /// <summary>
    /// Scrolls to <paramref name="offset"/>, gliding there if <see cref="SmoothScrolling"/> is on.
    /// </summary>
    public void ScrollTo(float2 offset)
    {
        _target = float2.Clamp(offset, float2.Zero, MaxScroll);
        if (!SmoothScrolling)
            _offset = _target;
    }

    /// <summary>
    /// The offsets that show the right and bottom ends of the content; zero on an axis whose content fits.
    /// </summary>
    public float2 MaxScroll => float2.Max(float2.Zero, _content - _viewport);

    public bool CanScrollX => MaxScroll.X > 0;
    public bool CanScrollY => MaxScroll.Y > 0;
    public bool CanScroll => CanScrollX || CanScrollY;

    /// <summary>
    /// A thumb is being dragged.
    /// </summary>
    public bool IsDraggingThumb => _drag is not null && IsPressed;

    /// <summary>
    /// Only the scrollbars take the mouse, and only while there's something to scroll; everywhere else, clicks reach
    /// the children.
    /// </summary>
    public override bool IsInteractive => CanScroll;

    public override UiCursor Cursor => UiCursor.Arrow;

    /// <summary>
    /// Scrolling the log shouldn't take you out of the note box, say.
    /// </summary>
    public override bool KeepsFocusOnClick => true;

    enum Axis
    {
        X,
        Y,
    }

    float2 _offset;
    float2 _target;

    /// <summary>
    /// When the animation last stepped, from <see cref="UiClock"/>; NaN before the first layout.
    /// </summary>
    double _lastStep = double.NaN;

    float2 _content;

    /// <summary>
    /// The area the content shows through, inside the border, padding and any scrollbars.
    /// </summary>
    float2 _viewport;

    /// <summary>
    /// While dragging a thumb: which one, and how far along it it was grabbed.
    /// </summary>
    (Axis Axis, float Grab)? _drag;

    /// <summary>
    /// The scrollbar the mouse was last over, from hit testing, for its hover colour.
    /// </summary>
    Axis? _hoverAxis;

    /// <summary>
    /// The room a scrollbar takes from the content: the bar plus a padding-sized gap before it.
    /// </summary>
    float BarSpace => ScrollbarWidth + Padding;

    public ScrollPanel() => Direction = UiDirection.Vertical;

    static float Along(float2 v, Axis axis) => axis == Axis.X ? v.X : v.Y;

    static float2 WithAxis(float2 v, Axis axis, float value) =>
        axis == Axis.X ? new float2(value, v.Y) : new float2(v.X, value);

    /// <summary>
    /// A scrollbar's track (top-left corner and length) and its thumb (offset along the track and length), in screen
    /// pixels.
    /// </summary>
    (float2 TrackPosition, float TrackLength, float ThumbOffset, float ThumbLength) Scrollbar(Axis axis)
    {
        var trackPosition =
            axis == Axis.Y
                ? new float2(ScreenPosition.X + Size.X - Inset - ScrollbarWidth, ScreenPosition.Y + Inset)
                : new float2(ScreenPosition.X + Inset, ScreenPosition.Y + Size.Y - Inset - ScrollbarWidth);
        var trackLength = Along(_viewport, axis);
        var content = Along(_content, axis);
        var max = Along(MaxScroll, axis);
        var thumbLength = MathF.Max(MathF.Min(16, trackLength), trackLength * trackLength / MathF.Max(content, 1));
        var thumbOffset = max > 0 ? (trackLength - thumbLength) * (Along(ScrollOffset, axis) / max) : 0;
        return (trackPosition, trackLength, thumbOffset, thumbLength);
    }

    bool CanScrollOn(Axis axis) => axis == Axis.X ? CanScrollX : CanScrollY;

    /// <summary>
    /// Which scrollbar <paramref name="point"/> is on, if any.
    /// </summary>
    Axis? ScrollbarAt(float2 point)
    {
        var halfThickness = MathF.Max(ScrollbarWidth, ScrollbarHitWidth) / 2;
        foreach (var axis in (ReadOnlySpan<Axis>)[Axis.Y, Axis.X])
        {
            if (!CanScrollOn(axis))
                continue;

            var (track, length, _, _) = Scrollbar(axis);
            var centre = axis == Axis.Y ? track.X + ScrollbarWidth / 2 : track.Y + ScrollbarWidth / 2;
            var across = axis == Axis.Y ? point.X : point.Y;
            var along = Along(point, axis) - Along(track, axis);
            if (MathF.Abs(across - centre) <= halfThickness && along >= 0 && along < length)
                return axis;
        }
        return null;
    }

    public override float2 Measure()
    {
        // Decided before this measure changes the extents, so a log that was at (or heading for) its end stays
        // there.
        var wasAtEnd = _target.Y >= MaxScroll.Y - 0.5f;

        _content = MeasureChildren();

        // Each scrollbar takes room from the other axis, which can make that axis overflow too, so settle it in a
        // few passes; it can only add bars, so it converges at once in practice.
        bool barX = false, barY = false;
        var size = float2.Zero;
        for (var pass = 0; pass < 3; pass++)
        {
            var bars = new float2(barY ? BarSpace : 0, barX ? BarSpace : 0);
            size = float2.Max(
                float2.Min(_content + new float2(2 * Inset, 2 * Inset) + bars, new float2(MaxWidth, MaxHeight)),
                MinSize
            );
            _viewport = float2.Max(float2.Zero, size - new float2(2 * Inset, 2 * Inset) - bars);

            var needX = _content.X > _viewport.X + 0.5f;
            var needY = _content.Y > _viewport.Y + 0.5f;
            if (needX == barX && needY == barY)
                break;
            (barX, barY) = (needX || barX, needY || barY);
        }
        Size = size;

        // A drag in progress owns the offset, so new log lines don't yank the thumb out from under the mouse.
        _target = float2.Clamp(_target, float2.Zero, MaxScroll);
        if (StickToEnd && wasAtEnd && !IsDraggingThumb)
            _target = new float2(_target.X, MaxScroll.Y);

        StepAnimation();
        return Size;
    }

    /// <summary>
    /// Moves the offset a frame's worth towards the target (see <see cref="UiClock.Approach"/>).
    /// </summary>
    void StepAnimation()
    {
        var first = double.IsNaN(_lastStep);
        var dt = UiClock.Delta(ref _lastStep);

        // The first layout has nothing to animate from, so it lands where it should be.
        var time = SmoothScrolling && !first ? SmoothScrollTime : 0;
        _offset = new float2(
            UiClock.Approach(_offset.X, _target.X, dt, time),
            UiClock.Approach(_offset.Y, _target.Y, dt, time)
        );
        _offset = float2.Clamp(_offset, float2.Zero, MaxScroll);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        // Whole pixels keep text sharp mid-scroll.
        ArrangeChildren(position + new float2(Inset - MathF.Round(ScrollOffset.X), Inset - MathF.Round(ScrollOffset.Y)));
    }

    public override void Draw(UiBatch batch)
    {
        DrawFrame(batch);

        // Inside the border, and short of any scrollbar, so scrolled content never draws under one.
        var clipSize =
            Size
            - new float2(2 * BorderWidth, 2 * BorderWidth)
            - new float2(CanScrollY ? BarSpace : 0, CanScrollX ? BarSpace : 0);
        batch.PushClip(ScreenPosition + new float2(BorderWidth, BorderWidth), clipSize);
        DrawChildren(batch);
        batch.PopClip();

        foreach (var axis in (ReadOnlySpan<Axis>)[Axis.Y, Axis.X])
        {
            if (!CanScrollOn(axis))
                continue;

            var (track, length, thumbOffset, thumbLength) = Scrollbar(axis);
            var thumbColor =
                IsDraggingThumb && _drag?.Axis == axis ? ScrollbarThumbDragging
                : IsHovered && _hoverAxis == axis && !IsDraggingThumb ? ScrollbarThumbHover
                : ScrollbarThumb;

            if (axis == Axis.Y)
            {
                batch.AddRect(track, new float2(ScrollbarWidth, length), ScrollbarTrack);
                batch.AddRect(track + new float2(0, thumbOffset), new float2(ScrollbarWidth, thumbLength), thumbColor);
            }
            else
            {
                batch.AddRect(track, new float2(length, ScrollbarWidth), ScrollbarTrack);
                batch.AddRect(track + new float2(thumbOffset, 0), new float2(thumbLength, ScrollbarWidth), thumbColor);
            }
        }
    }

    public override UiElement? HitTest(float2 point)
    {
        if (!Contains(point))
            return null;
        if (ScrollbarAt(point) is { } axis)
        {
            _hoverAxis = axis;
            return this;
        }

        // Children are clipped to the panel, which Contains has already checked, so the rest is Panel's own walk
        // minus the panel itself taking the click.
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            if (child.Visible && child.HitTest(point) is { } hit)
                return hit;
        }
        return null;
    }

    public override void PointerDown(float2 point, UiModifiers modifiers)
    {
        _drag = null;
        if (ScrollbarAt(point) is not { } axis)
            return;

        var (track, _, thumbOffset, thumbLength) = Scrollbar(axis);
        var along = Along(point, axis) - Along(track, axis);
        if (along >= thumbOffset && along < thumbOffset + thumbLength)
        {
            _drag = (axis, along - thumbOffset);
            return;
        }

        // On the track: a page towards the click, keeping a little overlap so the reader keeps their place.
        var page = MathF.Max(ScrollStep, Along(_viewport, axis) - ScrollStep / 2);
        // Paging from the target, so clicking again mid-glide goes a further page rather than a partial one.
        var target = Along(_target, axis) + (along < thumbOffset ? -page : page);
        ScrollTo(WithAxis(_target, axis, target));
    }

    public override void PointerDrag(float2 point)
    {
        if (_drag is not { } drag)
            return;

        var (track, length, _, thumbLength) = Scrollbar(drag.Axis);
        var travel = length - thumbLength;
        if (travel <= 0)
            return;

        var thumbOffset = Math.Clamp(Along(point, drag.Axis) - Along(track, drag.Axis) - drag.Grab, 0, travel);
        ScrollOffset = WithAxis(ScrollOffset, drag.Axis, thumbOffset / travel * Along(MaxScroll, drag.Axis));
    }

    /// <summary>
    /// Scrolls by <paramref name="notches"/> of the wheel on each axis, positive meaning towards the left and top as
    /// GLFW reports them. Returns false if neither axis could move, so the wheel can go to an outer scroll panel.
    /// </summary>
    public bool ScrollByWheel(float2 notches)
    {
        // From the target, so quick notches add up to the full distance instead of each cutting the last one short.
        var target = float2.Clamp(_target - notches * ScrollStep, float2.Zero, MaxScroll);
        if (MathF.Abs(target.X - _target.X) < 0.01f && MathF.Abs(target.Y - _target.Y) < 0.01f)
            return false;
        ScrollTo(target);
        return true;
    }
}
