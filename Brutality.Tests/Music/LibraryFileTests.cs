using Brutality.Audio.Analysis;
using Brutality.Music;

namespace Brutality.Tests.Music;

public class LibraryFileTests
{
    static readonly DateTime Modified = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    internal static Song Song(string path, long size = 1000, string title = "Title", string artist = "Artist", string album = "Album") =>
        new(path, size, Modified, title, artist, album);

    internal static TrackAnalysis Analysed(double bpm = 124) =>
        new(BeatGrid.Constant(bpm, 0.25, 200).WithDownbeat(2), Loudness: -9.5, Peak: 0.98f, new MusicalKey(9, true));

    [Fact]
    public void An_analysis_comes_back_as_it_was_saved()
    {
        using var folder = new TempFolder();
        var song = Song(folder["Music", "a.mp3"]);
        var file = LibraryFile.Load(folder["library.json"]);
        file.Set(song, Analysed());
        file.Save();

        Assert.True(LibraryFile.Load(folder["library.json"]).TryGet(song, out var analysis, out var error));

        Assert.Null(error);
        Assert.Equal(Analysed(), analysis);
    }

    [Fact]
    public void A_changed_file_or_a_newer_analysis_needs_analysing_again()
    {
        using var folder = new TempFolder();
        var song = Song(folder["a.mp3"]);
        var file = LibraryFile.Load(folder["library.json"]);
        file.Set(song, Analysed());

        Assert.False(file.TryGet(Song(song.Path, size: 1001), out _, out _));
        Assert.False(file.TryGet(new Song(song.Path, 1000, Modified.AddSeconds(1), "", "", ""), out _, out _));
    }

    [Fact]
    public void A_song_that_failed_keeps_why()
    {
        using var folder = new TempFolder();
        var song = Song(folder["a.mp3"]);
        var file = LibraryFile.Load(folder["library.json"]);
        file.SetFailed(song, "Couldn't decode it");
        file.Save();

        Assert.True(LibraryFile.Load(folder["library.json"]).TryGet(song, out var analysis, out var error));

        Assert.Null(analysis);
        Assert.Equal("Couldn't decode it", error);
    }

    [Fact]
    public void A_drive_keeps_its_songs_relative_so_they_are_found_under_another_letter()
    {
        using var folder = new TempFolder();
        var driveE = folder["E"] + Path.DirectorySeparatorChar;
        var file = LibraryFile.Load(Path.Combine(driveE, "Cuepoint", "library.json"), driveE);
        file.Set(Song(Path.Combine(driveE, "Music", "a.mp3")), Analysed());
        file.SetHotcues(Path.Combine(driveE, "Music", "a.mp3"), [TimeSpan.FromSeconds(12.5), null]);
        file.Save();

        var text = File.ReadAllText(file.FilePath);
        Assert.Contains("\"Music\\\\a.mp3\"", text);
        Assert.DoesNotContain(driveE.Replace("\\", "\\\\"), text);

        // The same drive, plugged in as F.
        var driveF = folder["F"] + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(driveF, "Cuepoint"));
        File.Copy(file.FilePath, Path.Combine(driveF, "Cuepoint", "library.json"));
        var moved = LibraryFile.Load(Path.Combine(driveF, "Cuepoint", "library.json"), driveF);

