using Brutality.Music;

namespace Brutality.Tests.Music;

public class VirtualFoldersTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly MusicLibrary _library;

    public VirtualFoldersTests()
    {
        Directory.CreateDirectory(System);
        Directory.CreateDirectory(DriveF);
        _folder.File("F", "Music", "a.mp3");
        _folder.File("F", "Music", "b.mp3");
        _folder.File("F", "Music", "c.mp3");
        _folder.File("C", "Music", "d.mp3");
        _library = new MusicLibrary(_folder["F", "Music"]);
    }

    public void Dispose()
    {
        _library.Dispose();
        _folder.Dispose();
    }

    string System => _folder["C"] + Path.DirectorySeparatorChar;
    string DriveF => _folder["F"] + Path.DirectorySeparatorChar;
    string AppFolder => _folder["C", "AppData", "VirtualFolders"];

    VirtualFolders Open(params string[] otherDrives) =>
        new(
            _library,
            AppFolder,
            () => [new StorageDrive(System, "C:", true), .. otherDrives.Select(root => new StorageDrive(root, root, true))],
            System
        );

    Song SongAt(params string[] parts) => _library.Find(_folder[parts]) ?? _library.SongsIn(Path.GetDirectoryName(_folder[parts])!).First(song => song.Path == _folder[parts]);

    [Fact]
    public void A_new_folder_is_saved_straight_away_and_found_again()
    {
        var folders = Open(DriveF);
        var made = folders.Create("Warm up");
        folders.Add(made, [SongAt("F", "Music", "a.mp3"), SongAt("C", "Music", "d.mp3")]);

        var reopened = Assert.Single(Open(DriveF).All);

        Assert.Equal("Warm up", reopened.Name);
        Assert.True(File.Exists(Path.Combine(AppFolder, "Warm up.m3u8")));
        Assert.Equal(new[] { _folder["F", "Music", "a.mp3"], _folder["C", "Music", "d.mp3"] }, reopened.Paths);
    }

    [Fact]
    public void Adding_a_song_twice_adds_it_once()
    {
        var folders = Open();
        var made = folders.Create("Set");
        var a = SongAt("F", "Music", "a.mp3");

        Assert.Equal(1, folders.Add(made, [a]).Added);
        Assert.Equal(0, folders.Add(made, [a]).Added);
        Assert.Single(made.Paths);
    }

    [Fact]
    public void Names_must_be_new_and_usable()
    {
        var folders = Open();
        folders.Create("New folder");

        Assert.NotNull(folders.CheckName("new FOLDER"));
        Assert.NotNull(folders.CheckName(""));
        Assert.NotNull(folders.CheckName("a/b"));
        Assert.Null(folders.CheckName("Peak time"));
        Assert.Equal("New folder 2", folders.UnusedName());
        Assert.Throws<InvalidOperationException>(() => folders.Create("New folder"));
    }

    [Fact]
    public void Renaming_renames_its_file()
    {
        var folders = Open();
        var made = folders.Create("Old");

        folders.Rename(made, "New");

        Assert.Equal("New", made.Name);
        Assert.False(File.Exists(Path.Combine(AppFolder, "Old.m3u8")));
        Assert.True(File.Exists(Path.Combine(AppFolder, "New.m3u8")));
    }

    [Fact]
    public void Kept_on_a_drive_its_songs_there_are_written_relative_and_found_under_another_letter()
    {
        var folders = Open(DriveF);
        var made = folders.Create("Gig");
        folders.Add(made, [SongAt("F", "Music", "a.mp3"), SongAt("C", "Music", "d.mp3")]);

        folders.MoveToDrive(made, DriveF);

        var file = Path.Combine(DriveF, "Cuepoint", "VirtualFolders", "Gig.m3u8");
        Assert.Equal(file, made.FilePath);
        Assert.False(File.Exists(Path.Combine(AppFolder, "Gig.m3u8")));
        var lines = File.ReadAllLines(file);
        Assert.Contains(Path.Combine("..", "..", "Music", "a.mp3"), lines);
        Assert.Contains(_folder["C", "Music", "d.mp3"], lines);

        // The drive comes back as G.
        var driveG = _folder["G"] + Path.DirectorySeparatorChar;
        Directory.Move(DriveF, driveG);
        var moved = Assert.Single(Open(driveG).All);

        Assert.Equal(driveG, moved.Drive);
        Assert.Equal(new[] { _folder["G", "Music", "a.mp3"], _folder["C", "Music", "d.mp3"] }, moved.Paths);
    }

    [Fact]
    public void A_drive_taken_out_takes_its_folders_with_it_and_brings_them_back()
    {
        var plugged = true;
        var folders = new VirtualFolders(
            _library,
            AppFolder,
            () => plugged ? [new StorageDrive(System, "C:", true), new StorageDrive(DriveF, "F:", true)] : [new StorageDrive(System, "C:", true)],
            System
        );
        folders.MoveToDrive(folders.Create("Gig"), DriveF);

        plugged = false;
        Assert.True(folders.Rescan());
        Assert.Empty(folders.All);

        plugged = true;
        Assert.True(folders.Rescan());
        Assert.Equal("Gig", Assert.Single(folders.All).Name);
        Assert.False(folders.Rescan());
    }

    [Fact]
    public void Songs_move_and_come_out_in_their_new_order()
    {
        var folders = Open();
        var made = folders.Create("Order");
        Song a = SongAt("F", "Music", "a.mp3"), b = SongAt("F", "Music", "b.mp3"), c = SongAt("F", "Music", "c.mp3");
        folders.Add(made, [a, b, c]);

        folders.Move(made, [c], 0);
        Assert.Equal(new[] { c, a, b }, folders.SongsIn(made));

        folders.Move(made, [c, a], 3);
        Assert.Equal(new[] { b, c, a }, folders.SongsIn(made));

        folders.Remove(made, [c]);
        Assert.Equal(new[] { b, a }, folders.SongsIn(made));
        Assert.Equal(2, Assert.Single(Open().All).Paths.Count);
    }

    [Fact]
    public void A_song_whose_file_has_gone_is_left_out_of_the_list_but_kept_in_the_folder()
    {
        var folders = Open();
        var made = folders.Create("Gone");
        folders.Add(made, [SongAt("F", "Music", "a.mp3"), SongAt("F", "Music", "b.mp3")]);

        File.Delete(_folder["F", "Music", "a.mp3"]);

        Assert.Equal(new[] { _folder["F", "Music", "b.mp3"] }, folders.SongsIn(made).Select(song => song.Path));
        Assert.Equal(2, made.Paths.Count);
    }

    [Fact]
    public void Deleting_removes_its_file_but_not_its_songs()
    {
        var folders = Open();
        var made = folders.Create("Temp");
        folders.Add(made, [SongAt("F", "Music", "a.mp3")]);

        folders.Delete(made);

        Assert.Empty(folders.All);
        Assert.False(File.Exists(made.FilePath));
        Assert.True(File.Exists(_folder["F", "Music", "a.mp3"]));
    }
}
