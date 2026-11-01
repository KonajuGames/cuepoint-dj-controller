using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// The cursor the UI wants shown, for the window layer to apply.
/// </summary>
enum Cursor
{
    Arrow,
    Hand,
    Text,

    /// <summary>
    /// Left and right, as over an edge that can be dragged to resize.
    /// </summary>
    ResizeHorizontal,
}

/// <summary>
/// Something being dragged: what it is, and what to call it on the label that follows the pointer, in which font.
/// </summary>
sealed record DragData(object Payload, string Label, Font Font);

/// <summary>
/// The system clipboard, for text boxes to paste from. The window layer fills these in; without it, paste does
/// nothing.
/// </summary>
static class Clipboard
{
    public static Func<string?> GetText { get; set; } = () => null;
    public static Action<string> SetText { get; set; } = _ => { };
}

/// <summary>
/// The time animations run on, in seconds from an arbitrary start. Replace it to drive them from a game clock, or
/// step them exactly in tests.
/// </summary>
static class Clock
{
    public static Func<double> Seconds { get; set; } =
        () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Seconds since the last call with the same <paramref name="lastStep"/>, which starts as NaN: 0 on the first
    /// call, and at most a tenth of a second, so a long stall (a minimised window, a breakpoint) finishes an
    /// animation rather than overshooting it.
    /// </summary>
    public static double Delta(ref double lastStep)
    {
        var now = Seconds();
        var dt = double.IsNaN(lastStep) ? 0 : Math.Clamp(now - lastStep, 0, 0.1);
        lastStep = now;
        return dt;
    }

    /// <summary>
    /// Moves <paramref name="current"/> towards <paramref name="target"/> over <paramref name="dt"/> seconds, easing
    /// out: every step closes the same fraction of what's left, so a move starts quickly and settles gently, and a
    /// target that changes mid-move just carries on from wherever it is. <paramref name="timeConstant"/> is the time
    /// to close about two thirds of the gap; within half a pixel it arrives.
    /// </summary>
    public static float Approach(float current, float target, double dt, float timeConstant)
    {
        if (timeConstant > 0)
            current = target + (current - target) * (float)Math.Exp(-dt / timeConstant);
        else
            current = target;
        return MathF.Abs(current - target) < 0.5f ? target : current;
    }
}

/// <summary>
/// The top-level elements on screen, plus the input state that ties them together: lays them out, works out what's
/// under the cursor, turns presses and releases into clicks, and routes keys and typing to the focused element.
/// Platform-neutral; see <see cref="GlfwUiInput"/> for feeding it a window's events.
/// </summary>
sealed class Root
{
    /// <summary>
    /// Drawn first to last, so later elements are on top and win hit tests.
    /// </summary>
    public List<Element> Elements { get; } = [];

    /// <summary>
    /// The interactive element under the mouse, if any.
    /// </summary>
    public Element? Hovered { get; private set; }

    /// <summary>
    /// The element the left button went down on. It keeps the mouse until the button is released, even if the
    /// cursor leaves it, and only clicks if released back over it.
    /// </summary>
    public Element? Pressed { get; private set; }

    /// <summary>
    /// What's being dragged, while a drag is under way: from when a press moves <see cref="DRAG_DISTANCE"/> on an
    /// element with something to drag, until the button comes up (dropping it on <see cref="DropTarget"/>, if
    /// anything) or Escape cancels it.
    /// </summary>
    public DragData? Dragging { get; private set; }

    /// <summary>
    /// The element the drag is over that would take it, if any.
    /// </summary>
    public Element? DropTarget { get; private set; }

    /// <summary>
    /// How far a press has to move, in pixels, to become a drag rather than a click.
    /// </summary>
    public const float DRAG_DISTANCE = 6;

    float2 _pressPoint;
    bool _dragTried;

    /// <summary>
    /// The mouse is over some visible top-level element, interactive or not.
    /// </summary>
    public bool IsMouseOver { get; private set; }

    /// <summary>
    /// The UI is using the mouse, so the rest of the app (a camera, say) should ignore it.
    /// </summary>
    public bool WantsMouse => IsMouseOver || Pressed is not null;

