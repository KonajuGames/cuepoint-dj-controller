using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// A recording a crash left unfinished, mended from its file's length.
/// </summary>
public class MendRecordingTests
{
    const int FRAME = 6;

    /// <summary>
    /// A recording as one's header looks: 24-bit stereo at 48 kHz, its data size as given, then this many bytes.
    /// </summary>
    static void Recording(string path, uint saysData, int hasBytes, ushort channels = 2)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + saysData);
        writer.Write("WAVEfmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write(channels);
        writer.Write(48000u);
        writer.Write(48000u * FRAME);
        writer.Write((ushort)FRAME);
        writer.Write((ushort)24);
        writer.Write("data"u8);
        writer.Write(saysData);
        writer.Write(new byte[hasBytes]);
    }

    static (uint Riff, uint Data) Sizes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return (BitConverter.ToUInt32(bytes, 4), BitConverter.ToUInt32(bytes, 40));
    }

    [Fact]
    public void One_left_saying_nothing_s_there_gets_the_sizes_it_has_to_its_last_whole_frame()
    {
        using var folder = new TempFolder();
        var path = folder["Mix 2026-11-22 21-00-00.wav"];
        // A thousand frames, and half of one cut off as the app went.
        Recording(path, 0, 1000 * FRAME + 3);

        Assert.True(MixRecorder.Mend(path));

        Assert.Equal((36u + 1000 * FRAME, 1000u * FRAME), Sizes(path));
        Assert.False(MixRecorder.Mend(path));
    }

    [Fact]
    public void One_finished_and_one_that_isn_t_ours_are_left_alone()
    {
        using var folder = new TempFolder();
        Recording(folder["finished.wav"], 600, 600);
        Recording(folder["mono.wav"], 0, 600, channels: 1);
        File.WriteAllBytes(folder["short.wav"], [1, 2, 3]);
        var before = File.ReadAllBytes(folder["mono.wav"]);

        Assert.Empty(MixRecorder.MendAll(folder.Path));

        Assert.Equal(before, File.ReadAllBytes(folder["mono.wav"]));
        Assert.Equal((636u, 600u), Sizes(folder["finished.wav"]));
    }

    [Fact]
    public void Every_unfinished_one_in_the_folder_is_mended_and_said()
    {
        using var folder = new TempFolder();
        Recording(folder["Mix 2026-11-22 21-00-00.wav"], 0, 60);
        Recording(folder["Mix 2026-11-22 21-00-00 (2).wav"], 0, 120);

        Assert.Equal(2, MixRecorder.MendAll(folder.Path).Count);
        Assert.Equal(120u, Sizes(folder["Mix 2026-11-22 21-00-00 (2).wav"]).Data);
    }

    [Fact]
    public void When_a_recording_started_comes_from_its_name_or_its_first_file_s()
    {
        Assert.Equal(new DateTime(2026, 11, 22, 21, 0, 5), MixRecorder.StartedFrom(@"C:\R\Mix 2026-11-22 21-00-05.wav"));
        Assert.Equal(new DateTime(2026, 11, 22, 21, 0, 5), MixRecorder.StartedFrom(@"C:\R\Mix 2026-11-22 21-00-05 (2).wav"));
        Assert.Null(MixRecorder.StartedFrom(@"C:\R\Something else.wav"));
    }
}
