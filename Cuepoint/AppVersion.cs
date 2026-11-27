using System.Reflection;

namespace Cuepoint;

/// <summary>
/// Which build of Cuepoint this is: the project's &lt;Version&gt;, with the commit it was built from after a + when the
/// build knew it (1.0.0+cbae87f).
/// </summary>
static class AppVersion
{
    public static string Text { get; } = Read();

    static string Read()
    {
        var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // The SDK adds the commit's full hash; the first seven characters are enough to find it.
        var plus = informational.IndexOf('+');
        return plus >= 0 && informational.Length > plus + 8 ? informational[..(plus + 8)] : informational;
    }
}
