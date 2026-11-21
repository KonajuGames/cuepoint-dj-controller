using System.IO.Compression;

namespace Cuepoint.Tests;

public class SettingsBackupTests
{
    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void A_backup_comes_back_into_another_computer_s_folder()
    {
        using var folder = new TempFolder();
        var here = folder["here"];
        Write(Path.Combine(here, "settings.json"), "{\"MasterLimiter\":false}");
        Write(Path.Combine(here, "keys.json"), "{}");
        Write(Path.Combine(here, "controllers", "DDJ-SB3.json"), "mapping");
        Write(Path.Combine(here, "VirtualFolders", "Warm up.m3u8"), "#EXTM3U");
        // The song database and the error log stay behind.
        Write(Path.Combine(here, "library.json"), "big");
        Write(Path.Combine(here, "errors.log"), "oops");

        var (written, error) = SettingsBackup.Write(folder["backup.zip"], here);
        Assert.Null(error);
        Assert.Equal(4, written);

        var there = folder["there"];
        Write(Path.Combine(there, "settings.json"), "{}");
        Write(Path.Combine(there, "VirtualFolders", "Theirs.m3u8"), "#EXTM3U");
        var (restored, problem) = SettingsBackup.Restore(folder["backup.zip"], there);

        Assert.Null(problem);
        Assert.Equal(4, restored);
        Assert.Equal("{\"MasterLimiter\":false}", File.ReadAllText(Path.Combine(there, "settings.json")));
        Assert.Equal("mapping", File.ReadAllText(Path.Combine(there, "controllers", "DDJ-SB3.json")));
        Assert.True(File.Exists(Path.Combine(there, "VirtualFolders", "Warm up.m3u8")));
        Assert.True(File.Exists(Path.Combine(there, "VirtualFolders", "Theirs.m3u8")));
        Assert.False(File.Exists(Path.Combine(there, "library.json")));
    }

    [Fact]
    public void A_zip_that_isn_t_a_backup_changes_nothing()
    {
        using var folder = new TempFolder();
        var there = folder["there"];
        Write(Path.Combine(there, "settings.json"), "mine");

        using (var zip = ZipFile.Open(folder["other.zip"], ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open()))
                writer.Write("hello");
        Assert.Equal("That isn't a Cuepoint settings backup", SettingsBackup.Restore(folder["other.zip"], there).Error);

        File.WriteAllText(folder["junk.zip"], "not a zip at all");
        Assert.Equal("That isn't a Cuepoint settings backup", SettingsBackup.Restore(folder["junk.zip"], there).Error);

        // Paths that climb out of the folder are refused outright.
        using (var zip = ZipFile.Open(folder["sneaky.zip"], ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("settings.json").Open()))
                writer.Write("{}");
            using (var writer = new StreamWriter(zip.CreateEntry("controllers/../../outside.json").Open()))
                writer.Write("x");
        }
        Assert.NotNull(SettingsBackup.Restore(folder["sneaky.zip"], there).Error);
        Assert.False(File.Exists(folder["outside.json"]));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(there, "settings.json")));
    }
}
