using System.Runtime.InteropServices;
using System.Text;

namespace Cuepoint.Streaming;

/// <summary>
/// Somewhere a password can be kept outside the settings file: the Mac's Keychain.
/// </summary>
interface ISecretStore
{
    bool Save(string secret);

    /// <summary>
    /// The password saved; null if there's none.
    /// </summary>
    string? Load();
}

/// <summary>
/// A password kept so it isn't in the settings file to read in plain text. On Windows it's there encrypted by Windows
/// for the user signed in (DPAPI), so it's no use copied to another account or computer; on a Mac it's in the user's
/// Keychain, and the settings say only that it is.
/// </summary>
static partial class SavedSecret
{
    /// <summary>
    /// What the settings hold for a password in the Keychain.
    /// </summary>
    internal const string IN_KEYCHAIN = "keychain";

    /// <summary>
    /// <paramref name="secret"/> encrypted, as text for the settings; empty for none. Null if it couldn't be kept.
    /// </summary>
    public static string? Protect(string secret)
    {
        if (secret.Length == 0)
            return "";
        if (!OperatingSystem.IsWindows())
            return Protect(secret, OperatingSystem.IsMacOS() ? new MacKeychain() : null);
        return Crypt(Encoding.UTF8.GetBytes(secret), protect: true) is { } sealedBytes ? Convert.ToBase64String(sealedBytes) : null;
    }

    /// <summary>
    /// <paramref name="secret"/> kept in <paramref name="store"/>, giving what the settings hold for it; null if there's
    /// no store, or it wouldn't take it.
    /// </summary>
    internal static string? Protect(string secret, ISecretStore? store) =>
        secret.Length == 0 ? "" : store?.Save(secret) == true ? IN_KEYCHAIN : null;

    /// <summary>
    /// The password <see cref="Protect"/> made <paramref name="saved"/> from; empty if there's none, or it can't be
    /// read (another account's, say).
    /// </summary>
    public static string Unprotect(string? saved)
    {
        if (string.IsNullOrEmpty(saved))
            return "";
        if (!OperatingSystem.IsWindows())
            return Unprotect(saved, OperatingSystem.IsMacOS() ? new MacKeychain() : null);
        try
        {
            return Crypt(Convert.FromBase64String(saved), protect: false) is { } plain ? Encoding.UTF8.GetString(plain) : "";
        }
        catch (FormatException)
        {
            return "";
        }
    }

    /// <summary>
    /// The password <paramref name="saved"/> says is in <paramref name="store"/>; empty if it says otherwise (one
    /// Windows encrypted, say) or the store has none.
    /// </summary>
    internal static string Unprotect(string? saved, ISecretStore? store) =>
        saved == IN_KEYCHAIN ? store?.Load() ?? "" : "";

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
