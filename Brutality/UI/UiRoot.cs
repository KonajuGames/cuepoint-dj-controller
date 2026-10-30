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
    Text,
}

/// <summary>
/// The keys the UI acts on; anything else arrives as <see cref="Other"/>.
/// </summary>
enum UiKey
{
    Other,
    Tab,
    Enter,
    Escape,
    Space,
    Backspace,
    Delete,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    PageUp,
    PageDown,
    A,
    C,
    V,
    X,
}

[Flags]
enum UiModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,

    /// <summary>
    /// Windows key, or Command on macOS.
    /// </summary>
    Super = 8,
}

static class UiModifiersExtensions
{
    /// <summary>
    /// The modifier for shortcuts like paste: Command on macOS, Control elsewhere.
    /// </summary>
    public static bool HasShortcutModifier(this UiModifiers modifiers) =>
        (modifiers & (OperatingSystem.IsMacOS() ? UiModifiers.Super : UiModifiers.Control)) != 0;

    /// <summary>
    /// The modifier for moving or deleting a word at a time: Option on macOS, Control elsewhere.
    /// </summary>
    public static bool HasWordModifier(this UiModifiers modifiers) =>
        (modifiers & (OperatingSystem.IsMacOS() ? UiModifiers.Alt : UiModifiers.Control)) != 0;
}

/// <summary>
/// The system clipboard, for text boxes to paste from. The window layer fills these in; without it, paste does
/// nothing.
/// </summary>
static class UiClipboard
{
    public static Func<string?> GetText { get; set; } = () => null;
    public static Action<string> SetText { get; set; } = _ => { };
}

/// <summary>
/// The top-level elements on screen, plus the input state that ties them together: lays them out, works out what's
/// under the cursor, turns presses and releases into clicks, and routes keys and typing to the focused element.
/// Platform-neutral; see <see cref="GlfwUiInput"/> for feeding it a window's events.
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

    public UiCursor Cursor => Hovered?.Cursor ?? UiCursor.Arrow;

    /// <summary>
    /// The element keys and typed characters go to, if any.
    /// </summary>
    public UiElement? Focused { get; private set; }

    /// <summary>
    /// The focused element takes every key (a text box being typed in), so the app's shortcuts should stay quiet.
    /// </summary>
    public bool WantsKeyboard => Focused is { CapturesKeyboard: true };

    float2? _mouse;

    /// <summary>
    /// Whether focus last moved by keyboard, which is when focus rings show.
    /// </summary>
    bool _focusVisible;

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

        // Focus can't stay on something that's been hidden or disabled since.
        if (Focused is not null && !FocusableElements().Contains(Focused))
            Focus(null);

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
            // Clicking moves focus to what was clicked, or clears it when that can't take focus.
            _focusVisible = false;
            Focus(Hovered is { IsFocusable: true } ? Hovered : null);

            if (Hovered is null)
                return;
            Pressed = Hovered;
            Pressed.IsPressed = true;
            if (_mouse is { } mouse)
                Pressed.PointerDown(mouse);
            return;
        }

        if (Pressed is not { } pressed)
            return;

        Pressed = null;
        pressed.IsPressed = false;
        if (pressed == Hovered)
            pressed.RaiseClicked();
    }

    /// <summary>
    /// Gives <paramref name="element"/> keyboard focus, or clears focus with null. Focus set this way shows a ring
    /// only if <paramref name="showFocusRing"/>, as when moving by keyboard.
    /// </summary>
    public void Focus(UiElement? element, bool showFocusRing = false)
    {
        if (showFocusRing)
            _focusVisible = true;

        if (Focused is not null)
            Focused.IsFocused = Focused.ShowFocus = false;
        Focused = element;
        if (Focused is not null)
        {
            Focused.IsFocused = true;
            Focused.ShowFocus = _focusVisible;
        }
    }

    /// <summary>
    /// A key went down or auto-repeated. Tab and Shift+Tab move focus; other keys go to the focused element, and
    /// Escape clears focus if that element doesn't use it. Returns whether the UI used the key; when it didn't, the
    /// app is free to.
    /// </summary>
    public bool KeyDown(UiKey key, UiModifiers modifiers)
    {
        if (key == UiKey.Tab)
            return MoveFocus(backwards: (modifiers & UiModifiers.Shift) != 0);

        if (Focused is not { } focused)
            return false;
        if (focused.KeyDown(key, modifiers))
            return true;
        if (key == UiKey.Escape)
        {
            Focus(null);
            return true;
        }

        // A text box swallows everything else too, so typing "q" doesn't quit, say.
        return focused.CapturesKeyboard;
    }

    /// <summary>
    /// A character was typed. Returns whether the focused element took it.
    /// </summary>
    public bool TextInput(System.Text.Rune character)
    {
        if (Focused is not { CapturesKeyboard: true } focused)
            return false;
        focused.TextInput(character);
        return true;
    }

    /// <summary>
    /// Moves focus to the next (or previous) focusable element in tree order, wrapping around. Returns false if
    /// there's nothing focusable.
    /// </summary>
    bool MoveFocus(bool backwards)
    {
        var focusable = FocusableElements().ToList();
        if (focusable.Count == 0)
            return false;

        var current = Focused is null ? -1 : focusable.IndexOf(Focused);
        var next =
            current < 0 ? (backwards ? focusable.Count - 1 : 0)
            : backwards ? (current - 1 + focusable.Count) % focusable.Count
            : (current + 1) % focusable.Count;

        Focus(focusable[next], showFocusRing: true);
        return true;
    }

    /// <summary>
    /// Every visible element that can take focus, depth first in drawing order, which is also Tab order.
    /// </summary>
    IEnumerable<UiElement> FocusableElements()
    {
        var stack = new Stack<UiElement>();
        for (var i = Elements.Count - 1; i >= 0; i--)
        {
            if (Elements[i].Visible)
                stack.Push(Elements[i]);
        }

        while (stack.TryPop(out var element))
        {
            if (element.IsFocusable)
                yield return element;
            foreach (var child in element.VisibleChildren.Reverse())
                stack.Push(child);
        }
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
