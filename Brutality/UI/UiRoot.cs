using Brutal.Numerics;

namespace Brutality.UI;

enum UiMouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// The cursor the UI wants shown, for the window layer to apply.
/// </summary>
enum UiCursor
{
    Arrow,
    Hand,
}

/// <summary>
/// The top-level elements on screen, plus the mouse state that ties them together: lays them out, works out what's
/// under the cursor, and turns presses and releases into clicks. Platform-neutral; see <see cref="GlfwUiInput"/> for
/// feeding it a window's events.
/// </summary>
sealed class UiRoot
{
    /// <summary>
    /// Drawn first to last, so later elements are on top and win hit tests.
    /// </summary>
    public List<UiElement> Elements { get; } = [];

    /// <summary>
    /// The interactive element under the mouse, if any.
    /// </summary>
    public UiElement? Hovered { get; private set; }

    /// <summary>
    /// The element the left button went down on. It keeps the mouse until the button is released, even if the
    /// cursor leaves it, and only clicks if released back over it.
    /// </summary>
    public UiElement? Pressed { get; private set; }

    /// <summary>
    /// The mouse is over some visible top-level element, interactive or not.
    /// </summary>
    public bool IsMouseOver { get; private set; }

    /// <summary>
    /// The UI is using the mouse, so the rest of the app (a camera, say) should ignore it.
    /// </summary>
    public bool WantsMouse => IsMouseOver || Pressed is not null;

    public UiCursor Cursor => Hovered is not null ? UiCursor.Hand : UiCursor.Arrow;

    float2? _mouse;

    public T Add<T>(T element)
        where T : UiElement
    {
        Elements.Add(element);
        return element;
    }

    /// <summary>
    /// Measures and places every element for a screen of <paramref name="screenSize"/> pixels. Call once a frame,
    /// before drawing, after anything that changes text or visibility.
    /// </summary>
    public void Layout(float2 screenSize)
    {
        foreach (var element in Elements)
        {
            if (!element.Visible)
                continue;

            var size = element.Measure();
            element.Arrange(
                element.Anchor switch
                {
                    UiAnchor.TopRight => new float2(screenSize.X - size.X - element.Position.X, element.Position.Y),
                    UiAnchor.BottomLeft => new float2(element.Position.X, screenSize.Y - size.Y - element.Position.Y),
                    UiAnchor.BottomRight => screenSize - size - element.Position,
                    _ => element.Position,
                }
            );
        }

        // Things may have moved or resized under a cursor that hasn't.
        UpdateHover();
    }

    public void Draw(UiBatch batch)
    {
        foreach (var element in Elements)
        {
            if (element.Visible)
                element.Draw(batch);
        }
    }

    /// <summary>
    /// The mouse moved to <paramref name="position"/>, in the same pixels as the layout.
    /// </summary>
    public void MouseMove(float2 position)
    {
        _mouse = position;
        UpdateHover();
    }

    /// <summary>
    /// The mouse left the window. A held press stays captured, so the release is still seen.
    /// </summary>
    public void MouseLeave()
    {
        _mouse = null;
        UpdateHover();
    }

    /// <summary>
    /// The wheel turned by <paramref name="notches"/>, positive towards the top. Scrolls the innermost scroll panel
    /// under the cursor; once that's at its limit, the next one out. Returns whether anything scrolled.
    /// </summary>
    public bool MouseWheel(float notches)
    {
        if (_mouse is not { } mouse || notches == 0)
            return false;

        // The path from the topmost element under the cursor down to the deepest one.
        var scrollers = new List<ScrollPanel>();
        for (var i = Elements.Count - 1; i >= 0; i--)
        {
            var element = Elements[i];
            if (!element.Visible || !element.Contains(mouse))
                continue;

            for (UiElement? current = element; current is not null; current = (current as Panel)?.ChildAt(mouse))
            {
                if (current is ScrollPanel scroller)
                    scrollers.Add(scroller);
            }
            break;
        }

        for (var i = scrollers.Count - 1; i >= 0; i--)
        {
            if (scrollers[i].ScrollByWheel(notches))
                return true;
        }
        return false;
    }

    public void MouseButton(UiMouseButton button, bool down)
    {
        if (button != UiMouseButton.Left)
            return;

        if (down)
        {
            if (Hovered is null)
                return;
            Pressed = Hovered;
            Pressed.IsPressed = true;
            return;
        }

        if (Pressed is not { } pressed)
            return;

        Pressed = null;
        pressed.IsPressed = false;
        if (pressed == Hovered)
            pressed.RaiseClicked();
    }

    void UpdateHover()
    {
        UiElement? hit = null;
        var over = false;
        if (_mouse is { } mouse)
        {
            for (var i = Elements.Count - 1; i >= 0 && hit is null; i--)
            {
                var element = Elements[i];
                if (!element.Visible || !element.Contains(mouse))
                    continue;
                over = true;
                hit = element.HitTest(mouse);
            }
        }

        IsMouseOver = over;

        // Something that was pressed and then disabled or hidden can't be clicked any more.
        if (Pressed is { IsInteractive: false } or { Visible: false })
        {
            Pressed.IsPressed = false;
            Pressed = null;
        }

        if (hit == Hovered)
            return;
        if (Hovered is not null)
            Hovered.IsHovered = false;
        Hovered = hit;
        if (Hovered is not null)
            Hovered.IsHovered = true;
    }
}
