using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

public class WavFileTests
{
    [Fact]
    public void A_written_wav_decodes_to_the_same_samples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cuepoint-wav-{Guid.NewGuid():N}.wav");
        try
        {
            var samples = Enumerable.Range(0, 44100 * 2).Select(i => MathF.Sin(i * 0.01f) * 0.5f).ToArray();
            WavFile.Write(path, new PcmAudio(samples, 2, 44100), new SongTags("Song (DJ Edit)", "Artist", "Album", null, "House"));

            var (audio, _, _) = AudioDecoder.Decode(path);

            Assert.Equal(2, audio.Channels);
            Assert.Equal(44100, audio.SampleRate);
            Assert.Equal(samples.Length, audio.Samples.Length);
            for (var i = 0; i < samples.Length; i += 97)
                Assert.True(MathF.Abs(samples[i] - audio.Samples[i]) < 1e-5f, $"Sample {i}: {audio.Samples[i]} not {samples[i]}");
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_tags_written_are_read_back_with_its_length()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cuepoint-wav-{Guid.NewGuid():N}.wav");
        try
        {
            WavFile.Write(path, new PcmAudio(new float[44100 * 2], 2, 44100), new SongTags("Song (DJ Edit)", "Artist", "Album", null, "House"));

            var tags = WavFile.ReadTags(path);

            Assert.Equal("Song (DJ Edit)", tags.Title);
            Assert.Equal("Artist", tags.Artist);
            Assert.Equal("Album", tags.Album);
            Assert.Equal("House", tags.Genre);
            Assert.Equal(TimeSpan.FromSeconds(1), tags.Duration);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_existing_file_is_replaced_only_when_asked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cuepoint-wav-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllText(path, "old");

            WavFile.Write(path, new PcmAudio(new float[100], 2, 44100), new SongTags("New", null, null, null), replace: true);

            Assert.Equal("New", WavFile.ReadTags(path).Title);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_existing_file_is_never_replaced()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cuepoint-wav-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllText(path, "keep");

            Assert.Throws<IOException>(() => WavFile.Write(path, new PcmAudio(new float[100], 2, 44100), default));

            Assert.Equal("keep", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
