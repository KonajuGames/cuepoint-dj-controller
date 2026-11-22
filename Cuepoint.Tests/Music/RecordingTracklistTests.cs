using Cuepoint.Music;

namespace Cuepoint.Tests.Music;

public class RecordingTracklistTests
{
    static readonly DateTime Day = new(2026, 11, 21);

    static RecordedTrack T(double seconds, string artist, string title) => new(TimeSpan.FromSeconds(seconds), artist, title);

    [Fact]
    public void A_tracklist_written_across_two_files_reads_back_the_same()
    {
        using var folder = new TempFolder();
        var first = folder["Mix 2026-11-21 22-00-00.wav"];
        File.WriteAllText(first, "");
        File.WriteAllText(folder["Mix 2026-11-21 22-00-00 (2).wav"], "");
        var length = TimeSpan.FromMinutes(10);
        RecordedTrack[] tracks = [T(0, "Avicii", "Hey Brother"), T(250.5, "Adele", "Rolling in the Deep"), T(500, "", "Untitled"), T(700, "Alan Walker", "Faded")];

        Assert.Null(RecordingTracklist.Write(first, tracks, Day, length));

        // The second file starts with the song still playing as it begins, at its start.
        var second = File.ReadAllText(folder["Mix 2026-11-21 22-00-00 (2).cue"]);
        Assert.Contains("TITLE \"Untitled\"\r\n    INDEX 01 00:00:00", second);
        Assert.Contains("PERFORMER \"Alan Walker\"\r\n    INDEX 01 01:40:00", second);
        Assert.Contains("8:20  Untitled\r\n", File.ReadAllText(folder["Mix 2026-11-21 22-00-00.txt"]));

        var (read, date) = RecordingTracklist.Read(first, length);
        Assert.Equal(Day, date);
        Assert.Equal(tracks.Select(track => (track.Artist, track.Title)), read.Select(track => (track.Artist, track.Title)));
        Assert.Equal(tracks.Select(track => Math.Round(track.At.TotalSeconds, 1)), read.Select(track => Math.Round(track.At.TotalSeconds, 1)));
    }

    [Fact]
    public void Edited_and_written_again_the_files_follow_and_a_file_left_with_nothing_loses_its_cue_sheet()
    {
        using var folder = new TempFolder();
        var first = folder["Mix.wav"];
        File.WriteAllText(first, "");
        File.WriteAllText(folder["Mix (2).wav"], "");
        var length = TimeSpan.FromMinutes(10);
        RecordingTracklist.Write(first, [T(0, "A", "One"), T(700, "B", "Two")], Day, length);
        Assert.True(File.Exists(folder["Mix (2).cue"]));

        RecordingTracklist.Write(first, [T(30, "A", "One, renamed")], Day, length);

        var (read, _) = RecordingTracklist.Read(first, length);
        Assert.Equal([T(30, "A", "One, renamed")], read);
        // The song's still playing all through the second file, so it's there from its start.
        Assert.Contains("One, renamed", File.ReadAllText(folder["Mix (2).cue"]));
    }

    [Theory]
    [InlineData("4:05", 245)]
    [InlineData("64:05", 3845)]
    [InlineData("1:04:05", 3845)]
    [InlineData("0:07.5", 7.5)]
    [InlineData("90", 90)]
    public void Times_are_read_as_they_re_typed(string text, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RecordingTracklist.ParseClock(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("4:65")]
    [InlineData("1:61:00")]
    [InlineData("a:00")]
    [InlineData("-1:00")]
    [InlineData("1:2:3:4")]
    public void Anything_else_isn_t_a_time(string text)
    {
        Assert.Null(RecordingTracklist.ParseClock(text));
    }

    [Fact]
    public void Any_of_a_recording_s_files_leads_back_to_its_first()
    {
        Assert.Equal(Path.Combine(@"C:\R", "Mix 1.wav"), RecordingTracklist.FirstFile(Path.Combine(@"C:\R", "Mix 1 (2).cue")));
        Assert.Equal(Path.Combine(@"C:\R", "Mix 1.wav"), RecordingTracklist.FirstFile(Path.Combine(@"C:\R", "Mix 1.txt")));
    }
}
