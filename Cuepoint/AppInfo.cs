using System.Reflection;
using System.Runtime.InteropServices;

namespace Cuepoint;

/// <summary>
/// What Cuepoint says about itself, from the metadata in Cuepoint.csproj that the build writes into the assembly: for
/// --version, the title bar and the options' About tab.
/// </summary>
static class AppInfo
{
    static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    /// <summary>
    /// The project's &lt;Version&gt;, with the commit it was built from after a + when the build knew it (1.0.0+cbae87f).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// The version alone, without the commit (1.0.0).
    /// </summary>
    public static string ShortVersion => Version.Split('+')[0];

    /// <summary>
    /// The commit it was built from, shortened; empty if the build didn't know.
    /// </summary>
    public static string Commit => Version.Split('+') is [_, var commit] ? commit : "";

    public static string Title { get; } = Assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "Cuepoint";
    public static string Description { get; } = Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "";
    public static string Company { get; } = Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";
    public static string Copyright { get; } = Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
    public static string License { get; } = Metadata("License");
    public static string RepositoryUrl { get; } = Metadata("RepositoryUrl");

    /// <summary>
    /// The .NET it's running on, and the processor (.NET 10.0.1, X64).
    /// </summary>
    public static string Runtime => $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}";

    static string Metadata(string key) =>
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value ?? "";

    static string ReadVersion()
    {
        var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            return Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // The SDK adds the commit's full hash; the first seven characters are enough to find it.
        var plus = informational.IndexOf('+');
        return plus >= 0 && informational.Length > plus + 8 ? informational[..(plus + 8)] : informational;
    }
}
