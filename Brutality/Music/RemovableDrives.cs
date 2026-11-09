using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Brutality.Music;

/// <summary>
/// The drives that can be unplugged and taken away, and written to. Windows calls a USB hard drive or SSD a fixed
/// drive, so those are told by the bus they're on.
/// </summary>
static partial class RemovableDrives
{
    /// <summary>
    /// The removable drives there are now that can be written to: tried by writing a small file to each, and deleting
    /// it, as nothing else tells of every way a drive can be locked. Never the system drive.
    /// </summary>
    public static IReadOnlyList<StorageDrive> Writable()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        return VirtualFolders
            .ReadyDrives()
            .Where(drive => !string.Equals(drive.Root, system, StringComparison.OrdinalIgnoreCase) && IsRemovable(drive.Root) && CanWrite(drive.Root))
            .OrderBy(drive => drive.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static bool IsRemovable(string root)
    {
        try
        {
            var type = new DriveInfo(root).DriveType;
            return type == DriveType.Removable || (type == DriveType.Fixed && OperatingSystem.IsWindows() && IsOnRemovableBus(root));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    static bool CanWrite(string root)
    {
        var probe = Path.Combine(root, $".cuepoint-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    const uint FILE_SHARE_READ_WRITE = 3;
    const uint OPEN_EXISTING = 3;
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    // Where STORAGE_DEVICE_DESCRIPTOR keeps its bus type.
    const int BUS_TYPE_OFFSET = 28;

    // BusType1394, BusTypeUsb, BusTypeSd and BusTypeMmc.
    static bool IsRemovableBus(uint bus) => bus is 4 or 7 or 12 or 13;

    [SupportedOSPlatform("windows")]
    static unsafe bool IsOnRemovableBus(string root)
    {
        // Asking needs no access to the drive's data, so no administrator.
        using var volume = CreateFile($@"\\.\{root.TrimEnd('\\', '/')}", 0, FILE_SHARE_READ_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (volume.IsInvalid)
            return false;
        // STORAGE_PROPERTY_QUERY: the device's own property, a standard query.
        Span<byte> query = stackalloc byte[12];
        query.Clear();
        Span<byte> descriptor = stackalloc byte[1024];
        fixed (byte* input = query, output = descriptor)
        {
            return DeviceIoControl(volume, IOCTL_STORAGE_QUERY_PROPERTY, input, (uint)query.Length, output, (uint)descriptor.Length, out var returned, 0)
                && returned >= BUS_TYPE_OFFSET + 4
                && IsRemovableBus(BinaryPrimitives.ReadUInt32LittleEndian(descriptor[BUS_TYPE_OFFSET..]));
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, uint inputSize, byte* output, uint outputSize, out uint returned, nint overlapped);
}
