using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Brutality;

/// <summary>
/// Gives a GLFW window the app's own title bar on Windows, without giving up what the system frame does. The window
/// keeps its native frame, so it still snaps, maximises and resizes like any other, but its title bar and borders are
/// removed and the whole window is drawn by the app. Windows asks where each mouse position falls; this answers
/// "caption" (drag to move, double-click to maximise) wherever <c>isCaption</c> says, "border" just inside the edges
/// so they still resize, and "client" everywhere else.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe partial class WindowChrome
{
    const uint WM_NCCALCSIZE = 0x0083;
    const uint WM_NCHITTEST = 0x0084;
    const int GWLP_WNDPROC = -4;

    const int HTCLIENT = 1, HTCAPTION = 2;
    const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// How far in from each edge the mouse resizes the window, in pixels.
    /// </summary>
    const int RESIZE_BORDER = 6;

    static nint _window;
    static nint _previousProcedure;
    static Func<int2, bool> _isCaption = _ => false;

    /// <summary>
    /// Takes over <paramref name="window"/>'s frame. <paramref name="isCaption"/> gets points in client pixels (the
    /// same as the framebuffer's) and says whether the window can be dragged from there. Call once, before showing
    /// anything; <see cref="Remove"/> before the window goes.
    /// </summary>
    public static void Install(GlfwWindow window, Func<int2, bool> isCaption)
    {
        _isCaption = isCaption;
        _window = GetWin32Window(window.NativeHandle);
        _previousProcedure = SetWindowLongPtrW(
            _window,
            GWLP_WNDPROC,
            (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WindowProcedure
        );

        // A sliver of the system frame kept in the client area is what keeps the drop shadow; the app draws over it.
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(_window, &margins);

        // Have Windows ask for the new frame size straight away.
        SetWindowPos(_window, 0, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Gives the window its own frame handling back.
    /// </summary>
    public static void Remove()
    {
        if (_window == 0)
            return;
        SetWindowLongPtrW(_window, GWLP_WNDPROC, _previousProcedure);
        _window = 0;
    }

    [UnmanagedCallersOnly]
    static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WM_NCCALCSIZE when wParam != 0:
                    // The whole window is client area. Maximised, though, Windows makes the window bigger than the
                    // screen by its frame on every side, so the client area is kept to the monitor's work area.
                    if (IsZoomed(window))
                    {
                        var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
                        if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &info))
                            ((Rect*)lParam)[0] = info.Work;
                    }
                    return 0;

                case WM_NCHITTEST:
                    return HitTest(window, lParam);
            }
        }
        catch (Exception e)
        {
            // An exception can't cross back into Windows, so it's reported and the message handled as usual.
            Console.Error.WriteLine(e);
        }
        return CallWindowProcW(_previousProcedure, window, message, wParam, lParam);
    }

    static nint HitTest(nint window, nint lParam)
    {
        var point = new Point { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
        ScreenToClient(window, &point);
        Rect client;
        GetClientRect(window, &client);

        if (!IsZoomed(window))
        {
            var left = point.X < RESIZE_BORDER;
            var right = point.X >= client.Right - RESIZE_BORDER;
            var top = point.Y < RESIZE_BORDER;
            var bottom = point.Y >= client.Bottom - RESIZE_BORDER;
            if (top && left)
                return HTTOPLEFT;
            if (top && right)
                return HTTOPRIGHT;
            if (bottom && left)
                return HTBOTTOMLEFT;
            if (bottom && right)
                return HTBOTTOMRIGHT;
            if (left)
                return HTLEFT;
            if (right)
                return HTRIGHT;
            if (top)
                return HTTOP;
            if (bottom)
                return HTBOTTOM;
        }

        return _isCaption(new int2(point.X, point.Y)) ? HTCAPTION : HTCLIENT;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("glfw3", EntryPoint = "glfwGetWin32Window")]
    private static partial nint GetWin32Window(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, Point* point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, Rect* rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, MonitorInfo* info);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint window, Margins* margins);
}
