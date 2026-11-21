using Cuepoint.Audio.Analysis;
using Cuepoint.Music;
using static Cuepoint.Tests.Music.LibraryFileTests;

namespace Cuepoint.Tests.Music;

public class MixOrderTests
{
    static MusicalKey Camelot(int number, bool minor = true) =>
        // 8A is A minor and each step round the wheel is a fifth up; 8B is C major.
        new(((minor ? 9 : 0) + (number - 8) * 7 % 12 + 12) % 12, minor);

    static Song At(string name, MusicalKey key, double bpm, int? energy = null)
    {
        var song = Song($@"C:\Music\{name}.mp3", title: name);
        song.Analysis = new TrackAnalysis(BeatGrid.Constant(bpm, 0, 300), -9, 0.9f, key, energy);
        return song;
    }

    static IEnumerable<string?> Titles(IEnumerable<Song> songs) => songs.Select(song => song.Title);

    [Fact]
    public void The_wheel_helper_names_keys_as_the_Camelot_wheel_does()
    {
        Assert.Equal("8A", Camelot(8).Camelot);
        Assert.Equal("9A", Camelot(9).Camelot);
        Assert.Equal("3A", Camelot(3).Camelot);
        Assert.Equal("8B", Camelot(8, false).Camelot);
    }

    [Fact]
    public void It_starts_from_the_calmest_and_walks_round_the_wheel_building_energy()
    {
        var songs = new[]
        {
            At("10A peak", Camelot(10), 124, 8),
            At("8A warm up", Camelot(8), 122, 5),
            At("9A build", Camelot(9), 123, 6),
            At("11A top", Camelot(11), 125, 9),
            At("9A lift", Camelot(9), 124, 7),
        };

        Assert.Equal(["8A warm up", "9A build", "9A lift", "10A peak", "11A top"], Titles(MixOrder.Arrange(songs)));
    }

    [Fact]
    public void After_a_song_playing_what_mixes_with_it_comes_first()
    {
        var playing = At("playing", Camelot(3), 124, 7);
        var songs = new[] { At("8A", Camelot(8), 124, 7), At("3A", Camelot(3), 124, 8), At("4A", Camelot(4), 124, 8) };

        Assert.Equal(["3A", "4A", "8A"], Titles(MixOrder.Arrange(songs, playing)));
    }

    [Fact]
    public void With_nothing_in_key_the_nearest_tempo_follows_and_songs_not_analysed_go_last_as_they_were()
    {
        var unanalysed = Song(@"C:\Music\b.mp3", title: "not analysed b");
        var alsoUnanalysed = Song(@"C:\Music\a.mp3", title: "not analysed a");
        var songs = new[] { unanalysed, At("far", Camelot(2), 150, 5), At("start", Camelot(8), 120, 4), alsoUnanalysed, At("near", Camelot(2), 121, 5) };

        Assert.Equal(["start", "near", "far", "not analysed b", "not analysed a"], Titles(MixOrder.Arrange(songs)));
    }

    [Fact]
    public void Nothing_and_one_song_stay_as_they_are()
    {
        Assert.Empty(MixOrder.Arrange([]));
        var one = At("one", Camelot(8), 124);
        Assert.Equal([one], MixOrder.Arrange([one]));
    }
}