    public Cursor Cursor => Hovered?.Cursor ?? Cursor.Arrow;

    /// <summary>
    /// The element keys and typed characters go to, if any.
    /// </summary>
    public Element? Focused { get; private set; }

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
        where T : Element
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
                    Anchor.TopRight => new float2(screenSize.X - size.X - element.Position.X, element.Position.Y),
                    Anchor.BottomLeft => new float2(element.Position.X, screenSize.Y - size.Y - element.Position.Y),
                    Anchor.BottomRight => screenSize - size - element.Position,
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

    public void Draw(Batch batch)
    {
        foreach (var element in Elements)
        {
            if (element.Visible)
                element.Draw(batch);
        }

        // What's being dragged, on a label beside the pointer, over everything.
        if (Dragging is { } drag && _mouse is { } mouse)
        {
            var size = drag.Font.Measure(drag.Label) + new float2(12, 8);
            var at = mouse + new float2(16, 14);
            batch.AddRect(at, size, Colors.Hex(0x23242C, 0.95f));
            batch.AddBorder(at, size, 1, Colors.Hex(0xFFFFFF, 0.25f));
            batch.AddText(drag.Font, drag.Label, at + new float2(6, 4), Colors.White);
        }
    }

    /// <summary>
    /// The mouse moved to <paramref name="position"/>, in the same pixels as the layout.
    /// </summary>
    public void MouseMove(float2 position)
    {
        _mouse = position;
        UpdateHover();

        if (Dragging is not null)
        {
            UpdateDropTarget();
            return;
        }

        // A press that's moved far enough becomes a drag, if what was pressed has something to drag from there. It
        // won't click when it comes up.
        if (Pressed is { } pressed && !_dragTried && Distance(position, _pressPoint) >= DRAG_DISTANCE)
        {
            _dragTried = true;
            if (pressed.StartDrag(_pressPoint) is { } drag)
            {
                Pressed = null;
                pressed.IsPressed = false;
                pressed.RaisePressEnded();
                Dragging = drag;
                UpdateDropTarget();
                return;
            }
        }

        // The pressed element follows the mouse until release, even outside itself (to drag a text selection, say).
        Pressed?.PointerDrag(position);
        Hovered?.PointerMove(position);
    }

    static float Distance(float2 a, float2 b)
    {
        var d = a - b;
        return MathF.Sqrt(d.X * d.X + d.Y * d.Y);
    }

    /// <summary>
    /// Ends a drag, dropping it on the target if <paramref name="drop"/> and there is one.
    /// </summary>
    void EndDrag(bool drop)
    {
        var (payload, target) = (Dragging?.Payload, DropTarget);
        Dragging = null;
        SetDropTarget(null);
        if (drop && payload is not null && target is not null && _mouse is { } at)
            target.Drop(payload, at);
    }

    void UpdateDropTarget() =>
        SetDropTarget(Dragging is { } drag && _mouse is { } mouse ? DropTargetAt(mouse, drag.Payload) : null);

    void SetDropTarget(Element? target)
    {
        if (target == DropTarget)
            return;
        if (DropTarget is not null)
            DropTarget.IsDropTarget = false;
        DropTarget = target;
        if (target is not null)
            target.IsDropTarget = true;
    }

