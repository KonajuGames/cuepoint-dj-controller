using Brutality.Audio.Analysis;
using Brutality.Music;

namespace Brutality.Tests.Music;

public class SongQueryTests
{
    [Fact]
    public void A_rating_finds_songs_rated_that_or_more()
    {
        var query = SongQuery.Parse("rating:4");
        var (three, four, five) = (Song("Three"), Song("Four"), Song("Five"));
        three.Notes = new SongNotes(3);
        four.Notes = new SongNotes(4);
        five.Notes = new SongNotes(5);

        Assert.False(query.IsEmpty);
        Assert.False(query.NeedsAnalysis);
        Assert.False(query.Matches(three));
        Assert.True(query.Matches(four));
        Assert.True(query.Matches(five));
    }

    [Theory]
    [InlineData("color:red")]
    [InlineData("colour:RED")]
    public void A_colour_finds_songs_tagged_with_it(string search)
    {
        var (red, blue) = (Song("Red"), Song("Blue"));
        red.Notes = new SongNotes(Color: "red");
        blue.Notes = new SongNotes(Color: "blue");

        var query = SongQuery.Parse(search);

        Assert.True(query.Matches(red));
        Assert.False(query.Matches(blue));
    }

    [Fact]
    public void A_comment_is_found_by_its_own_word_or_a_plain_one()
    {
        var song = Song("Something");
        song.Notes = new SongNotes(Comment: "Great opener");

        Assert.True(SongQuery.Parse("comment:opener").Matches(song));
        Assert.True(SongQuery.Parse("opener").Matches(song));
        Assert.False(SongQuery.Parse("comment:closer").Matches(song));
        Assert.False(SongQuery.Parse("comment:something").Matches(song));
    }

    [Fact]
    public void Notes_go_together_with_the_rest()
    {
        var song = Song("Disco", 124);
        song.Notes = new SongNotes(4, Color: "red");

        Assert.True(SongQuery.Parse("disco 120-126 rating:3 color:red").Matches(song));
        Assert.False(SongQuery.Parse("disco 130-135 rating:3").Matches(song));
        Assert.False(SongQuery.Parse("disco rating:5").Matches(song));
    }

    [Fact]
    public void Quoted_notes_are_text_to_find()
    {
        var song = Song("rating:4 mix");

        Assert.Equal(["rating:4"], SongQuery.Parse("\"rating:4\"").Words);
        Assert.True(SongQuery.Parse("\"rating:4\"").Matches(song));
    }

    static Song Song(string title, double? bpm = null, MusicalKey? key = null, string artist = "Artist")
    {
        var song = LibraryFileTests.Song($@"C:\Music\{title}.mp3", title: title, artist: artist);
        if (bpm is { } tempo)
            song.Analysis = new TrackAnalysis(BeatGrid.Constant(tempo, 0.1, 200), -9, 0.9f, key ?? new MusicalKey(9, true));
        return song;
    }

    // 8A is A minor; 9A E minor, 7A D minor, 8B C major, 3B D flat major.
    static readonly MusicalKey AMinor = new(9, true);
    static readonly MusicalKey EMinor = new(4, true);
    static readonly MusicalKey DMinor = new(2, true);
    static readonly MusicalKey CMajor = new(0, false);
    static readonly MusicalKey DFlatMajor = new(1, false);

    [Theory]
    [InlineData("120-126")]
    [InlineData("126-120")]
    [InlineData("120 - 126")]
    [InlineData("120\u2013126")]
    [InlineData("120-126BPM")]
    public void A_tempo_range_is_written_either_way_round_with_or_without_spaces(string text)
    {
        var query = SongQuery.Parse(text);

        Assert.Equal((120.0, 126.0), (query.MinBpm, query.MaxBpm));
        Assert.Empty(query.Words);
    }

    [Fact]
    public void A_range_finds_songs_by_the_tempo_shown_including_its_ends()
    {
        var query = SongQuery.Parse("120-126");

        Assert.True(query.Matches(Song("Low end", 119.96)));
        Assert.True(query.Matches(Song("High end", 126)));
        Assert.False(query.Matches(Song("Too fast", 126.1)));
        Assert.False(query.Matches(Song("Not analysed")));
    }

    [Fact]
    public void One_tempo_finds_songs_within_half_a_beat_a_minute()
    {
        var query = SongQuery.Parse("124bpm");

        Assert.True(query.Matches(Song("Near", 124.4)));
        Assert.False(query.Matches(Song("Off", 125)));
    }

    [Fact]
    public void A_Camelot_key_finds_that_key_and_with_a_tilde_those_that_mix_with_it()
    {
        var exact = SongQuery.Parse("8a");
        var mixes = SongQuery.Parse("~8A");

        Assert.True(exact.Matches(Song("Same", 124, AMinor)));
        Assert.False(exact.Matches(Song("Relative", 124, CMajor)));
        foreach (var key in new[] { AMinor, EMinor, DMinor, CMajor })
            Assert.True(mixes.Matches(Song("Mixes", 124, key)));
        Assert.False(mixes.Matches(Song("Clashes", 124, DFlatMajor)));
    }

    [Fact]
    public void Words_tempo_and_key_go_together_and_anything_else_is_a_word()
    {
        var query = SongQuery.Parse("disco 1999 120-126 ~8A 13B");

        Assert.Equal(new[] { "disco", "1999", "13B" }, query.Words);
        Assert.True(query.Matches(Song("Disco 1999 13B", 123, EMinor)));
        Assert.False(query.Matches(Song("Disco 1999 13B", 130, EMinor)));
        Assert.False(query.Matches(Song("Funk 1999 13B", 123, EMinor)));
    }

    [Theory]
    [InlineData("\"80-90\"", "80-90")]
    [InlineData("\"8A\"", "8A")]
    [InlineData("\"124bpm\"", "124bpm")]
    [InlineData("\"Back to the\"", "Back to the")]
    [InlineData("\"80 - 90", "80 - 90")]
    public void Text_in_quotes_is_found_as_it_s_written(string text, string word)
    {
        var query = SongQuery.Parse(text);

        Assert.Equal(new[] { word }, query.Words);
        Assert.False(query.NeedsAnalysis);
    }

    [Fact]
    public void Quoted_text_goes_with_a_tempo_and_key_outside_the_quotes()
    {
        var query = SongQuery.Parse("disco \"80-90\" 120 - 126 ~8A \"\"");

        Assert.Equal(new[] { "disco", "80-90" }, query.Words);
        Assert.Equal((120.0, 126.0), (query.MinBpm, query.MaxBpm));
        Assert.True(query.Compatible);
        Assert.True(query.Matches(Song("Disco 80-90 mix", 123, EMinor)));
        Assert.False(query.Matches(Song("Disco 80 90 mix", 123, EMinor)));
    }

    [Fact]
    public void Words_alone_find_songs_analysed_or_not_by_title_artist_or_album()
    {
        var query = SongQuery.Parse("  daft  ");

        Assert.False(query.NeedsAnalysis);
        Assert.True(query.Matches(Song("One More Time", artist: "Daft Punk")));
        Assert.False(query.Matches(Song("Porcelain", artist: "Moby")));
        Assert.True(SongQuery.Parse("").IsEmpty);
    }
}
