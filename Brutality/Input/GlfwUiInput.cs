using System.Text;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutality.UI;

namespace Brutality.Input;

/// <summary>
/// Feeds a GLFW window's mouse, keyboard and text events into a <see cref="Root"/>, shows the cursor it asks for
/// and connects <see cref="Clipboard"/> to the system clipboard. Dispose before the window.
/// </summary>
sealed class GlfwUiInput : IDisposable
{
    readonly GlfwWindow _window;
    readonly Root _ui;
    readonly GlfwCursor _handCursor;
    readonly GlfwCursor _textCursor;
    Cursor _shownCursor = Cursor.Arrow;
    readonly HashSet<GlfwKey> _keysHeldByUi = [];

    /// <summary>
    /// A key event the UI didn't use, releases included, for the app's own shortcuts. Subscribe to this instead of
    /// the window's OnKey, so typing in a text box doesn't also trigger them.
    /// </summary>
    public event Action<GlfwKey, GlfwKeyAction, GlfwModifier>? UnhandledKey;

    public GlfwUiInput(GlfwWindow window, Root ui)
    {
        _window = window;
        _ui = ui;
        _handCursor = Glfw.CreateCursor(GlfwStandardCursor.PointingHand);
        _textCursor = Glfw.CreateCursor(GlfwStandardCursor.IBeam);

        Clipboard.GetText = () => Glfw.Clipboard;
        Clipboard.SetText = text => Glfw.Clipboard = text;

        window.OnCursorPos += OnCursorPos;
        window.OnCursorEnter += OnCursorEnter;
        window.OnMouseButton += OnMouseButton;
        window.OnScroll += OnScroll;
        window.OnKey += OnKey;
        window.OnChar += OnChar;
    }

    /// <summary>
    /// GLFW reports the cursor in window coordinates, which differ from framebuffer pixels on high-DPI displays.
    /// </summary>
    float2 ToFramebuffer(double2 windowPosition)
    {
        var windowSize = _window.Size;
        var framebufferSize = _window.FramebufferSize;
        var scale =
            windowSize.X > 0 && windowSize.Y > 0
                ? new float2((float)framebufferSize.X / windowSize.X, (float)framebufferSize.Y / windowSize.Y)
                : new float2(1, 1);
        return new float2((float)windowPosition.X, (float)windowPosition.Y) * scale;
    }

    void OnCursorPos(GlfwWindow window, double2 position)
    {
        _ui.MouseMove(ToFramebuffer(position));
        UpdateCursor();
    }

    void OnCursorEnter(GlfwWindow window, bool entered)
    {
        if (entered)
            _ui.MouseMove(ToFramebuffer(window.CursorPos));
        else
            _ui.MouseLeave();
        UpdateCursor();
    }

    void OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
    {
        MouseButton? uiButton = button switch
        {
            GlfwMouseButton.Left => MouseButton.Left,
            GlfwMouseButton.Right => MouseButton.Right,
            GlfwMouseButton.Middle => MouseButton.Middle,
            _ => null,
        };
        if (uiButton is { } b)
            _ui.MouseButton(b, action == GlfwButtonAction.Press, ToModifiers(mods));
        UpdateCursor();
    }

    void OnScroll(GlfwWindow window, double2 offset)
    {
        // X comes from a tilt wheel or a sideways touchpad swipe. Holding Shift turns an ordinary wheel sideways,
        // as in most apps; GLFW's scroll event carries no modifiers, so ask for Shift directly.
        var notches = new float2((float)offset.X, (float)offset.Y);
        var shift =
            window.GetKey(GlfwKey.LeftShift) != GlfwKeyAction.Release
            || window.GetKey(GlfwKey.RightShift) != GlfwKeyAction.Release;
        if (shift && notches.X == 0)
            notches = new float2(notches.Y, 0);
        _ui.MouseWheel(notches);
    }

    void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        bool handled;
        if (action == GlfwKeyAction.Release)
        {
            // The UI ignores releases, but one whose press it used shouldn't reach the app on its own.
            handled = _keysHeldByUi.Remove(key);
        }
        else
        {
            handled = _ui.KeyDown(ToKey(key), ToModifiers(mods));
            if (handled)
                _keysHeldByUi.Add(key);
        }

        if (!handled)
            UnhandledKey?.Invoke(key, action, mods);
    }

    void OnChar(GlfwWindow window, uint codePoint)
    {
        if (Rune.TryCreate(codePoint, out var rune))
            _ui.TextInput(rune);
    }

    // GLFW's Shift, Control, Alt and Super bits match Modifiers; Caps Lock and Num Lock are dropped.
    static Modifiers ToModifiers(GlfwModifier mods) => (Modifiers)((int)mods & 0xF);

    static Key ToKey(GlfwKey key) =>
        key switch
        {
            GlfwKey.Tab => Key.Tab,
            GlfwKey.Enter or GlfwKey.KpEnter => Key.Enter,
            GlfwKey.Escape => Key.Escape,
            GlfwKey.Space => Key.Space,
            GlfwKey.Backspace => Key.Backspace,
            GlfwKey.Delete => Key.Delete,
            GlfwKey.Left => Key.Left,
            GlfwKey.Right => Key.Right,
            GlfwKey.Up => Key.Up,
            GlfwKey.Down => Key.Down,
            GlfwKey.Home => Key.Home,
            GlfwKey.End => Key.End,
            GlfwKey.PageUp => Key.PageUp,
            GlfwKey.PageDown => Key.PageDown,
            GlfwKey.A => Key.A,
            GlfwKey.C => Key.C,
            GlfwKey.V => Key.V,
            GlfwKey.X => Key.X,
            GlfwKey.Y => Key.Y,
            GlfwKey.Z => Key.Z,
            _ => Key.Other,
        };

    /// <summary>
    /// Applies the UI's cursor if it changed. Also call after <see cref="Root.Layout"/>, since a layout change can
    /// move a button under a still cursor.
    /// </summary>
    public void UpdateCursor()
    {
        var wanted = _ui.Cursor;
        if (wanted == _shownCursor)
            return;
        _window.SetCursor(
            wanted switch
            {
                Cursor.Hand => _handCursor,
                Cursor.Text => _textCursor,
                _ => default,
            }
        );
        _shownCursor = wanted;
    }

    public void Dispose()
    {
        _window.OnCursorPos -= OnCursorPos;
        _window.OnCursorEnter -= OnCursorEnter;
        _window.OnMouseButton -= OnMouseButton;
        _window.OnScroll -= OnScroll;
        _window.OnKey -= OnKey;
        _window.OnChar -= OnChar;
        _window.SetCursor(default);
        _handCursor.Dispose();
        _textCursor.Dispose();
        Clipboard.GetText = () => null;
        Clipboard.SetText = _ => { };
    }
}
