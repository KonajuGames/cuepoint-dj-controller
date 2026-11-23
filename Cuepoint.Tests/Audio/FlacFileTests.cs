using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

public class FlacFileTests
{
    static string TempPath() => Path.Combine(Path.GetTempPath(), $"cuepoint-flac-{Guid.NewGuid():N}.flac");

    // Something like music: a few notes, some noise, and silence, in each channel a little differently.
    static float[] MakeSamples(int frames, int channels, int seed = 5)
    {
        var random = new Random(seed);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                var t = (double)i / 44100;
                var tone = 0.3 * Math.Sin(2 * Math.PI * (220 + 30 * c) * t) + 0.2 * Math.Sin(2 * Math.PI * 55 * t) * Math.Exp(-(t % 0.5) * 8);
                var noise = 0.002 * (random.NextDouble() * 2 - 1);
                samples[i * channels + c] = i % 30000 < 2000 ? 0 : (float)(tone + noise);
            }
        }
        return samples;
    }

    static void AssertDecodesTo(float[] written, PcmAudio decoded)
    {
        Assert.Equal(written.Length, decoded.Samples.Length);
        for (var i = 0; i < written.Length; i++)
        {
            var expected = Math.Clamp(MathF.Round(written[i] * 32768f), short.MinValue, short.MaxValue) / 32768f;
            Assert.True(decoded.Samples[i] == expected, $"Sample {i}: {decoded.Samples[i]} not {expected}");
        }
    }

    [Theory]
    [InlineData(2, 44100 * 3 + 123)]
    [InlineData(1, 44100 + 7)]
    [InlineData(2, 100)]
    public void A_written_flac_decodes_to_the_same_16_bit_samples(int channels, int frames)
    {
        var path = TempPath();
        try
        {
            var samples = MakeSamples(frames, channels);
            FlacFile.Write(path, new PcmAudio(samples, channels, 44100), new SongTags("Song", null, null, null));

            var (audio, _, _) = AudioDecoder.Decode(path);

            Assert.Equal(channels, audio.Channels);
            Assert.Equal(44100, audio.SampleRate);
            AssertDecodesTo(samples, audio);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loud_and_clipped_sounds_and_silence_come_back_too()
    {
        var path = TempPath();
        try
        {
            var samples = new float[44100 * 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = i < 20000 ? 0 : i < 40000 ? (i % 2 == 0 ? 1.5f : -1.5f) : (i % 7 == 0 ? 0.99f : -0.99f) * (i % 2 == 0 ? 1 : -1);
            FlacFile.Write(path, new PcmAudio(samples, 2, 48000), default);

            var (audio, _, _) = AudioDecoder.Decode(path);

            Assert.Equal(48000, audio.SampleRate);
            AssertDecodesTo(samples, audio);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void It_takes_much_less_room_than_a_wav()
    {
        var path = TempPath();
        try
        {
            var samples = MakeSamples(44100 * 10, 2);
            FlacFile.Write(path, new PcmAudio(samples, 2, 44100), default);

            var wav16 = samples.Length * 2;
            Assert.True(new FileInfo(path).Length < wav16 * 0.6, $"{new FileInfo(path).Length} bytes against {wav16} as a 16-bit WAV");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Its_tags_are_read_back_with_its_length()
    {
        var path = TempPath();
        try
        {
            FlacFile.Write(path, new PcmAudio(MakeSamples(44100 * 2, 2), 2, 44100), new SongTags("Song (Cuepoint DJ Edit)", "Artist", "Album", null, "House"));

            var tags = WavFile.ReadAnyTags(path);

            Assert.Equal("Song (Cuepoint DJ Edit)", tags.Title);
            Assert.Equal("Artist", tags.Artist);
            Assert.Equal("Album", tags.Album);
            Assert.Equal("House", tags.Genre);
            Assert.NotNull(tags.Duration);
            Assert.Equal(2, tags.Duration!.Value.TotalSeconds, 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_existing_file_is_replaced_only_when_asked()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "keep");
            var audio = new PcmAudio(new float[200], 2, 44100);

            Assert.Throws<IOException>(() => FlacFile.Write(path, audio, default));
            Assert.Equal("keep", File.ReadAllText(path));

            FlacFile.Write(path, audio, new SongTags("New", null, null, null), replace: true);

            Assert.Equal(200, AudioDecoder.Decode(path).Audio.Samples.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
