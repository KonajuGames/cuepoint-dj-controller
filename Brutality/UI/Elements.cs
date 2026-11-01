using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// Where a top-level element sits on screen. Its <see cref="Element.Position"/> is then the offset from that corner,
/// measured inwards.
/// </summary>
enum Anchor
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
abstract class Element
{
    /// <summary>
    /// For a top-level element, its offset from <see cref="Anchor"/>. Ignored inside a panel, which places its
    /// children itself.
    /// </summary>
    public float2 Position { get; set; }

    public Anchor Anchor { get; set; } = Anchor.TopLeft;

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
    /// The cursor shown while the mouse is over this element, if it's interactive.
    /// </summary>
    public virtual Cursor Cursor => Cursor.Hand;

    /// <summary>
    /// Whether this element can take keyboard focus, by clicking it or with Tab.
    /// </summary>
    public virtual bool IsFocusable => false;

    /// <summary>
    /// Whether, while focused, this element takes every key, so the app's own shortcuts don't fire as you type.
    /// </summary>
    public virtual bool CapturesKeyboard => false;

    /// <summary>
    /// Whether clicking this element leaves keyboard focus where it was, rather than clearing it. For controls like
    /// scrollbars that help you look around without being somewhere to type.
    /// </summary>
    public virtual bool KeepsFocusOnClick => false;

    public bool IsFocused { get; internal set; }

    /// <summary>
    /// Focused, and the focus got here by keyboard, so a focus ring should show. Clicking focuses without one.
    /// </summary>
    public bool ShowFocus { get; internal set; }

    /// <summary>
    /// The left button was pressed and released over this element, or the keyboard activated it.
    /// </summary>
    public event Action<Element>? Clicked;

    /// <summary>
    /// The left button went down on this element, or the keyboard activated it. Always followed by
    /// <see cref="PressEnded"/>, for controls that act while held, such as a deck's cue button.
    /// </summary>
    public event Action<Element>? PressStarted;

    /// <summary>
    /// The press from <see cref="PressStarted"/> is over: the button came up (wherever the mouse is, unlike
    /// <see cref="Clicked"/>), or the element was disabled or hidden while held.
    /// </summary>
    public event Action<Element>? PressEnded;

    internal void RaiseClicked() => Clicked?.Invoke(this);
    internal void RaisePressStarted() => PressStarted?.Invoke(this);
    internal void RaisePressEnded() => PressEnded?.Invoke(this);

    /// <summary>
    /// A key went down (or repeated) while this element had focus. Returns whether it was used.
    /// </summary>
    public virtual bool KeyDown(Key key, Modifiers modifiers) => false;

    /// <summary>
    /// A character was typed while this element had focus.
    /// </summary>
    public virtual void TextInput(System.Text.Rune character) { }

    /// <summary>
    /// The left button went down at <paramref name="point"/>, over this element.
    /// </summary>
    public virtual void PointerDown(float2 point, Modifiers modifiers) { }

    /// <summary>
    /// A drag starting from <paramref name="point"/>, where the press began, if this element has something there to
    /// drag; null if not. Asked once the press has moved far enough to count as a drag.
    /// </summary>
    public virtual DragData? StartDrag(float2 point) => null;

    /// <summary>
    /// Whether this element would take <paramref name="payload"/> dropped on it at <paramref name="point"/>. Asked
    /// as the drag moves, so an element can say where in it the drop would land.
    /// </summary>
    public virtual bool CanDrop(object payload, float2 point) => false;

    /// <summary>
    /// <paramref name="payload"/> was dropped on this element at <paramref name="point"/>, where it said it would
    /// take it.
    /// </summary>
    public virtual void Drop(object payload, float2 point) { }

    /// <summary>
    /// Something it would take is being dragged over it, for it to show it's a place to drop.
    /// </summary>
    public bool IsDropTarget { get; internal set; }

    /// <summary>
    /// The right button went down over this element, at <paramref name="point"/>, for a secondary action such as
    /// clearing it. Doesn't press or focus it.
    /// </summary>
    public virtual void SecondaryClick(float2 point) { }

    /// <summary>
    /// The mouse moved to <paramref name="point"/> while the left button, pressed on this element, is still held.
    /// The point can be outside the element.
    /// </summary>
    public virtual void PointerDrag(float2 point) { }

    /// <summary>
    /// The mouse moved to <paramref name="point"/> while over this element (it's <see cref="Root.Hovered"/>), for
    /// elements that highlight a part of themselves, such as a list's rows.
    /// </summary>
    public virtual void PointerMove(float2 point) { }

    /// <summary>
    /// This element just took focus, by keyboard (Tab) if <paramref name="byKeyboard"/>, otherwise by a click or
    /// code.
    /// </summary>
    public virtual void FocusGained(bool byKeyboard) { }

    /// <summary>
    /// The visible children, in drawing order, for walking the tree.
    /// </summary>
    internal virtual IEnumerable<Element> VisibleChildren => [];

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

    public abstract void Draw(Batch batch);

    /// <summary>
    /// The innermost interactive element under <paramref name="point"/>, or null.
    /// </summary>
    public virtual Element? HitTest(float2 point) => IsInteractive && Contains(point) ? this : null;
}

/// <summary>
/// A line (or several, split on '\n') of text in one font and colour.
/// </summary>
sealed class Label(Font font, string text = "") : Element
{
    public Font Font { get; set; } = font;
    public string Text { get; set; } = text;
    public float4 Color { get; set; } = Colors.White;

    /// <summary>
    /// The widest it takes up; longer text is cut off there.
    /// </summary>
    public float MaxWidth { get; set; } = float.PositiveInfinity;

    public override float2 Measure()
    {
        var size = Font.Measure(Text);
        return Size = new float2(MathF.Min(size.X, MaxWidth), size.Y);
    }

