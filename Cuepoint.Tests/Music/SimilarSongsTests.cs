using Cuepoint.Music;
using static Cuepoint.Tests.Music.LibraryFileTests;

namespace Cuepoint.Tests.Music;

public class SimilarSongsTests
{
    static Song At(string path, string artist, string title, double? seconds)
    {
        var song = Song(path, title: title, artist: artist);
        song.Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null;
        return song;
    }

    [Theory]
    [InlineData("Wake Me Up", "wakemeup")]
    [InlineData("01 - Wake Me Up (Remastered 2013)", "wakemeup")]
    [InlineData("Wake Me Up [Radio Edit] feat. Aloe Blacc", "wakemeup")]
    [InlineData("Déjà Vu", "déjàvu")]
    public void Names_are_compared_without_what_varies_between_copies(string name, string normal)
    {
        Assert.Equal(normal, LibraryRepair.Normal(name));
    }

    [Fact]
    public void The_same_recording_tagged_or_encoded_differently_is_found()
    {
        var songs = new[]
        {
            At(@"C:\A\wake.mp3", "Avicii", "Wake Me Up", 249),
            At(@"C:\B\wake.m4a", "AVICII", "Wake Me Up (Remastered)", 250.5),
            // The extended mix is another recording, and Hey Brother another song.
            At(@"C:\C\wake extended.mp3", "Avicii", "Wake Me Up", 330),
            At(@"C:\A\hey.mp3", "Avicii", "Hey Brother", 255),
        };

        var group = Assert.Single(LibraryRepair.FindSimilar(songs));
        Assert.Equal([@"C:\A\wake.mp3", @"C:\B\wake.m4a"], group.Select(song => song.Path));
    }

    [Fact]
    public void A_song_whose_length_isn_t_known_goes_by_its_name()
    {
        var songs = new[] { At(@"C:\A\faded.mp3", "Alan Walker", "Faded", 212), At(@"C:\B\faded.mp3", "Alan Walker", "Faded", null) };
        Assert.Equal(2, Assert.Single(LibraryRepair.FindSimilar(songs)).Count);
        Assert.Empty(LibraryRepair.FindSimilar([songs[0]]));
    }
}
