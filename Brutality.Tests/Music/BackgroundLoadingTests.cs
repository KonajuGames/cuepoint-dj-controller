using Brutality.Audio.Analysis;
using Brutality.Music;

namespace Brutality.Tests.Music;

/// <summary>
/// Virtual folders and playlists looked for in the background, the library saved there, and the main tempo kept.
/// </summary>
public sealed class BackgroundLoadingTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly MusicLibrary _library;

    public BackgroundLoadingTests()
    {
        _folder.File("Music", "a.mp3");
        _folder.File("Elsewhere", "b.mp3");
        _library = new MusicLibrary(_folder["Music"]);
    }

    public void Dispose()
    {
        _library.Dispose();
        _folder.Dispose();
    }

    void Pump(Func<bool> until)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && watch.Elapsed.TotalSeconds < 5)
        {
            _library.Background.Apply();
            Thread.Sleep(5);
        }
        Assert.True(until());
    }

    [Fact]
    public void Work_that_goes_wrong_tells_what_s_waiting_says_why_and_leaves_the_rest_to_come()
    {
        var work = new BackgroundWork();
        var (failed, finished, other) = (false, false, false);
        work.Start<int>(() => throw new InvalidDataException("broken"), _ => finished = true, () => failed = true);
        work.Start(() => 1, _ => other = true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var problems = 0;

        while (work.IsBusy && watch.Elapsed.TotalSeconds < 5)
        {
            try
            {
                work.Apply();
            }
            catch (InvalidDataException)
            {
                problems++;
            }
            Thread.Sleep(5);
        }

        Assert.Equal(1, problems);
        Assert.True(failed);
        Assert.False(finished);
        Assert.True(other);
    }

    [Fact]
    public void Songs_at_paths_are_found_in_order_those_elsewhere_included_and_those_gone_left_out()
    {
        IReadOnlyList<Song>? found = null;

        _library.LoadSongsAt([_folder["Elsewhere", "b.mp3"], _folder["Gone", "x.mp3"], _folder["Music", "a.mp3"]], songs => found = songs);
        Assert.Null(found);
        Pump(() => found is not null);

        Assert.Equal(new[] { _folder["Elsewhere", "b.mp3"], _folder["Music", "a.mp3"] }, found!.Select(song => song.Path));
    }

    LibraryNode VirtualFolderNode(out VirtualFolder folder, out VirtualFolders folders)
    {
        Directory.CreateDirectory(_folder["C"]);
        var root = _folder["C"] + Path.DirectorySeparatorChar;
        folders = new VirtualFolders(_library, _folder["C", "Folders"], () => [new StorageDrive(root, "C:", true)], root);
        folder = folders.Create("Set");
        Assert.Null(folders.Add(folder, [_library.Find(_folder["Music", "a.mp3"])!]).Error);
        var nodes = LibraryTree.Build(_library, Array.Empty<Brutality.Audio.Deck>(), [], folders, _ => false);
        return LibraryTree.Find(nodes, ["Virtual folders", "Set"])!;
    }

    [Fact]
    public void A_virtual_folder_loads_in_the_background_and_keeps_its_songs_showing_while_it_looks_again()
    {
        var node = VirtualFolderNode(out var folder, out var folders);

        Assert.Empty(node.Songs);
        Assert.True(node.IsLoadingSongs);
        Pump(() => !node.IsLoadingSongs);
        Assert.Single(node.Songs);

        var extra = _library.SongsIn(_folder["Elsewhere"])[0];
        Assert.Null(folders.Add(folder, [extra]).Error);
        node.RefreshSongs();

        Assert.Single(node.Songs);
        Assert.True(node.IsLoadingSongs);
        Pump(() => !node.IsLoadingSongs);
        Assert.Equal(2, node.Songs.Count);
    }

    LibraryDatabase Database() => LibraryDatabase.Load(_folder["App", "library.json"], _folder.Path, _ => _folder.Path);

    static bool Finish(LibraryDatabase database, out string? error)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        error = null;
        while (watch.Elapsed.TotalSeconds < 5)
        {
            if (database.FinishSave(out error))
                return true;
            Thread.Sleep(5);
        }
        return false;
    }

    [Fact]
    public void A_background_save_writes_what_was_there_when_it_began_and_later_changes_stay_to_save()
    {
        var database = Database();
        database.SetHotcues(_folder["Music", "a.mp3"], [TimeSpan.FromSeconds(1)]);

        database.BeginSave();
        Assert.True(database.IsSaving);
        database.SetHotcues(_folder["Music", "a.mp3"], [TimeSpan.FromSeconds(2)]);
        Assert.True(Finish(database, out var error));

        Assert.Null(error);
        Assert.False(database.IsSaving);
        Assert.True(database.Dirty);
        Assert.Equal(TimeSpan.FromSeconds(1), Database().GetHotcues(_folder["Music", "a.mp3"])[0]);
    }

    [Fact]
    public void Saving_outright_waits_for_a_background_save_so_the_latest_is_what_stays()
    {
        var database = Database();
        database.SetHotcues(_folder["Music", "a.mp3"], [TimeSpan.FromSeconds(1)]);
        database.BeginSave();
        database.SetHotcues(_folder["Music", "a.mp3"], [TimeSpan.FromSeconds(2)]);

        database.Save();

        Assert.False(database.IsSaving);
        Assert.False(database.Dirty);
        Assert.Equal(TimeSpan.FromSeconds(2), Database().GetHotcues(_folder["Music", "a.mp3"])[0]);
    }

    [Fact]
    public void A_background_save_that_fails_says_why_and_stays_to_save()
    {
        var database = Database();
        database.SetHotcues(_folder["Music", "a.mp3"], [TimeSpan.FromSeconds(1)]);
        // A folder where its file's folder should be.
        File.WriteAllText(_folder["App"], "");

        database.BeginSave();

        Assert.True(Finish(database, out var error));
        Assert.NotNull(error);
        Assert.True(database.Dirty);
    }

    [Fact]
    public void A_grid_s_main_tempo_is_the_one_covering_most_of_it()
    {
        var grid = new BeatGrid([new GridMarker(0, 100), new GridMarker(10, 130), new GridMarker(200, 90)], 210);

        Assert.Equal(130, grid.MainBpm);
        Assert.Equal(130, grid.MainBpm);
    }
}
