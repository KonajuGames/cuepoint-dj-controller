using System.Runtime.InteropServices;

namespace Brutality;

/// <summary>
/// What a windowed (WinExe) app loses on Windows, put back: console output when started from a terminal, and a
/// visible error when it crashes. Elsewhere there is no WinExe distinction, so both fall back to the console.
/// </summary>
static partial class WindowsShell
{
    const int ATTACH_PARENT_PROCESS = -1;
    const uint MB_OK = 0x0, MB_ICONERROR = 0x10;

    /// <summary>
    /// Sends Console output to the terminal that started the app, if any. A double-clicked app has none, and its
    /// output is dropped as usual.
    /// </summary>
    public static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows() || !AttachConsole(ATTACH_PARENT_PROCESS))
            return;

        // The standard streams were bound before the console existed, so rebind them to it.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    /// <summary>
    /// Reports a fatal error: to the console, and on Windows also in a message box, since a windowed app usually
    /// has no console to read it in.
    /// </summary>
    public static void ShowFatalError(Exception exception)
    {
        Console.Error.WriteLine(exception);

        if (OperatingSystem.IsWindows())
            MessageBoxW(nint.Zero, exception.ToString(), "Cuepoint crashed", MB_OK | MB_ICONERROR);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}
