using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// Where a top-level element sits on screen. Its <see cref="UiElement.Position"/> is then the offset from that corner,
/// measured inwards.
/// </summary>
enum UiAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Something that takes up space and draws itself. Layout runs every frame in two passes: <see cref="Measure"/>
/// works out sizes from the bottom up, then <see cref="Draw"/> places and draws from the top down.
/// </summary>
abstract class UiElement
{
    /// <summary>
    /// For a top-level element, its offset from <see cref="Anchor"/>. Ignored inside a panel, which places its
    /// children itself.
    /// </summary>
    public float2 Position { get; set; }

    public UiAnchor Anchor { get; set; } = UiAnchor.TopLeft;

    /// <summary>
    /// Hidden elements take no space and draw nothing.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// The size from the last <see cref="Measure"/>.
    /// </summary>
    public float2 Size { get; protected set; }

    /// <summary>
    /// Works out and stores <see cref="Size"/>, measuring any children first.
    /// </summary>
    public abstract float2 Measure();

    /// <summary>
    /// Draws the element with its top-left at <paramref name="position"/>, after <see cref="Measure"/>.
    /// </summary>
    public abstract void Draw(UiBatch batch, float2 position);
}

/// <summary>
/// A line (or several, split on '\n') of text in one font and colour.
/// </summary>
sealed class Label(Font font, string text = "") : UiElement
{
    public Font Font { get; set; } = font;
    public string Text { get; set; } = text;
    public float4 Color { get; set; } = UiColor.White;

    public override float2 Measure() => Size = Font.Measure(Text);

    public override void Draw(UiBatch batch, float2 position) => batch.AddText(Font, Text, position, Color);
}

enum UiDirection
{
    Vertical,
    Horizontal,
}

/// <summary>
/// A box with an optional background and border that stacks its children in a row or column. It sizes itself to fit
/// them, but never smaller than <see cref="MinSize"/>.
/// </summary>
sealed class Panel : UiElement
{
    public List<UiElement> Children { get; } = [];

    public UiDirection Direction { get; set; } = UiDirection.Vertical;

    /// <summary>
    /// Space between the border and the children, on every side.
    /// </summary>
    public float Padding { get; set; }

    /// <summary>
    /// Space between one child and the next.
    /// </summary>
    public float Spacing { get; set; }

    public float2 MinSize { get; set; }

    public float4 Background { get; set; } = UiColor.Transparent;
    public float4 BorderColor { get; set; } = UiColor.Transparent;
    public float BorderWidth { get; set; }

    /// <summary>
    /// Adds a child and returns it, so a tree can be built and kept hold of in one go.
    /// </summary>
    public T Add<T>(T child)
        where T : UiElement
    {
        Children.Add(child);
        return child;
    }

    public override float2 Measure()
    {
        var content = float2.Zero;
        var count = 0;
        foreach (var child in Children)
        {
            if (!child.Visible)
                continue;

            var size = child.Measure();
            content =
                Direction == UiDirection.Vertical
                    ? new float2(MathF.Max(content.X, size.X), content.Y + size.Y)
                    : new float2(content.X + size.X, MathF.Max(content.Y, size.Y));
            count++;
        }

        var gaps = Math.Max(0, count - 1) * Spacing;
        content += Direction == UiDirection.Vertical ? new float2(0, gaps) : new float2(gaps, 0);

        var inset = 2 * (Padding + BorderWidth);
        return Size = float2.Max(content + new float2(inset, inset), MinSize);
    }

    public override void Draw(UiBatch batch, float2 position)
    {
        batch.AddRect(position, Size, Background);
        batch.AddBorder(position, Size, BorderWidth, BorderColor);

        var inset = Padding + BorderWidth;
        var cursor = position + new float2(inset, inset);
        foreach (var child in Children)
        {
            if (!child.Visible)
                continue;

            child.Draw(batch, cursor);
            cursor += Direction == UiDirection.Vertical
                ? new float2(0, child.Size.Y + Spacing)
                : new float2(child.Size.X + Spacing, 0);
        }
    }
}
