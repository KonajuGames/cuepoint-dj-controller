using System.Runtime.InteropServices;

namespace Brutality;

/// <summary>
/// What a windowed (WinExe) app loses on Windows, put back: console output when started from a terminal, and a
/// visible error when it crashes. Elsewhere there is no WinExe distinction, so both fall back to the console. Also
/// the Recycle Bin, for deleting files the user can get back, keeping the computer awake mid-set, and telling a
/// read-only drive, and asking for a file to open.
/// </summary>
static unsafe partial class WindowsShell
{
    const int ATTACH_PARENT_PROCESS = -1;
    const uint MB_OK = 0x0, MB_ICONERROR = 0x10;
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
    const uint FILE_READ_ONLY_VOLUME = 0x80000;
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

    /// <summary>
    /// Whether the drive at <paramref name="root"/> (as "F:\\") can only be read: a CD, or a card with its lock on.
    /// Asks without writing anything there.
    /// </summary>
    public static bool IsReadOnlyVolume(string root)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        uint flags;
        return GetVolumeInformationW(root, null, 0, null, null, &flags, null, 0) && (flags & FILE_READ_ONLY_VOLUME) != 0;
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

    const uint OFN_OVERWRITEPROMPT = 0x2, OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;

    /// <summary>
    /// What Windows' Open dialog is told and tells back; the Unicode OPENFILENAMEW.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct OpenFileName
    {
        public int Size;
        public nint Owner;
        public nint Instance;
        public char* Filter;
        public char* CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public char* File;
        public int MaxFile;
        public char* FileTitle;
        public int MaxFileTitle;
        public char* InitialFolder;
        public char* Title;
        public uint Flags;
        public ushort FileOffset;
        public ushort FileExtension;
        public char* DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public char* Template;
        public nint Reserved;
        public uint Reserved2;
        public uint FlagsEx;
    }

    /// <summary>
    /// Asks for a file to open with Windows' own Open dialog, over <paramref name="owner"/> (a window's handle, or 0):
    /// <paramref name="filter"/> pairs a description with its patterns, as ("Rekordbox XML", "*.xml"). Returns the
    /// file's path, or null if none was chosen. It waits for the answer; the decks play on meanwhile, on their own
    /// thread. Null away from Windows.
    /// </summary>
    public static string? AskForFile(nint owner, string title, (string Description, string Patterns) filter, string? folder = null)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        // Each description and its patterns end in a nul, and the list in another.
        var filters = $"{filter.Description} ({filter.Patterns})\0{filter.Patterns}\0All files (*.*)\0*.*\0\0";
        var file = new char[4096];
        fixed (char* filterText = filters)
        fixed (char* titleText = title)
        fixed (char* folderText = folder)
        fixed (char* fileText = file)
        {
            var dialog = new OpenFileName
            {
                Size = sizeof(OpenFileName),
                Owner = owner,
                Filter = filterText,
                FilterIndex = 1,
                File = fileText,
                MaxFile = file.Length,
                InitialFolder = folderText,
                Title = titleText,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };
            if (GetOpenFileNameW(&dialog) == 0)
                return null;
        }
        var length = Array.IndexOf(file, '\0');
        return length > 0 ? new string(file, 0, length) : null;
    }

    [LibraryImport("comdlg32.dll")]
    private static partial int GetOpenFileNameW(OpenFileName* dialog);

    /// <summary>
    /// Asks where to save a file with Windows' own Save dialog, over <paramref name="owner"/>, offering
    /// <paramref name="types"/> to choose from, each a description and its pattern, as ("M3U playlist", "*.m3u8"),
    /// and starting with <paramref name="fileName"/>. Returns where, with the chosen type's extension put on if none
    /// was typed, and which of the types was chosen, from 0; null if it was cancelled. It asks before overwriting a
    /// file there already. Null away from Windows.
    /// </summary>
    public static (string Path, int Type)? AskWhereToSave(
        nint owner,
        string title,
        IReadOnlyList<(string Description, string Pattern)> types,
        string fileName,
        string? folder = null
    )
    {
        if (!OperatingSystem.IsWindows() || types.Count == 0)
            return null;
        var filters = string.Concat(types.Select(type => $"{type.Description} ({type.Pattern})\0{type.Pattern}\0")) + "\0";
        // Without a dot, the extension of the type chosen goes on what's typed.
        var extension = types[0].Pattern.TrimStart('*', '.');
        var file = new char[4096];
        fileName.AsSpan(0, Math.Min(fileName.Length, file.Length - 1)).CopyTo(file);
        int chosen;
        fixed (char* filterText = filters)
        fixed (char* titleText = title)
        fixed (char* folderText = folder)
        fixed (char* extensionText = extension)
        fixed (char* fileText = file)
        {
            var dialog = new OpenFileName
            {
                Size = sizeof(OpenFileName),
                Owner = owner,
                Filter = filterText,
                FilterIndex = 1,
                File = fileText,
                MaxFile = file.Length,
                InitialFolder = folderText,
                Title = titleText,
                DefaultExtension = extensionText,
                Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };
            if (GetSaveFileNameW(&dialog) == 0)
                return null;
            chosen = Math.Clamp(dialog.FilterIndex - 1, 0, types.Count - 1);
        }
        var length = Array.IndexOf(file, '\0');
        if (length <= 0)
            return null;
        var path = new string(file, 0, length);
        if (!Path.HasExtension(path))
            path += "." + types[chosen].Pattern.TrimStart('*', '.');
        return (path, chosen);
    }

    [LibraryImport("comdlg32.dll")]
    private static partial int GetSaveFileNameW(OpenFileName* dialog);

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

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationW(
        string rootPath,
        char* volumeName,
        uint volumeNameSize,
        uint* serialNumber,
        uint* maximumComponentLength,
        uint* fileSystemFlags,
        char* fileSystemName,
        uint fileSystemNameSize
    );
}
