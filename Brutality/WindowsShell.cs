using System.Runtime.InteropServices;

namespace Brutality;

/// <summary>
/// What a windowed (WinExe) app loses on Windows, put back: console output when started from a terminal, and a
/// visible error when it crashes. Elsewhere there is no WinExe distinction, so both fall back to the console. Also
/// the Recycle Bin, for deleting files the user can get back, and keeping the computer awake mid-set.
/// </summary>
static unsafe partial class WindowsShell
{
    const int ATTACH_PARENT_PROCESS = -1;
    const uint MB_OK = 0x0, MB_ICONERROR = 0x10;
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
    const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2, ES_CONTINUOUS = 0x80000000;

    /// <summary>
    /// Whether the computer is being kept awake, by <see cref="KeepAwake"/>.
    /// </summary>
    public static bool IsKeepingAwake { get; private set; }

    /// <summary>
    /// Keeps the computer from sleeping and the screen from turning off, or lets them again. Call from the thread
    /// that runs the window, which holds it until told otherwise. The screen saver isn't stopped by this alone; the
    /// window turns it away too (see <see cref="WindowChrome"/>) while <see cref="IsKeepingAwake"/>.
    /// </summary>
    public static void KeepAwake(bool awake)
    {
        if (!OperatingSystem.IsWindows() || awake == IsKeepingAwake)
            return;
        if (SetThreadExecutionState(awake ? ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED : ES_CONTINUOUS) != 0)
            IsKeepingAwake = awake;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileOperation
    {
        public nint Window;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    /// <summary>
    /// Moves a file to the Recycle Bin, quietly. Returns why it couldn't, or null once it's gone.
    /// </summary>
    public static string? MoveToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows())
            return "There's no Recycle Bin here";

        // The shell takes a list of paths, each ended by a null, and the list by another.
        var paths = Path.GetFullPath(path) + "\0\0";
        fixed (char* from = paths)
        {
            var operation = new FileOperation
            {
                Function = FO_DELETE,
                From = from,
                Flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
            };
            var result = SHFileOperationW(&operation);
            return result == 0 && operation.AnyAborted == 0 ? null : $"Windows couldn't move it to the Recycle Bin (error {result})";
        }
    }

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

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(FileOperation* operation);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
