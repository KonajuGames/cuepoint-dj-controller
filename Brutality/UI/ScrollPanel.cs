using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// A vertical panel that grows to fit its children up to <see cref="MaxHeight"/>, then clips them and scrolls. A
/// scrollbar shows on the right while there's more content than fits: scroll with the mouse wheel, drag the thumb,
/// or click the track above or below it to move a page.
/// </summary>
sealed class ScrollPanel : Panel
{
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

    public float ScrollbarWidth { get; set; } = 6;

    /// <summary>
    /// How wide the strip that takes the mouse is, centred on the scrollbar; wider than the bar so it's easy to grab.
    /// </summary>
    public float ScrollbarHitWidth { get; set; } = 12;

    public float4 ScrollbarTrack { get; set; } = UiColor.Hex(0xFFFFFF, 0.06f);
    public float4 ScrollbarThumb { get; set; } = UiColor.Hex(0xFFFFFF, 0.3f);
    public float4 ScrollbarThumbHover { get; set; } = UiColor.Hex(0xFFFFFF, 0.45f);
    public float4 ScrollbarThumbDragging { get; set; } = UiColor.Hex(0xFFFFFF, 0.6f);

    /// <summary>
    /// How far the content is scrolled up, in pixels; kept within <see cref="MaxScroll"/> by layout.
    /// </summary>
    public float ScrollOffset { get; set; }

    /// <summary>
    /// The scroll offset that shows the end of the content, or 0 if it all fits.
    /// </summary>
    public float MaxScroll => MathF.Max(0, _contentHeight - ViewportHeight);

    public bool CanScroll => MaxScroll > 0;

    /// <summary>
    /// The thumb is being dragged.
    /// </summary>
    public bool IsDraggingThumb => _thumbGrab is not null && IsPressed;

    /// <summary>
    /// Only the scrollbar takes the mouse, and only while there's something to scroll; everywhere else, clicks reach
    /// the children.
    /// </summary>
    public override bool IsInteractive => CanScroll;

    public override UiCursor Cursor => UiCursor.Arrow;

    /// <summary>
    /// Scrolling the log shouldn't take you out of the note box, say.
    /// </summary>
    public override bool KeepsFocusOnClick => true;

    float _contentHeight;

    /// <summary>
    /// While dragging the thumb, how far below its top it was grabbed.
    /// </summary>
    float? _thumbGrab;

    /// <summary>
    /// Gap between the content and the scrollbar.
    /// </summary>
    float Gutter => CanScroll ? ScrollbarWidth + Padding : 0;

    float ViewportHeight => MathF.Max(0, Size.Y - 2 * Inset);

    public ScrollPanel() => Direction = UiDirection.Vertical;

    /// <summary>
    /// The track's top-left corner and height, and the thumb's offset down it and height, in screen pixels.
    /// </summary>
    (float2 TrackPosition, float TrackHeight, float ThumbY, float ThumbHeight) Scrollbar()
    {
        var trackPosition = new float2(ScreenPosition.X + Size.X - Inset - ScrollbarWidth, ScreenPosition.Y + Inset);
        var trackHeight = ViewportHeight;
        var thumbHeight = MathF.Max(MathF.Min(16, trackHeight), trackHeight * ViewportHeight / _contentHeight);
        var thumbY = MaxScroll > 0 ? (trackHeight - thumbHeight) * (ScrollOffset / MaxScroll) : 0;
        return (trackPosition, trackHeight, thumbY, thumbHeight);
    }

    bool OnScrollbar(float2 point)
    {
        if (!CanScroll)
            return false;
        var (track, trackHeight, _, _) = Scrollbar();
        var centre = track.X + ScrollbarWidth / 2;
        var halfWidth = MathF.Max(ScrollbarWidth, ScrollbarHitWidth) / 2;
        return MathF.Abs(point.X - centre) <= halfWidth && point.Y >= track.Y && point.Y < track.Y + trackHeight;
    }

    public override float2 Measure()
    {
        // Decided before this measure changes the extents, so a log that was at its end stays there.
        var wasAtEnd = ScrollOffset >= MaxScroll - 0.5f;

        var content = MeasureChildren();
        _contentHeight = content.Y;

        var height = MathF.Min(content.Y + 2 * Inset, MathF.Max(MaxHeight, 2 * Inset));
        Size = float2.Max(new float2(content.X + 2 * Inset, height), MinSize);

        // The gutter depends on whether it overflows, which needs the height first.
        Size = new float2(MathF.Max(Size.X, content.X + 2 * Inset + Gutter), Size.Y);

        // A drag in progress owns the offset, so new log lines don't yank the thumb out from under the mouse.
        ScrollOffset = StickToEnd && wasAtEnd && !IsDraggingThumb ? MaxScroll : Math.Clamp(ScrollOffset, 0, MaxScroll);
        return Size;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        // Whole pixels keep text sharp mid-scroll.
        ArrangeChildren(position + new float2(Inset, Inset - MathF.Round(ScrollOffset)));
    }

    public override void Draw(UiBatch batch)
    {
        DrawFrame(batch);

        batch.PushClip(ScreenPosition + new float2(BorderWidth, BorderWidth), Size - new float2(2 * BorderWidth, 2 * BorderWidth));
        DrawChildren(batch);
        batch.PopClip();

        if (!CanScroll)
            return;

        var (track, trackHeight, thumbY, thumbHeight) = Scrollbar();
        batch.AddRect(track, new float2(ScrollbarWidth, trackHeight), ScrollbarTrack);

        var thumbColor =
            IsDraggingThumb ? ScrollbarThumbDragging
            : IsHovered ? ScrollbarThumbHover
            : ScrollbarThumb;
        batch.AddRect(track + new float2(0, thumbY), new float2(ScrollbarWidth, thumbHeight), thumbColor);
    }

    public override UiElement? HitTest(float2 point)
    {
        if (!Contains(point))
            return null;
        if (OnScrollbar(point))
            return this;

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
        var (track, trackHeight, thumbY, thumbHeight) = Scrollbar();
        var y = point.Y - track.Y;

        if (y >= thumbY && y < thumbY + thumbHeight)
        {
            _thumbGrab = y - thumbY;
            return;
        }

        // On the track: a page towards the click, keeping a line of overlap so the reader keeps their place.
        _thumbGrab = null;
        var page = MathF.Max(ScrollStep, ViewportHeight - ScrollStep / 2);
        ScrollOffset = Math.Clamp(ScrollOffset + (y < thumbY ? -page : page), 0, MaxScroll);
    }

    public override void PointerDrag(float2 point)
    {
        if (_thumbGrab is not { } grab)
            return;

        var (track, trackHeight, _, thumbHeight) = Scrollbar();
        var travel = trackHeight - thumbHeight;
        if (travel <= 0)
            return;

        var thumbY = Math.Clamp(point.Y - track.Y - grab, 0, travel);
        ScrollOffset = thumbY / travel * MaxScroll;
    }

    /// <summary>
    /// Scrolls by <paramref name="notches"/> of the wheel, positive meaning towards the top as GLFW reports it.
    /// Returns false if already at that end, so the wheel can go to an outer scroll panel instead.
    /// </summary>
    public bool ScrollByWheel(float notches)
    {
        var target = Math.Clamp(ScrollOffset - notches * ScrollStep, 0, MaxScroll);
        if (MathF.Abs(target - ScrollOffset) < 0.01f)
            return false;
        ScrollOffset = target;
        return true;
    }
}
