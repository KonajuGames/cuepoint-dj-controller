using System.IO.Compression;

namespace Cuepoint;

/// <summary>
/// Everything set up in Cuepoint, in one file to keep or take to another computer: the settings (devices, options,
/// smart playlists, sampler pads and the rest), the keyboard's keys, the controller mappings learned, the virtual
/// folders kept in the app's own folder, and the sets played. Not the song database, which is large and kept with the songs on each drive,
/// nor virtual folders kept on other drives, which go with them.
/// </summary>
static class SettingsBackup
{
    /// <summary>
    /// What's backed up, relative to the app's folder: files by name, and folders whose every file goes.
    /// </summary>
    static readonly string[] Files = ["settings.json", "keys.json"];

    static readonly string[] Folders = ["controllers", "VirtualFolders", "history", "Samples"];

    /// <summary>
    /// Writes what's set up in <paramref name="appFolder"/> to a zip at <paramref name="zipPath"/>. Returns how many
    /// files went in, and why it couldn't, if it couldn't.
    /// </summary>
    public static (int Files, string? Error) Write(string zipPath, string appFolder)
    {
        try
        {
            var temporary = zipPath + ".tmp";
            var count = 0;
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var (path, entry) in Wanted(appFolder))
                {
                    zip.CreateEntryFromFile(path, entry);
                    count++;
                }
            }
            File.Move(temporary, zipPath, overwrite: true);
            return (count, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, e.Message);
        }
    }

    static IEnumerable<(string Path, string Entry)> Wanted(string appFolder)
    {
        foreach (var name in Files)
        {
            var path = Path.Combine(appFolder, name);
            if (File.Exists(path))
                yield return (path, name);
        }
        foreach (var folder in Folders)
        {
            var root = Path.Combine(appFolder, folder);
            if (!Directory.Exists(root))
                continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                yield return (path, Path.GetRelativePath(appFolder, path).Replace('\\', '/'));
        }
    }

    /// <summary>
    /// Puts what's in the backup at <paramref name="zipPath"/> back into <paramref name="appFolder"/>, over what's
    /// there; anything there that the backup hasn't is left. Only what a backup holds is taken out of it, so a zip
    /// of anything else can't put files elsewhere. Returns how many files came back, and why it couldn't, if it
    /// couldn't, in which case nothing's changed.
    /// </summary>
    public static (int Files, string? Error) Restore(string zipPath, string appFolder)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var root = Path.GetFullPath(appFolder) + Path.DirectorySeparatorChar;
            var entries = zip.Entries.Where(entry => entry.Length > 0 || entry.Name.Length > 0).Where(IsWanted).ToList();
            if (!entries.Any(entry => entry.FullName == "settings.json"))
                return (0, "That isn't a Cuepoint settings backup");

            var targets = entries.Select(entry => (Entry: entry, Path: Path.GetFullPath(Path.Combine(root, entry.FullName)))).ToList();
            if (targets.Any(target => !target.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                return (0, "That backup has files that would go outside Cuepoint's folder");
            foreach (var (entry, path) in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
            return (targets.Count, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (0, e is InvalidDataException ? "That isn't a Cuepoint settings backup" : e.Message);
        }
    }

    static bool IsWanted(ZipArchiveEntry entry) =>
        Files.Contains(entry.FullName) || Folders.Any(folder => entry.FullName.StartsWith(folder + "/", StringComparison.Ordinal));
}