    /// <summary>
    /// The innermost visible element under <paramref name="point"/> that would take <paramref name="payload"/>,
    /// whether or not it's otherwise interactive; the one drawn on top if they overlap.
    /// </summary>
    Element? DropTargetAt(float2 point, object payload)
    {
        Element? Search(Element element)
        {
            if (!element.Visible || !element.Contains(point))
                return null;
            foreach (var child in element.VisibleChildren.Reverse())
            {
                if (Search(child) is { } inner)
                    return inner;
            }
            return element.CanDrop(payload, point) ? element : null;
        }

        for (var i = Elements.Count - 1; i >= 0; i--)
        {
            if (Search(Elements[i]) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// The mouse left the window. A held press stays captured, so the release is still seen.
    /// </summary>
    public void MouseLeave()
    {
        _mouse = null;
        UpdateHover();
        if (Dragging is not null)
            UpdateDropTarget();
    }

    /// <summary>
    /// A vertical wheel turned by <paramref name="notches"/>, positive towards the top.
    /// </summary>
    public bool MouseWheel(float notches) => MouseWheel(new float2(0, notches));

    /// <summary>
    /// The wheel moved by <paramref name="notches"/> on each axis, positive towards the left and top. Scrolls the
    /// innermost scroll panel under the cursor that can move that way; once that's at its limit, the next one out.
    /// Returns whether anything scrolled.
    /// </summary>
    public bool MouseWheel(float2 notches)
    {
        if (_mouse is not { } mouse || (notches.X == 0 && notches.Y == 0))
            return false;

        // The path from the topmost element under the cursor down to the deepest one.
        var scrollers = new List<ScrollPanel>();
        for (var i = Elements.Count - 1; i >= 0; i--)
        {
            var element = Elements[i];
            if (!element.Visible || !element.Contains(mouse))
                continue;

            for (Element? current = element; current is not null; current = (current as Panel)?.ChildAt(mouse))
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

    public void MouseButton(MouseButton button, bool down, Modifiers modifiers = Modifiers.None)
    {
        // A right-click goes to what's under the mouse as it goes down, and does nothing else: no focus, no press.
        // Qualified, since inside this class the bare name means this method.
        if (button == Input.MouseButton.Right)
        {
            if (down && Hovered is { } target && _mouse is { } at)
                target.SecondaryClick(at);
            return;
        }
        if (button != Input.MouseButton.Left)
            return;

        if (down)
        {
            // Clicking moves focus to what was clicked, or clears it when that can't take focus, except on controls
            // like scrollbars that leave focus (and its ring) where it was.
            if (Hovered is not { KeepsFocusOnClick: true })
            {
                _focusVisible = false;
                Focus(Hovered is { IsFocusable: true } ? Hovered : null);
            }

            if (Hovered is null)
                return;
            _pressPoint = _mouse ?? default;
            _dragTried = false;
            Pressed = Hovered;
            Pressed.IsPressed = true;
            if (_mouse is { } mouse)
                Pressed.PointerDown(mouse, modifiers);
            Pressed.RaisePressStarted();
            return;
        }

        if (Dragging is not null)
        {
            EndDrag(drop: true);
            return;
        }

        if (Pressed is not { } pressed)
            return;

        Pressed = null;
        pressed.IsPressed = false;
        pressed.RaisePressEnded();
        if (pressed == Hovered)
            pressed.RaiseClicked();
    }

    /// <summary>
    /// Gives <paramref name="element"/> keyboard focus, or clears focus with null. Focus set this way shows a ring
    /// only if <paramref name="showFocusRing"/>, as when moving by keyboard.
    /// </summary>
    public void Focus(Element? element, bool showFocusRing = false)
    {
        if (showFocusRing)
            _focusVisible = true;

        var previous = Focused;
        if (previous is not null)
            previous.IsFocused = previous.ShowFocus = false;
        Focused = element;
        if (Focused is null)
            return;

        Focused.IsFocused = true;
        Focused.ShowFocus = _focusVisible;
        if (Focused != previous)
            Focused.FocusGained(showFocusRing);
    }

    /// <summary>
    /// A key went down or auto-repeated. Tab and Shift+Tab move focus; other keys go to the focused element, and
    /// Escape clears focus if that element doesn't use it. Returns whether the UI used the key; when it didn't, the
    /// app is free to.
    /// </summary>
    public bool KeyDown(Key key, Modifiers modifiers)
    {
        if (Dragging is not null && key == Key.Escape)
        {
            EndDrag(drop: false);
            return true;
        }

        if (key == Key.Tab)
            return MoveFocus(backwards: (modifiers & Modifiers.Shift) != 0);

        if (Focused is not { } focused)
            return false;
        if (focused.KeyDown(key, modifiers))
            return true;
        if (key == Key.Escape)
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
    IEnumerable<Element> FocusableElements()
    {
        var stack = new Stack<Element>();
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
        Element? hit = null;
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
            // Still ends the press, so a held control (a cue preview, say) never gets stuck down.
            var cancelled = Pressed;
            Pressed = null;
            cancelled.IsPressed = false;
            cancelled.RaisePressEnded();
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