        Assert.True(moved.TryGet(Song(Path.Combine(driveF, "Music", "a.mp3")), out var analysis, out _));
        Assert.Equal(Analysed(), analysis);
        Assert.Equal(TimeSpan.FromSeconds(12.5), moved.GetHotcues(Path.Combine(driveF, "Music", "a.mp3"))[0]);
    }

    [Fact]
    public void Hotcues_are_kept_and_cleared_ones_forgotten()
    {
        using var folder = new TempFolder();
        var path = folder["a.mp3"];
        var file = LibraryFile.Load(folder["library.json"]);

        file.SetHotcues(path, [null, TimeSpan.FromSeconds(1.2346), null]);
        file.Save();
        var hotcues = LibraryFile.Load(folder["library.json"]).GetHotcues(path);
        Assert.Equal(new TimeSpan?[] { null, TimeSpan.FromSeconds(1.235), null }, hotcues);

        file.SetHotcues(path, [null, null]);
        file.Save();
        Assert.Empty(LibraryFile.Load(folder["library.json"]).GetHotcues(path));
        Assert.True(LibraryFile.Load(folder["library.json"]).IsEmpty);
    }

    [Fact]
    public void A_grid_fixed_by_hand_is_used_in_place_of_the_analysed_one()
    {
        using var folder = new TempFolder();
        var song = Song(folder["a.mp3"]);
        var file = LibraryFile.Load(folder["library.json"]);
        file.Set(song, Analysed());
        var fixedGrid = BeatGrid.Constant(125, 0.3, 200).WithDownbeat(1);
        file.SetGridEdit(song.Path, new GridEdit(fixedGrid, [0.3]));
        file.Save();
        var loaded = LibraryFile.Load(folder["library.json"]);

        Assert.True(loaded.TryGet(song, out var analysis, out _));
        Assert.Equal(fixedGrid, analysis!.Grid);
        Assert.True(loaded.TryGetAnalysed(song, out var asAnalysed, out _));
        Assert.Equal(Analysed().Grid, asAnalysed!.Grid);
        Assert.Equal(new[] { 0.3 }, loaded.GetGridEdit(song.Path, 200)!.Pins);

        // Analysing again keeps the fixed grid.
        loaded.Set(song, Analysed(bpm: 126));
        Assert.True(loaded.TryGet(song, out var again, out _));
        Assert.Equal(fixedGrid, again!.Grid);

        loaded.SetGridEdit(song.Path, null);
        Assert.True(loaded.TryGet(song, out var unfixed, out _));
        Assert.Equal(126, unfixed!.Grid.Markers[0].Bpm);
    }

    [Fact]
    public void Titles_artists_and_albums_from_tags_are_kept_but_guesses_are_not()
    {
        using var folder = new TempFolder();
        var guessed = Song(folder["a.mp3"], title: "01 a");
        var file = LibraryFile.Load(folder["library.json"]);
        file.Set(guessed, Analysed());

        Assert.False(file.TryGetNames(guessed, out _, out _, out _));

        var tagged = Song(guessed.Path, title: "Real Title", artist: "Real Artist", album: "");
        tagged.HasTags = true;
        file.SetNames(tagged);
        file.Save();

        Assert.True(LibraryFile.Load(folder["library.json"]).TryGetNames(guessed, out var title, out var artist, out var album));
        Assert.Equal("Real Title", title);
        Assert.Equal("Real Artist", artist);
        Assert.Null(album);

        // Analysed again before its tags are read this time, it keeps them.
        file.Set(guessed, Analysed(bpm: 130));
        Assert.True(file.TryGetNames(guessed, out title, out _, out _));
        Assert.Equal("Real Title", title);
    }

    [Fact]
    public void A_file_of_another_version_or_that_will_not_read_starts_empty_and_says_why()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder["v2.json"], """{ "version": 2, "tracks": {} }""");
        File.WriteAllText(folder["broken.json"], "{ not json");

        var newer = LibraryFile.Load(folder["v2.json"]);
        var broken = LibraryFile.Load(folder["broken.json"]);
        var missing = LibraryFile.Load(folder["missing.json"]);

        Assert.NotNull(newer.LoadError);
        Assert.True(newer.IsEmpty);
        Assert.NotNull(broken.LoadError);
        Assert.True(broken.IsEmpty);
        Assert.Null(missing.LoadError);
        Assert.False(File.Exists(folder["missing.json"]));
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        using var folder = new TempFolder();
        var file = LibraryFile.Load(folder["Cuepoint", "library.json"]);
        file.Set(Song(folder["a.mp3"]), Analysed());

        file.Save();

        Assert.False(file.Dirty);
        Assert.Equal(new[] { "library.json" }, Directory.GetFiles(folder["Cuepoint"]).Select(Path.GetFileName));
    }
}
