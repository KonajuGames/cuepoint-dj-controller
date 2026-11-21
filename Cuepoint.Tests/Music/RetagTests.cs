using Cuepoint.Audio;
using Cuepoint.Music;
using static Cuepoint.Tests.Music.LibraryFileTests;

namespace Cuepoint.Tests.Music;

public class RetagTests
{
    [Fact]
    public void A_song_retagged_keeps_its_analysis_for_the_file_as_it_now_is()
    {
        using var folder = new TempFolder();
        var song = Song(folder["Music", "a.mp3"]);
        var file = LibraryFile.Load(folder["library.json"]);
        file.Set(song, Analysed());

        (song.Title, song.Artist) = ("New title", "New artist");
        var modified = song.Modified.AddMinutes(5);
        file.Retag(song, 1234, modified);
        (song.Size, song.Modified) = (1234, modified);
        file.Save();

        var again = LibraryFile.Load(folder["library.json"]);
        Assert.True(again.TryGet(song, out var analysis, out _));
        Assert.Equal(Analysed(), analysis);
        Assert.True(again.TryGetNames(song, out var title, out var artist, out _));
        Assert.Equal(("New title", "New artist"), (title, artist));
    }

    /// <summary>
    /// Writes tags into copies of real songs, in the folder CUEPOINT_TAG_SAMPLES names, as there's no making an MP3
    /// or M4A here; without it, there's nothing to try.
    /// </summary>
    [Fact]
    public void Tags_written_come_back_when_they_re_read()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CUEPOINT_TAG_SAMPLES") is not { Length: > 0 } samples)
            return;
        using var folder = new TempFolder();
        foreach (var sample in Directory.GetFiles(samples))
        {
            var copy = folder[Path.GetFileName(sample)];
            File.Copy(sample, copy);

            Assert.Null(WindowsTags.Write(copy, "Written title", "Written artist", "Written album"));

            var tags = WindowsTags.Read(copy);
            Assert.Equal(("Written title", "Written artist", "Written album"), (tags.Title, tags.Artist, tags.Album));
        }
    }
}
