using Brutality.Audio;
using Brutality.Music;
using static Brutality.Tests.Music.LibraryFileTests;

namespace Brutality.Tests.Music;

/// <summary>
/// Getting ready for a set: ratings and comments, smart playlists, idle analysis, copies and moved songs, and previews.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class PreparationTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly LibraryDatabase _database;

    public PreparationTests() => _database = LibraryDatabase.Load(_folder["library.json"], _folder.Path, _ => _folder.Path);

    public void Dispose() => _folder.Dispose();

    LibraryAnalyser Analyser(IEnumerable<Song>? songs = null) => new(songs ?? [], _database, workers: 0);

    static SongNotes Noted => new(4, "Peak time", "red");

    string Write(byte[] contents, params string[] parts)
    {
        var path = _folder.File(parts);
        File.WriteAllBytes(path, contents);
        return path;
    }

    [Fact]
    public void Notes_are_kept_between_runs_and_put_on_songs_as_they_re_looked_up()
    {
        var song = Song(_folder["a.mp3"]);
        _database.SetNotes(song.Path, Noted);
        _database.Save();

        var again = LibraryDatabase.Load(_folder["library.json"], _folder.Path, _ => _folder.Path);
        Assert.Equal(Noted, again.GetNotes(song.Path));

        var fresh = Song(song.Path);
        using var analyser = new LibraryAnalyser([fresh], again, workers: 0);
        Assert.Equal(Noted, fresh.Notes);
    }

    [Fact]
    public void Notes_taken_off_leave_nothing_to_keep()
    {
        var path = _folder["a.mp3"];
        _database.SetNotes(path, Noted);
        _database.SetNotes(path, SongNotes.None);

        Assert.True(_database.GetNotes(path).IsEmpty);
        Assert.Empty(_database.KnownPaths());
    }

    [Fact]
    public void Idle_analysis_asks_for_one_song_at_a_time()
    {
        var songs = new[] { Song(_folder["a.mp3"]), Song(_folder["b.mp3"]) };
        _database.Set(songs[0], Analysed());
        using var analyser = Analyser();

        Assert.Equal(songs[1], analyser.RequestIdle(songs));
        Assert.Equal(1, analyser.QueuedCount);
        Assert.Null(analyser.RequestIdle(songs));
        Assert.Equal(1, analyser.QueuedCount);
    }

    [Fact]
    public void Idle_analysis_passes_over_songs_that_failed()
    {
        var song = Song(_folder["a.mp3"]);
        _database.SetFailed(song, "Not audio");
        using var analyser = Analyser();

        Assert.Null(analyser.RequestIdle([song]));
    }

    [Fact]
    public void Exact_copies_are_found_and_lookalikes_are_not()
    {
        byte[] one = [1, 2, 3, 4, 5], other = [9, 9, 9, 9, 9];
        var first = Write(one, "A", "song.mp3");
        var second = Write(one, "B", "again.mp3");
        Write(other, "C", "same size.mp3");
        Write([1, 2, 3], "D", "different size.mp3");
        Write(one, "B", "._song.mp3");

        var copies = LibraryRepair.FindCopies(MusicLibrary.ListFolder(_folder.Path, recursive: true));

        var group = Assert.Single(copies);
        Assert.Equal([first, second], group);
    }

    (string Path, long Size) Gone(string name, long size) => (_folder["Old", name], size);

    IReadOnlyList<FileInfo> Under(string folder) => MusicLibrary.ListFolder(_folder[folder], recursive: true);

    [Fact]
    public void A_moved_song_is_found_by_its_name_and_size()
    {
        var moved = Write([1, 2, 3, 4, 5], "New", "Deep", "a.mp3");

        var found = LibraryRepair.FindMoved([Gone("a.mp3", 5)], Under("New"));

        Assert.Equal(1, found.Missing);
        Assert.Equal(moved, Assert.Single(found.Moves).To);
    }

    [Fact]
    public void A_file_of_the_same_name_and_another_size_is_a_different_song()
    {
        Write([1, 2, 3], "New", "a.mp3");

        var found = LibraryRepair.FindMoved([Gone("a.mp3", 5)], Under("New"));

        Assert.Equal(1, found.Missing);
        Assert.Empty(found.Moves);
    }

    [Fact]
    public void Two_files_that_could_be_the_song_leave_it_where_it_was()
    {
        Write([1, 2, 3, 4, 5], "New", "1", "a.mp3");
        Write([5, 4, 3, 2, 1], "New", "2", "a.mp3");

        var found = LibraryRepair.FindMoved([Gone("a.mp3", 5)], Under("New"));

        Assert.Empty(found.Moves);
        Assert.Equal(1, found.Ambiguous);
    }

    [Fact]
    public void Two_songs_gone_that_match_one_file_are_both_left()
    {
        Write([1, 2, 3, 4, 5], "New", "a.mp3");

        var found = LibraryRepair.FindMoved([(_folder["Old", "1", "a.mp3"], 5), (_folder["Old", "2", "a.mp3"], 5)], Under("New"));

        Assert.Empty(found.Moves);
        Assert.Equal(2, found.Ambiguous);
    }

    [Fact]
    public void A_song_on_a_drive_not_plugged_in_has_not_moved()
    {
        Write([1, 2, 3, 4, 5], "New", "a.mp3");
        var unplugged = Enumerable.Range('D', 23).Select(letter => $"{(char)letter}:\\").First(root => !Directory.Exists(root));

        var found = LibraryRepair.FindMoved([(Path.Combine(unplugged, "a.mp3"), 5)], Under("New"));

        Assert.Equal(0, found.Missing);
        Assert.Empty(found.Moves);
    }

    [Fact]
    public void Everything_known_about_a_song_follows_it_and_virtual_folders_too()
    {
        var was = _folder["Old", "a.mp3"];
        var song = Song(was);
        _database.Set(song, Analysed());
        _database.SetHotcues(was, [TimeSpan.FromSeconds(12)]);
        _database.SetMemoryCues(was, [TimeSpan.FromSeconds(3)]);
        _database.SetCuePoint(was, TimeSpan.FromSeconds(8));
        _database.SetNotes(was, Noted);

        var now = _folder.File("Music", "a.mp3");
        using var library = new MusicLibrary(_folder["Music"]);
        var moved = library.Songs[0];
        File.WriteAllBytes(now, new byte[moved.Size]);
        using var analyser = Analyser(library.Songs);
        var root = _folder.Path + "\\";
        var folders = new VirtualFolders(library, _folder["Folders"], () => [new StorageDrive(root, "C:", true)], root);
        var set = folders.Create("Set");
        set.EditablePaths.Add(was);

        var moves = new[] { new LibraryRepair.Move(was, now, moved.Modified) };
        Assert.Null(LibraryRepair.Apply(moves, _database, library, analyser, folders));

        Assert.Equal(TimeSpan.FromSeconds(12), _database.GetHotcues(now)[0]);
        Assert.Equal(TimeSpan.FromSeconds(3), _database.GetMemoryCues(now)[0]);
        Assert.Equal(TimeSpan.FromSeconds(8), _database.GetCuePoint(now));
        Assert.Equal(Noted, _database.GetNotes(now));
        Assert.Empty(_database.GetHotcues(was));
        Assert.DoesNotContain(was, _database.KnownPaths(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Noted, moved.Notes);
        Assert.Equal([now], set.Paths);
    }

    [Fact]
    public void What_a_song_already_has_at_its_new_place_stays()
    {
        var (was, now) = (_folder["Old", "a.mp3"], _folder["New", "a.mp3"]);
        _database.SetHotcues(was, [TimeSpan.FromSeconds(12)]);
        _database.SetHotcues(now, [TimeSpan.FromSeconds(40)]);
        _database.SetCuePoint(was, TimeSpan.FromSeconds(8));

        _database.Relocate(was, now, DateTime.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(40), _database.GetHotcues(now)[0]);
        Assert.Equal(TimeSpan.FromSeconds(8), _database.GetCuePoint(now));
    }

    [Fact]
    public void A_song_moved_to_another_drive_goes_into_that_drive_s_file()
    {
        var (c, d) = (_folder["C"] + "\\", _folder["D"] + "\\");
        Directory.CreateDirectory(c);
        Directory.CreateDirectory(d);
        string? RootOf(string path) => path.StartsWith(d, StringComparison.OrdinalIgnoreCase) ? d : c;
        var database = LibraryDatabase.Load(_folder["C", "library.json"], c, RootOf);
        var (was, now) = (c + "a.mp3", d + "a.mp3");
        database.SetNotes(was, Noted);

        Assert.True(database.Relocate(was, now, DateTime.UtcNow));
        database.Save();

        Assert.Equal(Noted, database.GetNotes(now));
        Assert.True(File.Exists(_folder["D", "Cuepoint", "library.json"]));
        Assert.Contains("\"a.mp3\"", File.ReadAllText(_folder["D", "Cuepoint", "library.json"]));
    }

    [Fact]
    public void Relocating_a_song_nothing_is_known_about_does_nothing()
    {
        Assert.False(_database.Relocate(_folder["Old", "a.mp3"], _folder["New", "a.mp3"], DateTime.UtcNow));
        Assert.False(_database.Dirty);
    }

    SmartPlaylists Smart(MusicLibrary library, LibraryAnalyser analyser, List<SmartPlaylist>? lists = null) => new(lists ?? [], library, _database, analyser, () => []);

    static void Pump(MusicLibrary library, Func<bool> until)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && watch.Elapsed.TotalSeconds < 5)
        {
            library.Background.Apply();
            Thread.Sleep(5);
        }
        Assert.True(until());
    }

    [Fact]
    public void A_smart_playlist_lists_library_songs_that_match_as_they_change()
    {
        _folder.File("Music", "a.mp3");
        _folder.File("Music", "b.mp3");
        using var library = new MusicLibrary(_folder["Music"]);
        using var analyser = Analyser(library.Songs);
        var smart = Smart(library, analyser);
        var list = smart.Add("Bangers", "rating:4");

        Assert.Empty(smart.SongsIn(list));

        library.Songs[1].Notes = new SongNotes(5);
        Assert.Equal([library.Songs[1]], smart.SongsIn(list));
    }

    [Fact]
    public void A_smart_playlist_finds_songs_the_database_knows_from_folders_not_opened()
    {
        var elsewhere = _folder.File("Elsewhere", "b.mp3");
        _database.SetNotes(elsewhere, Noted);
        Directory.CreateDirectory(_folder["Music"]);
        using var library = new MusicLibrary(_folder["Music"]);
        using var analyser = Analyser();
        var smart = Smart(library, analyser);
        var list = smart.Add("Red", "color:red");

        Pump(library, () => smart.SongsIn(list).Count == 1);

        Assert.Equal(elsewhere, smart.SongsIn(list)[0].Path);
    }

    [Fact]
    public void A_smart_playlist_has_a_name_of_its_own_and_is_kept_in_the_list_it_was_given()
    {
        Directory.CreateDirectory(_folder["Music"]);
        using var library = new MusicLibrary(_folder["Music"]);
        using var analyser = Analyser();
        var kept = new List<SmartPlaylist>();
        var smart = Smart(library, analyser, kept);
        var changes = 0;
        smart.Changed += () => changes++;

        var zed = smart.Add("Zed", "rating:1");
        smart.Add("Alpha", "rating:2");

        Assert.Equal(["Alpha", "Zed"], kept.Select(list => list.Name));
        Assert.NotNull(smart.CheckName("alpha"));
        Assert.NotNull(smart.CheckName("  "));
        Assert.Null(smart.CheckName("zed", zed));
        smart.Delete(zed);
        Assert.Equal(3, changes);
        Assert.Single(kept);
    }

    [Fact]
    public void A_smart_playlist_has_to_ask_for_something()
    {
        Assert.NotNull(SmartPlaylists.CheckSearch("   "));
        Assert.Null(SmartPlaylists.CheckSearch("rating:3"));
    }

    [Fact]
    public void A_song_that_will_not_load_is_reported_and_the_preview_stops()
    {
        using var engine = new AudioEngine();
        using var preview = new SongPreview(engine);
        var song = Song(_folder.File("not audio.mp3"));
        string? failure = null;
        preview.Failed += (_, error) => failure = error;

        preview.Start(song);
        Assert.Equal(song, preview.Song);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (failure is null && watch.Elapsed.TotalSeconds < 10)
        {
            preview.Update();
            Thread.Sleep(5);
        }

        Assert.NotNull(failure);
        Assert.Null(preview.Song);
    }

    [Fact]
    public void Stopping_a_preview_while_it_loads_leaves_it_quiet()
    {
        using var engine = new AudioEngine();
        using var preview = new SongPreview(engine);

        preview.Start(Song(_folder.File("not audio.mp3")));
        preview.Stop();

        Assert.Null(preview.Song);
        Assert.False(preview.IsPlaying);
        Assert.Equal(0, preview.Fraction);
    }
}
