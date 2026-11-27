using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuepoint;

/// <summary>
/// The process's environment as native libraries see it, on a Mac. .NET's own Environment.SetEnvironmentVariable
/// changes only .NET's copy there, which getenv in a native library (the Vulkan loader, say) never reads.
/// </summary>
[SupportedOSPlatform("macos")]
static partial class MacEnvironment
{
    /// <summary>
    /// Sets <paramref name="name"/> to <paramref name="value"/> for native code and .NET alike.
    /// </summary>
    public static void Set(string name, string value)
    {
        setenv(name, value, 1);
        Environment.SetEnvironmentVariable(name, value);
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);
}
