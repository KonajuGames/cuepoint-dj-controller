namespace Cuepoint;

/// <summary>
/// Where Cuepoint keeps what's its own: the settings, keys, library database, virtual folders, controller mappings,
/// sets played and the error log. %LOCALAPPDATA%\Cuepoint, unless the screenshot tool has put a scratch folder in
/// its place before anything's read.
/// </summary>
static class AppFolder
{
    public static string Path { get; private set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuepoint");

    /// <summary>
    /// Uses <paramref name="folder"/> instead, for the whole run.
    /// </summary>
    public static void Use(string folder) => Path = folder;
}
