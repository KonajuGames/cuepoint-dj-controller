namespace Cuepoint.Music;

/// <summary>
/// The drives music can be on, and which one a file's on, on Windows or a Mac. Windows has a drive letter for each. A
/// Mac has one tree: the startup disk is /, and every other volume (a USB stick, a second drive, a network share) is
/// mounted as a folder in /Volumes, where the startup disk also appears, as a link back to /.
/// </summary>
static class StorageDrives
{
    const string VOLUMES = "/Volumes/";

    /// <summary>
    /// What a file or folder name mustn't have, wherever the app runs: what Windows forbids, which is more than a Mac
    /// does, since a drive (a FAT or exFAT stick, say) can be written on one and read on the other.
    /// </summary>
    public static IReadOnlySet<char> InvalidNameChars { get; } =
        Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").Concat(Enumerable.Range(0, 32).Select(i => (char)i)).ToHashSet();

    /// <summary>
    /// A path relative to a drive, as a drive's own files keep it, made one for this computer. They're kept with
    /// Windows' separators wherever they're written, so a drive's library and playlists work on Windows and a Mac
    /// alike.
    /// </summary>
    public static string FromKept(string relative) => FromKept(relative, OperatingSystem.IsWindows());

    internal static string FromKept(string relative, bool windows) => windows ? relative : relative.Replace('\\', '/');

    /// <summary>
    /// A path relative to a drive, made as the drive's own files keep it: with Windows' separators.
    /// </summary>
    public static string ToKept(string relative) => ToKept(relative, OperatingSystem.IsWindows());

    internal static string ToKept(string relative, bool windows) => windows ? relative : relative.Replace('/', '\\');

    /// <summary>
    /// The drive the system's on: C:\ (or whichever Windows is on), or / on a Mac.
    /// </summary>
    public static string SystemRoot { get; } =
        OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? root : "C:\\" : "/";

    /// <summary>
    /// The root of the drive <paramref name="path"/> is on, with a separator at the end; null if it has none.
    /// </summary>
    public static string? RootOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (!OperatingSystem.IsMacOS())
            return Path.GetPathRoot(Path.IsPathRooted(path) ? Path.GetFullPath(path) : path) is { Length: > 0 } root ? root : null;
        return Path.IsPathRooted(path) ? MacRootOf(Path.GetFullPath(path), MacStartupName) : null;
    }

    /// <summary>
    /// The root of the Mac drive <paramref name="fullPath"/> is on: /Volumes/name/ for one in /Volumes, unless it's
    /// <paramref name="startupName"/> (the startup disk's link back to /), or / for anything else.
    /// </summary>
    internal static string MacRootOf(string fullPath, string? startupName)
    {
        if (!fullPath.StartsWith(VOLUMES, StringComparison.OrdinalIgnoreCase))
            return "/";
        var end = fullPath.IndexOf('/', VOLUMES.Length);
        var name = end < 0 ? fullPath[VOLUMES.Length..] : fullPath[VOLUMES.Length..end];
        if (name.Length == 0 || string.Equals(name, startupName, StringComparison.OrdinalIgnoreCase))
            return "/";
        return VOLUMES + name + "/";
    }

    /// <summary>
    /// The drives there are now that could hold music, by root and the name to show for each, the system's first on a
    /// Mac. Null on Windows, where the caller lists Windows' own.
    /// </summary>
    public static IReadOnlyList<(string Root, string Label)>? MacDrives() =>
        OperatingSystem.IsMacOS() ? MacDrives(MacVolumes()) : null;

    /// <summary>
    /// The drives for the folders found in /Volumes (by name, and where each links to if it's a link): / under the
    /// startup disk's name (the one linking to /), then the rest by name. Hidden ones (starting with a dot) are left
    /// out.
    /// </summary>
    internal static IReadOnlyList<(string Root, string Label)> MacDrives(IEnumerable<(string Name, string? LinkTarget)> volumes)
    {
        var list = volumes.Where(volume => volume.Name.Length > 0 && volume.Name[0] != '.').ToList();
        var startup = list.FirstOrDefault(volume => volume.LinkTarget == "/").Name;
        var drives = new List<(string Root, string Label)> { ("/", startup ?? "Macintosh HD") };
        drives.AddRange(
            list.Where(volume => volume.LinkTarget != "/")
                .OrderBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase)
                .Select(volume => (VOLUMES + volume.Name + "/", volume.Name))
        );
        return drives;
    }

    /// <summary>
    /// The startup disk's name, as its link in /Volumes has it; null if there's no such link.
    /// </summary>
    static string? MacStartupName => MacVolumes().FirstOrDefault(volume => volume.LinkTarget == "/").Name;

    static IEnumerable<(string Name, string? LinkTarget)> MacVolumes()
    {
        DirectoryInfo[] folders;
        try
        {
            folders = new DirectoryInfo(VOLUMES).GetDirectories();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return folders.Select(folder => (folder.Name, folder.LinkTarget));
    }
}
