using Cuepoint.Audio.Analysis;
using Cuepoint.Music;
using static Cuepoint.Tests.Music.LibraryFileTests;

namespace Cuepoint.Tests.Music;

public class SetCheckTests
{
    static Song Analysed(string path)
    {
        var song = Song(path);
        song.Analysis = new TrackAnalysis(BeatGrid.Constant(125, 0, 300), -9, 0.9f, new MusicalKey(9, true));
        return song;
    }

    [Fact]
    public void It_tells_songs_gone_from_songs_out_of_reach_and_counts_those_not_analysed()
    {
        string[] paths = [@"C:\Music\a.mp3", @"C:\Music\b.mp3", @"C:\Music\gone.mp3", @"E:\Music\usb.mp3", @"E:\Music\usb2.mp3"];
        var found = new[] { Analysed(paths[0]), Song(paths[1]) };

        var check = SetCheck.Of(paths, found, path => path.StartsWith(@"C:\"));

        Assert.Equal(new SetCheck(5, 1, 2, 1), check);
        Assert.False(check.AllGood);
        Assert.Equal("Of 5 songs, 1 song can't be found, 2 songs are on a drive that isn't plugged in and 1 song isn't analysed yet.", check.Summary);
    }

    [Fact]
    public void A_list_with_everything_there_and_analysed_is_all_good()
    {
        var check = SetCheck.Of([@"C:\Music\a.mp3"], [Analysed(@"C:\Music\a.mp3")], _ => true);
        Assert.True(check.AllGood);
        Assert.Equal("The song is here and analysed.", check.Summary);
        Assert.Equal("Of 3 songs, 3 songs aren't analysed yet.", new SetCheck(3, 0, 0, 3).Summary);
    }
}
