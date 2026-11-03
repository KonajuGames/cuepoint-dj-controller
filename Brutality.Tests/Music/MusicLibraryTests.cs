using Brutality.Music;

namespace Brutality.Tests.Music;

public class MusicLibraryTests
{
    /// <summary>
    /// A small library: a song at the top, two in an artist's album, one under a grouping folder, and a file that
    /// isn't music.
    /// </summary>
    static TempFolder Library()
    {
        var folder = new TempFolder();
        folder.File("Top.mp3");
        folder.File("Daft Punk", "Discovery", "01 - One More Time.mp3");
        folder.File("Daft Punk", "Discovery", "03 Daft Punk - Digital Love.m4a");
        folder.File("_CDs", "Moby", "Play", "02. Porcelain.flac");
        folder.File("Daft Punk", "cover.jpg");
        return folder;
    }

    [Fact]
    public void Lists_only_the_songs_directly_in_its_folder_unless_recursive()
    {
        using var folder = Library();

        using var flat = new MusicLibrary(folder.Path);
        using var deep = new MusicLibrary(folder.Path, recursive: true);

        Assert.Equal(new[] { "Top" }, flat.Songs.Select(song => song.Title));
        Assert.Equal(4, deep.Songs.Count);
        Assert.True(deep.Recursive);
        Assert.All(deep.Songs, song => Assert.True(song.InLibrary));
    }

    [Fact]
    public void Guesses_artist_album_and_title_from_where_a_song_is()
    {
        using var folder = Library();
        using var library = new MusicLibrary(folder.Path, recursive: true);

        var oneMoreTime = library.Find(folder["Daft Punk", "Discovery", "01 - One More Time.mp3"])!;
        var digitalLove = library.Find(folder["Daft Punk", "Discovery", "03 Daft Punk - Digital Love.m4a"])!;
        var porcelain = library.Find(folder["_CDs", "Moby", "Play", "02. Porcelain.flac"])!;

        Assert.Equal(("One More Time", "Daft Punk", "Discovery"), (oneMoreTime.Title, oneMoreTime.Artist, oneMoreTime.Album));
        Assert.Equal("Digital Love", digitalLove.Title);
        Assert.Equal(("Porcelain", "Moby", "Play"), (porcelain.Title, porcelain.Artist, porcelain.Album));
    }

    [Fact]
    public void Scanning_another_folder_moves_the_library_but_keeps_the_songs_it_knew()
    {
        using var folder = Library();
        using var library = new MusicLibrary(folder.Path, recursive: true);
        var top = library.Find(folder["Top.mp3"])!;
        var porcelain = library.Find(folder["_CDs", "Moby", "Play", "02. Porcelain.flac"])!;

        library.Scan(folder["_CDs"], recursive: true);

        Assert.Equal(folder["_CDs"], library.Root);
        Assert.Same(porcelain, Assert.Single(library.Songs));
        Assert.True(porcelain.InLibrary);
        Assert.False(top.InLibrary);
        Assert.Same(top, library.Find(folder["Top.mp3"]));
    }

    [Fact]
    public void A_folder_that_is_not_there_gives_an_empty_library_and_says_why()
    {
        using var folder = new TempFolder();

        using var library = new MusicLibrary(folder["Nowhere"]);

        Assert.Empty(library.Songs);
        Assert.NotNull(library.Error);
    }

    [Fact]
    public void Browsing_a_folder_finds_its_songs_once_each()
    {
        using var folder = Library();
        using var library = new MusicLibrary(folder["Daft Punk"]);

        var album = library.SongsIn(folder["Daft Punk", "Discovery"]);
        var again = library.SongsIn(folder["Daft Punk", "Discovery"]);
        var everything = library.SongsIn(folder.Path, recursive: true);

        Assert.Equal(2, album.Count);
        Assert.Equal(album, again);
        Assert.Equal(4, everything.Count);
        Assert.Contains(album[0], everything);
        Assert.All(album, song => Assert.False(song.InLibrary));
        Assert.Empty(library.SongsIn(folder["Nowhere"]));
    }

    [Theory]
    [InlineData("daft", true)]
    [InlineData("love DISCOVERY", true)]
    [InlineData("love moby", false)]
    [InlineData("", true)]
    public void A_search_matches_every_word_in_the_title_artist_or_album(string query, bool matches)
    {
        var song = LibraryFileTests.Song("x.mp3", title: "Digital Love", artist: "Daft Punk", album: "Discovery");

        Assert.Equal(matches, song.Matches(query));
    }

    /// <summary>
    /// Takes in a background scan once it's done, as the app does a frame at a time; false if it didn't finish in time.
    /// </summary>
    static bool ApplyWithin(MusicLibrary library, double seconds = 5)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            if (library.ApplyScan())
                return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [Fact]
    public void A_library_made_without_scanning_is_empty_until_scanned_in_the_background()
    {
        using var folder = Library();
        using var library = new MusicLibrary(folder.Path, recursive: true, scan: false);
        var scanned = 0;

        Assert.Empty(library.Songs);
        Assert.Equal(folder.Path, library.Root);
        library.BeginScan(library.Root, library.Recursive, () => scanned++);
        Assert.True(library.IsScanning);

        Assert.True(ApplyWithin(library));
        Assert.Equal(4, library.Songs.Count);
        Assert.All(library.Songs, song => Assert.True(song.InLibrary));
        Assert.Equal(1, scanned);
        Assert.False(library.IsScanning);
        Assert.False(library.ApplyScan());
    }

    [Fact]
    public void A_newer_scan_overtakes_one_still_going()
    {
        using var folder = Library();
        using var library = new MusicLibrary(folder.Path, scan: false);
        var done = new List<string>();

        library.BeginScan(folder.Path, recursive: true, () => done.Add("everything"));
        library.BeginScan(folder["Daft Punk", "Discovery"], recursive: false, () => done.Add("the album"));

        Assert.True(ApplyWithin(library));
        Assert.Equal(new[] { "the album" }, done);
        Assert.Equal(2, library.Songs.Count);
        Assert.Equal(folder["Daft Punk", "Discovery"], library.Root);
    }

    [Fact]
    public void Scanning_straight_away_overtakes_a_scan_in_the_background()
    {
        using var folder = Library();
        using var library = new MusicLibrary(folder.Path, scan: false);
        var scanned = false;
        library.BeginScan(folder.Path, recursive: true, () => scanned = true);

        library.Scan(folder.Path, recursive: false);

        Assert.False(library.IsScanning);
        Assert.Equal(new[] { "Top" }, library.Songs.Select(song => song.Title));
        Thread.Sleep(200);
        Assert.False(library.ApplyScan());
        Assert.False(scanned);
    }

    [Fact]
    public void A_scan_in_the_background_keeps_the_songs_it_knew_and_forgets_those_gone()
    {
        using var folder = Library();
        using var library = new MusicLibrary(folder.Path);
        folder.File("New.mp3");
        File.Delete(folder["Top.mp3"]);

        library.BeginScan(folder.Path);
        Assert.True(ApplyWithin(library));

        Assert.Equal(new[] { "New" }, library.Songs.Select(song => song.Title));
        Assert.Null(library.Find(folder["Top.mp3"]));
    }
}
