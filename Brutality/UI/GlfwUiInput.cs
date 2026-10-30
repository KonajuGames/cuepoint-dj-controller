using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// Feeds a GLFW window's mouse events into a <see cref="UiRoot"/> and shows the cursor it asks for. Dispose before
/// the window.
/// </summary>
sealed class GlfwUiInput : IDisposable
{
    readonly GlfwWindow _window;
    readonly UiRoot _ui;
    readonly GlfwCursor _handCursor;
    UiCursor _shownCursor = UiCursor.Arrow;

    public GlfwUiInput(GlfwWindow window, UiRoot ui)
    {
        _window = window;
        _ui = ui;
        _handCursor = Glfw.CreateCursor(GlfwStandardCursor.PointingHand);

        window.OnCursorPos += OnCursorPos;
        window.OnCursorEnter += OnCursorEnter;
        window.OnMouseButton += OnMouseButton;
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
        UiMouseButton? uiButton = button switch
        {
            GlfwMouseButton.Left => UiMouseButton.Left,
            GlfwMouseButton.Right => UiMouseButton.Right,
            GlfwMouseButton.Middle => UiMouseButton.Middle,
            _ => null,
        };
        if (uiButton is { } b)
            _ui.MouseButton(b, action == GlfwButtonAction.Press);
        UpdateCursor();
    }

    /// <summary>
    /// Applies the UI's cursor if it changed. Also call after <see cref="UiRoot.Layout"/>, since a layout change can
    /// move a button under a still cursor.
    /// </summary>
    public void UpdateCursor()
    {
        var wanted = _ui.Cursor;
        if (wanted == _shownCursor)
            return;
        _window.SetCursor(wanted == UiCursor.Hand ? _handCursor : default);
        _shownCursor = wanted;
    }

    public void Dispose()
    {
        _window.OnCursorPos -= OnCursorPos;
        _window.OnCursorEnter -= OnCursorEnter;
        _window.OnMouseButton -= OnMouseButton;
        _window.SetCursor(default);
        _handCursor.Dispose();
    }
}
