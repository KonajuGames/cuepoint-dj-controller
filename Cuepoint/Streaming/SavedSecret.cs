using System.Runtime.InteropServices;
using System.Text;

namespace Cuepoint.Streaming;

/// <summary>
/// A password kept in the settings file encrypted by Windows for the user signed in (DPAPI), so it isn't there to
/// read in plain text, and is no use copied to another account or computer.
/// </summary>
static partial class SavedSecret
{
    /// <summary>
    /// <paramref name="secret"/> encrypted, as text for the settings; empty for none. Null if Windows wouldn't.
    /// </summary>
    public static string? Protect(string secret)
    {
        if (secret.Length == 0)
            return "";
        if (!OperatingSystem.IsWindows())
            return null;
        return Crypt(Encoding.UTF8.GetBytes(secret), protect: true) is { } sealedBytes ? Convert.ToBase64String(sealedBytes) : null;
    }

    /// <summary>
    /// The password <see cref="Protect"/> made <paramref name="saved"/> from; empty if there's none, or it can't be
    /// read (another account's, say).
    /// </summary>
    public static string Unprotect(string? saved)
    {
        if (string.IsNullOrEmpty(saved) || !OperatingSystem.IsWindows())
            return "";
        try
        {
            return Crypt(Convert.FromBase64String(saved), protect: false) is { } plain ? Encoding.UTF8.GetString(plain) : "";
        }
        catch (FormatException)
        {
            return "";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    static unsafe byte[]? Crypt(byte[] data, bool protect)
    {
        fixed (byte* bytes = data)
        {
            var input = new Blob { Size = data.Length, Data = (nint)bytes };
            var ok = protect
                ? CryptProtectData(ref input, 0, 0, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok)
                return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }
}
