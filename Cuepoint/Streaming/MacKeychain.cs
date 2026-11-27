using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Cuepoint.Streaming;

/// <summary>
/// The streaming password in the Mac user's login Keychain, as a generic password for the service "Cuepoint" and the
/// account "Live stream", which Keychain Access shows by those names.
/// </summary>
[SupportedOSPlatform("macos")]
sealed unsafe partial class MacKeychain : ISecretStore
{
    const string SECURITY = "/System/Library/Frameworks/Security.framework/Security";
    const string CORE_FOUNDATION = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const int errSecSuccess = 0;
    const int errSecDuplicateItem = -25299;

    static readonly byte[] Service = Encoding.UTF8.GetBytes("Cuepoint");
    static readonly byte[] Account = Encoding.UTF8.GetBytes("Live stream");

    public bool Save(string secret)
    {
        var password = Encoding.UTF8.GetBytes(secret);
        fixed (byte* service = Service)
        fixed (byte* account = Account)
        fixed (byte* data = password)
        {
            var status = SecKeychainAddGenericPassword(0, (uint)Service.Length, service, (uint)Account.Length, account, (uint)password.Length, data, null);
            if (status == errSecSuccess)
                return true;
            if (status != errSecDuplicateItem)
                return false;

            // There's one already: change its password.
            nint item;
            if (SecKeychainFindGenericPassword(0, (uint)Service.Length, service, (uint)Account.Length, account, null, null, &item) != errSecSuccess)
                return false;
            try
            {
                return SecKeychainItemModifyAttributesAndData(item, 0, (uint)password.Length, data) == errSecSuccess;
            }
            finally
            {
                CFRelease(item);
            }
        }
    }

    public string? Load()
    {
        fixed (byte* service = Service)
        fixed (byte* account = Account)
        {
            uint length;
            void* data;
            if (SecKeychainFindGenericPassword(0, (uint)Service.Length, service, (uint)Account.Length, account, &length, &data, null) != errSecSuccess)
                return null;
            try
            {
                return Encoding.UTF8.GetString((byte*)data, (int)length);
            }
            finally
            {
                SecKeychainItemFreeContent(0, data);
            }
        }
    }

    [LibraryImport(SECURITY)]
    private static partial int SecKeychainAddGenericPassword(
        nint keychain,
        uint serviceLength,
        byte* service,
        uint accountLength,
        byte* account,
        uint passwordLength,
        void* password,
        nint* item
    );

    [LibraryImport(SECURITY)]
    private static partial int SecKeychainFindGenericPassword(
        nint keychains,
        uint serviceLength,
        byte* service,
        uint accountLength,
        byte* account,
        uint* passwordLength,
        void** password,
        nint* item
    );

    [LibraryImport(SECURITY)]
    private static partial int SecKeychainItemModifyAttributesAndData(nint item, nint attributes, uint length, void* data);

    [LibraryImport(SECURITY)]
    private static partial int SecKeychainItemFreeContent(nint attributes, void* data);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial void CFRelease(nint item);
}
