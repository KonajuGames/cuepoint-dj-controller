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
}
