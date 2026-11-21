using Cuepoint.Music;
using static Cuepoint.Tests.Music.LibraryFileTests;

namespace Cuepoint.Tests.Music;

/// <summary>
/// Sets kept between runs, and searching by when songs were played. With the other tests that set
/// <see cref="SongQuery.LastPlayed"/>, as it's shared.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public class PlayLogTests
{
    static readonly DateTime Gig = new(2026, 11, 14, 21, 0, 0);

    [Fact]
    public void A_set_is_kept_as_it_grows_and_read_back_next_time_newest_first()
    {
        using var folder = new TempFolder();
        var log = new PlayLog(folder["history"]);
        Assert.Null(log.Keep(Gig, [(Gig, @"C:\Music\a.mp3")]));
        Assert.Null(log.Keep(Gig, [(Gig, @"C:\Music\a.mp3"), (Gig.AddMinutes(4), @"C:\Music\b.mp3")]));
        var later = Gig.AddDays(7);
        Assert.Null(log.Keep(later, [(later, @"C:\Music\a.mp3")]));

        var again = new PlayLog(folder["history"]);

        Assert.Equal([later, Gig], again.Past.Select(set => set.Started));
        Assert.Equal([@"C:\Music\a.mp3", @"C:\Music\b.mp3"], again.Past[1].Songs.Select(song => song.Path));
        Assert.Equal(later, again.LastPlayed(@"C:\Music\a.mp3"));
        Assert.Equal(Gig.AddMinutes(4), again.LastPlayed(@"c:\music\B.MP3"));
        Assert.Null(again.LastPlayed(@"C:\Music\never.mp3"));
        Assert.StartsWith("Sat 14 Nov 2026, 21:00 (2 songs)", again.Past[1].Name);
    }

    [Fact]
    public void A_set_with_nothing_left_in_it_isn_t_kept_and_a_broken_file_is_passed_over()
    {
        using var folder = new TempFolder();
        var log = new PlayLog(folder["history"]);
        log.Keep(Gig, [(Gig, @"C:\Music\a.mp3")]);
        log.Keep(Gig, []);
        File.WriteAllText(folder["history", "broken.json"], "{ not json");

        Assert.Empty(new PlayLog(folder["history"]).Past);
    }

    [Fact]
    public void Songs_can_be_found_by_whether_they_were_played_lately()
    {
        var recent = Song(@"C:\Music\recent.mp3");
        var old = Song(@"C:\Music\old.mp3");
        var never = Song(@"C:\Music\never.mp3");
        var now = new DateTime(2026, 11, 21, 12, 0, 0);
        try
        {
            SongQuery.Now = () => now;
            SongQuery.LastPlayed = path => path.Contains("recent") ? now.AddDays(-3) : path.Contains("old") ? now.AddDays(-60) : null;

            var played = SongQuery.Parse("played:30d");
            Assert.False(played.IsEmpty);
            Assert.True(played.Matches(recent));
            Assert.False(played.Matches(old));
            Assert.False(played.Matches(never));

            var notPlayed = SongQuery.Parse("notplayed:2w");
            Assert.False(notPlayed.Matches(recent));
            Assert.True(notPlayed.Matches(old));
            Assert.True(notPlayed.Matches(never));
        }
        finally
        {
            (SongQuery.Now, SongQuery.LastPlayed) = (() => DateTime.Now, _ => null);
        }
    }
}
