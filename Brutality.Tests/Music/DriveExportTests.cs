using System.Text;
using Brutality.Audio.Analysis;
using Brutality.Music;

namespace Brutality.Tests.Music;

public sealed class DriveExportTests : IDisposable
{
    readonly TempFolder _folder = new();
    string System => _folder["C"] + Path.DirectorySeparatorChar;
    string Drive => _folder["F"] + Path.DirectorySeparatorChar;

    public DriveExportTests()
    {
        Directory.CreateDirectory(System);
        Directory.CreateDirectory(Drive);
    }

    public void Dispose() => _folder.Dispose();

    string? RootOf(string path) =>
        path.StartsWith(System, StringComparison.OrdinalIgnoreCase) ? System
        : path.StartsWith(Drive, StringComparison.OrdinalIgnoreCase) ? Drive
        : null;

    LibraryDatabase Database() => LibraryDatabase.Load(Path.Combine(System, "AppData", "library.json"), System, RootOf);

    Song Song(string relative, int bytes = 4096, string? root = null)
    {
        var path = Path.Combine(root ?? System, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i * 7)).ToArray());
        var info = new FileInfo(path);
        return new Song(path, info.Length, info.LastWriteTimeUtc, Path.GetFileNameWithoutExtension(path), "Artist", "Album");
    }

    DriveExport Export(string name, params Song[] songs) => new(name, songs, Drive, _ => long.MaxValue);

    [Fact]
    public void Songs_are_copied_whole_in_order_to_a_folder_of_the_list_s_name()
    {
        var songs = new[] { Song(@"Music\b.mp3", 3000), Song(@"Music\a.m4a", 2_500_000) };
        var export = Export("Friday", songs);

        var result = export.Run();

        Assert.Null(result.Error);
        Assert.False(result.Cancelled);
        Assert.Equal(new[] { Path.Combine(Drive, "Music", "Friday", "b.mp3"), Path.Combine(Drive, "Music", "Friday", "a.m4a") }, result.Songs.Select(song => song.Path));
        Assert.All(result.Songs, song => Assert.Equal(File.ReadAllBytes(song.Source.Path), File.ReadAllBytes(song.Path)));
        Assert.Equal(1, export.Fraction);
        Assert.Empty(Directory.GetFiles(export.Folder, "*.part"));
    }

    [Fact]
    public void Two_songs_of_one_name_each_get_their_own_file_and_keep_them_when_exported_again()
    {
        var songs = new[] { Song(@"One\intro.mp3", 1000), Song(@"Two\intro.mp3", 2000) };

        var first = Export("Set", songs).Run();
        var written = File.GetLastWriteTimeUtc(first.Songs[0].Path);
        var again = Export("Set", songs).Run();

        Assert.Equal(new[] { "intro.mp3", "intro (2).mp3" }, first.Songs.Select(song => Path.GetFileName(song.Path)));
        Assert.Equal(first.Songs.Select(song => song.Path), again.Songs.Select(song => song.Path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(again.Songs[0].Path));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Drive, "Music", "Set")).Length);
    }

    [Fact]
    public void A_song_already_on_the_drive_stays_where_it_is()
    {
        var there = Song(@"Tunes\here.mp3", root: Drive);

        var result = Export("Set", there).Run();

        Assert.Equal(there.Path, Assert.Single(result.Songs).Path);
        Assert.False(Directory.Exists(Path.Combine(Drive, "Music")));
    }

    [Fact]
    public void A_song_gone_is_counted_and_the_rest_still_copied()
    {
        var gone = Song(@"Music\gone.mp3");
        var kept = Song(@"Music\kept.mp3");
        File.Delete(gone.Path);

        var result = Export("Set", gone, kept).Run();

        Assert.Equal(new[] { gone.Path }, result.Failed);
        Assert.Equal("kept.mp3", Path.GetFileName(Assert.Single(result.Songs).Path));
    }

    [Fact]
    public void Too_little_room_says_so_and_copies_nothing()
    {
        var result = new DriveExport("Set", [Song(@"Music\big.mp3", 5000)], Drive, _ => 1000).Run();

        Assert.Contains("free", result.Error);
        Assert.False(Directory.Exists(Path.Combine(Drive, "Music")));
    }

    [Fact]
    public void Stopped_before_it_starts_it_copies_nothing()
    {
        var export = Export("Set", Song(@"Music\a.mp3"));

        export.Cancel();
        var result = export.Run();

        Assert.True(result.Cancelled);
        Assert.False(Directory.Exists(Path.Combine(Drive, "Music")));
        Assert.True(export.Wait(TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Friday: late/night", "Friday- late-night")]
    [InlineData("Ends with a dot.", "Ends with a dot")]
    [InlineData("...", "Export")]
    public void A_list_s_name_is_made_fit_for_a_file(string name, string safe) => Assert.Equal(safe, DriveExport.SafeName(name));

    [Fact]
    public void The_drive_s_library_file_learns_what_s_known_about_each_song()
    {
        var song = Song(@"Music\a.mp3");
        var database = Database();
        var analysis = new TrackAnalysis(BeatGrid.Constant(124, 0.25, 200), -9.5, 0.9f, new MusicalKey(9, true));
        database.Set(song, analysis);
        database.SetHotcues(song.Path, [TimeSpan.FromSeconds(12)]);
        database.SetMemoryCues(song.Path, [TimeSpan.FromSeconds(3)]);
        database.SetGridEdit(song.Path, new GridEdit(BeatGrid.Constant(125, 0.5, 200), []));

        var result = Export("Set", song).Run();
        DriveExport.Record(database, result.Songs);
        database.Save();

        var copy = result.Songs[0];
        var again = LibraryFile.Load(Path.Combine(Drive, LibraryDatabase.DRIVE_FOLDER, LibraryDatabase.FILE_NAME), Drive);
        Assert.True(again.TryGetAnalysed(new Song(copy.Path, copy.Size, copy.Modified, "", "", ""), out var saved, out _));
        Assert.Equal(analysis.Grid, saved!.Grid);
        Assert.Equal(TimeSpan.FromSeconds(12), again.GetHotcues(copy.Path)[0]);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, again.GetMemoryCues(copy.Path));
        Assert.Equal(125, again.GetGridEdit(copy.Path, 200)!.Grid.Markers[0].Bpm);
        // Kept relative to the drive, so it's found under any letter.
        Assert.Contains(@"Music\\Set\\a.mp3", File.ReadAllText(again.FilePath));
        Assert.DoesNotContain(_folder.Path.Replace(@"\", @"\\"), File.ReadAllText(again.FilePath));
    }

    [Fact]
    public void A_copy_s_own_hot_cues_from_an_earlier_export_are_kept()
    {
        var song = Song(@"Music\a.mp3");
        var database = Database();
        database.SetHotcues(song.Path, [TimeSpan.FromSeconds(12)]);
        var result = Export("Set", song).Run();
        database.SetHotcues(result.Songs[0].Path, [TimeSpan.FromSeconds(40)]);

        DriveExport.Record(database, result.Songs);

        Assert.Equal(TimeSpan.FromSeconds(40), database.GetHotcues(result.Songs[0].Path)[0]);
    }

    [Fact]
    public void A_drive_gets_a_library_file_even_with_nothing_known_yet()
    {
        var database = Database();

        Assert.True(database.EnsureDriveFile(Drive));
        Assert.True(File.Exists(Path.Combine(Drive, LibraryDatabase.DRIVE_FOLDER, LibraryDatabase.FILE_NAME)));
    }

    [Fact]
    public void The_playlist_goes_on_the_drive_as_a_virtual_folder_with_paths_relative_to_it()
    {
        using var library = new MusicLibrary(System);
        var folders = new VirtualFolders(library, Path.Combine(System, "Folders"), () => [new StorageDrive(System, "C:", true), new StorageDrive(Drive, "F:", true)], System);
        var result = Export("Set", Song(@"Music\a.mp3")).Run();

        Assert.False(folders.IsOnDrive("Set", Drive));
        Assert.Null(folders.WriteToDrive("Set", Drive, result.Songs.Select(song => song.Path).ToList()));

        Assert.True(folders.IsOnDrive("Set", Drive));
        var folder = Assert.Single(folders.All);
        Assert.Equal(Drive, folder.Drive);
        Assert.Equal(result.Songs[0].Path, Assert.Single(folder.Paths));
        var lines = File.ReadAllLines(folder.FilePath, Encoding.UTF8);
        Assert.Equal(new[] { "#EXTM3U", @"..\..\Music\Set\a.mp3" }, lines);

        // Exported again, it's the same virtual folder with a new list.
        Assert.Null(folders.WriteToDrive("Set", Drive, []));
        Assert.Empty(Assert.Single(folders.All).Paths);
    }
}
