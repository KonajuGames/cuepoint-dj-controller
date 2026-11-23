using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Cuepoint.Music;

/// <summary>
/// Deleting a file: to the Recycle Bin where its drive has one, and for good where it hasn't, or away from Windows.
/// </summary>
static partial class FileDeletion
{
    /// <summary>
    /// Whether deleting <paramref name="path"/> sends it to the Recycle Bin, so it can be got back: on Windows, on a
    /// fixed drive, unless the Recycle Bin is turned off for that drive ("Don't move files to the Recycle Bin") or for
    /// every drive by policy. A memory card, a network drive or a drive elsewhere has none.
    /// </summary>
    public static bool WouldRecycle(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed || RecyclingForbidden())
                return false;
            return VolumeId(path) is not { } volume
                || Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{volume}")?.GetValue("NukeOnDelete") is not int nuke
                || nuke == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the file at <paramref name="path"/>: to the Recycle Bin if <paramref name="recycle"/>, as
    /// <see cref="WouldRecycle"/> says, otherwise for good. Returns why it couldn't, or null once it's gone. One that
    /// couldn't be recycled is left, not deleted for good instead.
    /// </summary>
    public static string? Delete(string path, bool recycle)
    {
        if (recycle)
            return WindowsShell.MoveToRecycleBin(path);
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return e.Message;
        }
    }

    // "Remove files immediately when deleted", set by policy for the user or the computer.
    [SupportedOSPlatform("windows")]
    static bool RecyclingForbidden() =>
        new[] { Registry.CurrentUser, Registry.LocalMachine }.Any(hive =>
            hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer")?.GetValue("NoRecycleFiles") is int forbidden && forbidden != 0
        );

    // The "{GUID}" of the volume the path is on, as the Recycle Bin's settings are kept under.
    [SupportedOSPlatform("windows")]
    static unsafe string? VolumeId(string path)
    {
        var mount = stackalloc char[MAX_PATH];
        var volume = stackalloc char[MAX_PATH];
        if (!GetVolumePathNameW(Path.GetFullPath(path), mount, MAX_PATH) || !GetVolumeNameForVolumeMountPointW(mount, volume, MAX_PATH))
            return null;
        // As "\\?\Volume{...}\".
        var name = new string(volume);
        var start = name.IndexOf('{');
        var end = name.IndexOf('}');
        return start >= 0 && end > start ? name[start..(end + 1)] : null;
    }

    const int MAX_PATH = 261;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathNameW(string fileName, char* volumePath, int length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPointW(char* mountPoint, char* volumeName, int length);
}
