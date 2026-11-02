using Brutality.Music;
using static Brutality.Tests.Music.LibraryFileTests;

namespace Brutality.Tests.Music;

public class LibraryDatabaseTests
{
    /// <summary>
    /// A system drive and another, as folders, with the database's own file on the system one.
    /// </summary>
    sealed class Drives : IDisposable
    {
        public readonly TempFolder Folder = new();
        public string System => Folder["C"] + Path.DirectorySeparatorChar;
        public string Other => Folder["F"] + Path.DirectorySeparatorChar;
        public string AppFile => Path.Combine(System, "AppData", "library.json");

        public Drives()
        {
            Directory.CreateDirectory(System);
            Directory.CreateDirectory(Other);
        }

        public string? RootOf(string path) =>
            path.StartsWith(System, StringComparison.OrdinalIgnoreCase) ? System
            : path.StartsWith(Other, StringComparison.OrdinalIgnoreCase) ? Other
            : null;

        public LibraryDatabase Load() => LibraryDatabase.Load(AppFile, System, RootOf);

        public void Dispose() => Folder.Dispose();
    }

    [Fact]
    public void Songs_on_the_system_drive_go_in_the_app_file_and_others_on_their_own_drive()
    {
        using var drives = new Drives();
        var database = drives.Load();
        var onSystem = Song(Path.Combine(drives.System, "Music", "a.mp3"));
        var onOther = Song(Path.Combine(drives.Other, "Music", "b.mp3"));

        database.Set(onSystem, Analysed());
        database.Set(onOther, Analysed());
        database.Save();

        var driveFile = Path.Combine(drives.Other, LibraryDatabase.DRIVE_FOLDER, LibraryDatabase.FILE_NAME);
        Assert.True(File.Exists(drives.AppFile));
        Assert.True(File.Exists(driveFile));
        Assert.Contains("a.mp3", File.ReadAllText(drives.AppFile));
        Assert.DoesNotContain("b.mp3", File.ReadAllText(drives.AppFile));
        Assert.Contains("\"Music\\\\b.mp3\"", File.ReadAllText(driveFile));

        var reloaded = drives.Load();
        Assert.True(reloaded.TryGet(onSystem, out _, out _));
        Assert.True(reloaded.TryGet(onOther, out _, out _));
    }

    [Fact]
    public void What_the_app_file_knew_about_another_drive_moves_onto_it_once_it_is_plugged_in()
    {
        using var drives = new Drives();
        var onOther = Song(Path.Combine(drives.Other, "Music", "b.mp3"));

        // Learnt while the drive wasn't there, so kept in the app's own file.
        var unplugged = LibraryDatabase.Load(drives.AppFile, drives.System, _ => drives.Folder["Z"]);
        unplugged.Set(onOther, Analysed());
        unplugged.SetHotcues(onOther.Path, [TimeSpan.FromSeconds(3)]);
        unplugged.Save();
        Assert.Contains("b.mp3", File.ReadAllText(drives.AppFile));

        var database = drives.Load();

        Assert.DoesNotContain("b.mp3", File.ReadAllText(drives.AppFile));
        Assert.True(database.TryGet(onOther, out _, out _));
        Assert.Equal(TimeSpan.FromSeconds(3), database.GetHotcues(onOther.Path)[0]);
    }

    [Fact]
    public void A_drive_that_is_not_plugged_in_keeps_its_songs_in_the_app_file()
    {
        using var drives = new Drives();
        var gone = drives.Folder["G"] + Path.DirectorySeparatorChar;
        var database = LibraryDatabase.Load(drives.AppFile, drives.System, path => path.StartsWith(gone) ? gone : drives.RootOf(path));
        var song = Song(Path.Combine(gone, "c.mp3"));

        database.Set(song, Analysed());
        database.Save();

        Assert.Contains("c.mp3", File.ReadAllText(drives.AppFile));
        Assert.False(Directory.Exists(gone));
    }
}
