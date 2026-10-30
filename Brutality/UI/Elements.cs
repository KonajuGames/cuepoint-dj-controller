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
/// works out sizes from the bottom up, then <see cref="Arrange"/> places elements from the top down. Drawing and
/// mouse hit testing both use the resulting <see cref="ScreenPosition"/> and <see cref="Size"/>.
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
    /// Hidden elements take no space, draw nothing and ignore the mouse.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// The size from the last <see cref="Measure"/>.
    /// </summary>
    public float2 Size { get; protected set; }

    /// <summary>
    /// The top-left corner in screen pixels from the last <see cref="Arrange"/>.
    /// </summary>
    public float2 ScreenPosition { get; private set; }

    /// <summary>
    /// The mouse is over this element, and nothing interactive inside it is in the way.
    /// </summary>
    public bool IsHovered { get; internal set; }

    /// <summary>
    /// The left button went down on this element and hasn't been released yet.
    /// </summary>
    public bool IsPressed { get; internal set; }

    /// <summary>
    /// Whether this element takes mouse input. Elements that don't are transparent to it, so the mouse reaches
    /// whatever is behind or around them.
    /// </summary>
    public virtual bool IsInteractive => false;

    /// <summary>
    /// The left button was pressed and released over this element.
    /// </summary>
    public event Action<UiElement>? Clicked;

    internal void RaiseClicked() => Clicked?.Invoke(this);

    public bool Contains(float2 point) =>
        point.X >= ScreenPosition.X
        && point.Y >= ScreenPosition.Y
        && point.X < ScreenPosition.X + Size.X
        && point.Y < ScreenPosition.Y + Size.Y;

    /// <summary>
    /// Works out and stores <see cref="Size"/>, measuring any children first.
    /// </summary>
    public abstract float2 Measure();

    /// <summary>
    /// Places the element with its top-left at <paramref name="position"/>, after <see cref="Measure"/>.
    /// </summary>
    public virtual void Arrange(float2 position) => ArrangeSelf(position);

    /// <summary>
    /// Sets <see cref="ScreenPosition"/> alone, for overrides that place their children differently.
    /// </summary>
    protected void ArrangeSelf(float2 position) => ScreenPosition = position;

    public abstract void Draw(UiBatch batch);

    /// <summary>
    /// The innermost interactive element under <paramref name="point"/>, or null.
    /// </summary>
    public virtual UiElement? HitTest(float2 point) => IsInteractive && Contains(point) ? this : null;
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

    public override void Draw(UiBatch batch) => batch.AddText(Font, Text, ScreenPosition, Color);
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
class Panel : UiElement
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
    /// The background to draw right now; subclasses change it with their state.
    /// </summary>
    protected virtual float4 CurrentBackground => Background;

    /// <summary>
    /// Adds a child and returns it, so a tree can be built and kept hold of in one go.
    /// </summary>
    public T Add<T>(T child)
        where T : UiElement
    {
        Children.Add(child);
        return child;
    }

    /// <summary>
    /// Space taken by the border and padding on each side.
    /// </summary>
    protected float Inset => Padding + BorderWidth;

    public override float2 Measure() =>
        Size = float2.Max(MeasureChildren() + new float2(2 * Inset, 2 * Inset), MinSize);

    /// <summary>
    /// Measures the visible children and returns the size of them stacked, without padding or border.
    /// </summary>
    protected float2 MeasureChildren()
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
        return content + (Direction == UiDirection.Vertical ? new float2(0, gaps) : new float2(gaps, 0));
    }

    public override void Arrange(float2 position)
    {
        base.Arrange(position);
        ArrangeChildren(position + new float2(Inset, Inset));
    }

    /// <summary>
    /// Stacks the visible children starting at <paramref name="origin"/>.
    /// </summary>
    protected void ArrangeChildren(float2 origin)
    {
        var cursor = origin;
        foreach (var child in Children)
        {
            if (!child.Visible)
                continue;

            child.Arrange(cursor);
            cursor += Direction == UiDirection.Vertical
                ? new float2(0, child.Size.Y + Spacing)
                : new float2(child.Size.X + Spacing, 0);
        }
    }

    public override void Draw(UiBatch batch)
    {
        DrawFrame(batch);
        DrawChildren(batch);
    }

    /// <summary>
    /// The background and border.
    /// </summary>
    protected void DrawFrame(UiBatch batch)
    {
        batch.AddRect(ScreenPosition, Size, CurrentBackground);
        batch.AddBorder(ScreenPosition, Size, BorderWidth, BorderColor);
    }

    protected void DrawChildren(UiBatch batch)
    {
        foreach (var child in Children)
        {
            if (child.Visible)
                child.Draw(batch);
        }
    }

    /// <summary>
    /// The visible child under <paramref name="point"/> that's drawn on top, or null.
    /// </summary>
    internal UiElement? ChildAt(float2 point)
    {
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            if (child.Visible && child.Contains(point))
                return child;
        }
        return null;
    }

    public override UiElement? HitTest(float2 point)
    {
        if (!Contains(point))
            return null;

        // Last drawn is on top, so it gets first say.
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            if (child.Visible && child.HitTest(point) is { } hit)
                return hit;
        }

        return IsInteractive ? this : null;
    }
}

/// <summary>
/// A clickable panel holding a label. It shows <see cref="HoverBackground"/> while the mouse is over it and
/// <see cref="PressedBackground"/> while it's held down; subscribe to <see cref="UiElement.Clicked"/>.
/// </summary>
sealed class Button : Panel
{
    readonly Label _label;

    public Button(Font font, string text)
    {
        _label = Add(new Label(font, text));
        Padding = 6;
        Background = UiColor.Hex(0x2A2A35);
        HoverBackground = UiColor.Hex(0x3A3A48);
        PressedBackground = UiColor.Hex(0x1E1E26);
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    public float4 TextColor
    {
        get => _label.Color;
        set => _label.Color = value;
    }

    public float4 HoverBackground { get; set; }
    public float4 PressedBackground { get; set; }

    /// <summary>
    /// A disabled button draws faded and ignores the mouse.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public override bool IsInteractive => Enabled;

    // Pressed only shows while the mouse is still over the button, since releasing elsewhere won't click.
    protected override float4 CurrentBackground =>
        !Enabled ? Background * new float4(1, 1, 1, 0.4f)
        : IsPressed && IsHovered ? PressedBackground
        : IsHovered || IsPressed ? HoverBackground
        : Background;

    public override void Draw(UiBatch batch)
    {
        var color = _label.Color;
        if (!Enabled)
            _label.Color = color * new float4(1, 1, 1, 0.4f);

        base.Draw(batch);
        _label.Color = color;
    }
}
