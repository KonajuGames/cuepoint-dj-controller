using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// A vertical panel that grows to fit its children up to <see cref="MaxHeight"/>, then clips them and scrolls with
/// the mouse wheel. A scrollbar shows on the right while there's more content than fits.
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
    public float4 ScrollbarTrack { get; set; } = UiColor.Hex(0xFFFFFF, 0.06f);
    public float4 ScrollbarThumb { get; set; } = UiColor.Hex(0xFFFFFF, 0.3f);

    /// <summary>
    /// How far the content is scrolled up, in pixels; kept within <see cref="MaxScroll"/> by layout.
    /// </summary>
    public float ScrollOffset { get; set; }

    /// <summary>
    /// The scroll offset that shows the end of the content, or 0 if it all fits.
    /// </summary>
    public float MaxScroll => MathF.Max(0, _contentHeight - ViewportHeight);

    public bool CanScroll => MaxScroll > 0;

    float _contentHeight;

    /// <summary>
    /// Gap between the content and the scrollbar.
    /// </summary>
    float Gutter => CanScroll ? ScrollbarWidth + Padding : 0;

    float ViewportHeight => MathF.Max(0, Size.Y - 2 * Inset);

    public ScrollPanel() => Direction = UiDirection.Vertical;

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

        ScrollOffset = StickToEnd && wasAtEnd ? MaxScroll : Math.Clamp(ScrollOffset, 0, MaxScroll);
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

        var trackPosition = new float2(
            ScreenPosition.X + Size.X - Inset - ScrollbarWidth,
            ScreenPosition.Y + Inset
        );
        var trackHeight = ViewportHeight;
        batch.AddRect(trackPosition, new float2(ScrollbarWidth, trackHeight), ScrollbarTrack);

        var thumbHeight = MathF.Max(MathF.Min(16, trackHeight), trackHeight * ViewportHeight / _contentHeight);
        var thumbY = (trackHeight - thumbHeight) * (ScrollOffset / MaxScroll);
        batch.AddRect(trackPosition + new float2(0, thumbY), new float2(ScrollbarWidth, thumbHeight), ScrollbarThumb);
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