    public override void Draw(Batch batch)
    {
        var clipped = Font.Measure(Text).X > Size.X;
        if (clipped)
            batch.PushClip(ScreenPosition, Size);
        batch.AddText(Font, Text, ScreenPosition, Color);
        if (clipped)
            batch.PopClip();
    }
}

enum Direction
{
    Vertical,
    Horizontal,
}

/// <summary>
/// A box with an optional background and border that stacks its children in a row or column. It sizes itself to fit
/// them, but never smaller than <see cref="MinSize"/>.
/// </summary>
class Panel : Element
{
    public List<Element> Children { get; } = [];

    public Direction Direction { get; set; } = Direction.Vertical;

    /// <summary>
    /// Space between the border and the children, on every side.
    /// </summary>
    public float Padding { get; set; }

    /// <summary>
    /// Space between one child and the next.
    /// </summary>
    public float Spacing { get; set; }

    public float2 MinSize { get; set; }

    public float4 Background { get; set; } = Colors.Transparent;
    public float4 BorderColor { get; set; } = Colors.Transparent;
    public float BorderWidth { get; set; }

    /// <summary>
    /// The background to draw right now; subclasses change it with their state.
    /// </summary>
    protected virtual float4 CurrentBackground => Background;

    /// <summary>
    /// Adds a child and returns it, so a tree can be built and kept hold of in one go.
    /// </summary>
    public T Add<T>(T child)
        where T : Element
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
                Direction == Direction.Vertical
                    ? new float2(MathF.Max(content.X, size.X), content.Y + size.Y)
                    : new float2(content.X + size.X, MathF.Max(content.Y, size.Y));
            count++;
        }

        var gaps = Math.Max(0, count - 1) * Spacing;
        return content + (Direction == Direction.Vertical ? new float2(0, gaps) : new float2(gaps, 0));
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
            cursor += Direction == Direction.Vertical
                ? new float2(0, child.Size.Y + Spacing)
                : new float2(child.Size.X + Spacing, 0);
        }
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        DrawChildren(batch);
    }

    /// <summary>
    /// The background and border.
    /// </summary>
    protected void DrawFrame(Batch batch)
    {
        batch.AddRect(ScreenPosition, Size, CurrentBackground);
        batch.AddBorder(ScreenPosition, Size, BorderWidth, BorderColor);
    }

    protected void DrawChildren(Batch batch)
    {
        foreach (var child in Children)
        {
            if (child.Visible)
                child.Draw(batch);
        }
    }

    internal override IEnumerable<Element> VisibleChildren => Children.Where(child => child.Visible);

    /// <summary>
    /// The visible child under <paramref name="point"/> that's drawn on top, or null.
    /// </summary>
    internal Element? ChildAt(float2 point)
    {
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            if (child.Visible && child.Contains(point))
                return child;
        }
        return null;
    }

    public override Element? HitTest(float2 point)
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
/// <see cref="PressedBackground"/> while it's held down; subscribe to <see cref="Element.Clicked"/>.
/// </summary>
sealed class Button : Panel
{
    readonly Label _label;

    public Button(Font font, string text)
    {
        _label = Add(new Label(font, text));
        Padding = 6;
        Background = Colors.Hex(0x2A2A35);
        HoverBackground = Colors.Hex(0x3A3A48);
        PressedBackground = Colors.Hex(0x1E1E26);
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
    /// A picture to show in the middle, tinted the text's colour, for a button with no text (give it a
    /// <see cref="Panel.MinSize"/>); none if null.
    /// </summary>
    public Image? Icon { get; set; }

    /// <summary>
    /// The ring drawn around the button while it has keyboard focus.
    /// </summary>
    public float4 FocusColor { get; set; } = Colors.Hex(0x7AB8FF);

    /// <summary>
    /// A disabled button draws faded and ignores the mouse and keyboard.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The key that presses this button, shown small in its top-right corner in <see cref="HintFont"/>; none if
    /// null.
    /// </summary>
    public string? KeyHint { get; set; }

    public Font? HintFont { get; set; }

    public float4 HintColor { get; set; } = Colors.Hex(0xFFFFFF, 0.3f);

    public override bool IsInteractive => Enabled;

    public override bool IsFocusable => Enabled;

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key is not (Key.Enter or Key.Space))
            return false;

        // A complete press, so anything listening for press and release sees the same as from a mouse click.
        RaisePressStarted();
        RaisePressEnded();
        RaiseClicked();
        return true;
    }

    // Pressed only shows while the mouse is still over the button, since releasing elsewhere won't click.
    protected override float4 CurrentBackground =>
        !Enabled ? Background * new float4(1, 1, 1, 0.4f)
        : IsPressed && IsHovered ? PressedBackground
        : IsHovered || IsPressed ? HoverBackground
        : Background;

    public override void Draw(Batch batch)
    {
        var color = _label.Color;
        if (!Enabled)
            _label.Color = color * new float4(1, 1, 1, 0.4f);

        base.Draw(batch);
        _label.Color = color;

        if (Icon is { } icon)
            batch.AddImage(icon, ScreenPosition + (Size - new float2(icon.Size.X, icon.Size.Y)) / 2, Enabled ? color : color * new float4(1, 1, 1, 0.4f));

        if (KeyHint is { } hint && HintFont is { } hintFont)
        {
            var hintSize = hintFont.Measure(hint);
            var hintAt = new float2(MathF.Round(ScreenPosition.X + Size.X - hintSize.X - 2), ScreenPosition.Y);
            batch.AddText(hintFont, hint, hintAt, Enabled ? HintColor : HintColor * new float4(1, 1, 1, 0.4f));
        }

        if (ShowFocus)
            batch.AddBorder(ScreenPosition, Size, 2, FocusColor);
    }
}
