using Brutality.Audio;

namespace Brutality.Tests.Audio;

public unsafe class MixRecorderTests
{
    const int RATE = AudioOutput.RATE;

    /// <summary>
    /// Feeds <paramref name="frames"/> of stereo to the recorder in buffers of 1024 frames, as the audio thread would.
    /// </summary>
    static void Feed(MixRecorder recorder, int frames, Func<int, (float Left, float Right)> frame)
    {
        var buffer = new float[2048];
        for (var done = 0; done < frames; )
        {
            var count = Math.Min(1024, frames - done);
            for (var i = 0; i < count; i++)
                (buffer[2 * i], buffer[2 * i + 1]) = frame(done + i);
            fixed (float* samples = buffer)
                recorder.Write(samples, 2 * count);
            done += count;
        }
    }

    /// <summary>
    /// A WAV file's format and its samples, as fractions of full scale, read back with nothing but the bytes.
    /// </summary>
    static (int Channels, int Rate, int Bits, float[] Samples) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));

        var channels = BitConverter.ToInt16(bytes, 22);
        var rate = BitConverter.ToInt32(bytes, 24);
        var bits = BitConverter.ToInt16(bytes, 34);
        var dataBytes = BitConverter.ToInt32(bytes, 40);
        Assert.Equal(bytes.Length - 44, dataBytes);

        var samples = new float[dataBytes / 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var at = 44 + 3 * i;
            var value = bytes[at] | bytes[at + 1] << 8 | (sbyte)bytes[at + 2] << 16;
            samples[i] = value / 8388607f;
        }
        return (channels, rate, bits, samples);
    }

    [Fact]
    public void Writes_the_mix_as_24_bit_stereo_at_the_mixer_rate()
    {
        using var folder = new TempFolder();
        var path = folder["Recordings", "mix.wav"];

        using (var recorder = new MixRecorder(path))
        {
            Feed(recorder, RATE, i => (0.5f * MathF.Sin(2 * MathF.PI * 440 * i / RATE), -0.25f));
            recorder.Stop();
            Assert.Null(recorder.Error);
            Assert.Equal(1.0, recorder.Duration.TotalSeconds, 3);
            Assert.Equal(new[] { path }, recorder.Paths);
        }

        var (channels, rate, bits, samples) = Read(path);
        Assert.Equal(2, channels);
        Assert.Equal(RATE, rate);
        Assert.Equal(24, bits);
        Assert.Equal(2 * RATE, samples.Length);
        for (var i = 0; i < RATE; i += 997)
        {
            Assert.Equal(0.5f * MathF.Sin(2 * MathF.PI * 440 * i / RATE), samples[2 * i], 0.0001f);
            Assert.Equal(-0.25f, samples[2 * i + 1], 0.0001f);
        }
    }

    [Fact]
    public void Clips_at_full_scale_as_the_speakers_would()
    {
        using var folder = new TempFolder();
        var path = folder["mix.wav"];
        using (var recorder = new MixRecorder(path))
        {
            Feed(recorder, 10, _ => (1.7f, -3f));
            recorder.Stop();
        }

        var samples = Read(path).Samples;
        Assert.Equal(1f, samples[0]);
        Assert.Equal(-1f, samples[1]);
    }

    [Fact]
    public void Carries_on_into_numbered_files_past_the_size_limit_losing_nothing()
    {
        using var folder = new TempFolder();
        var path = folder["mix.wav"];

        // A file holds a second at most here, rather than four hours.
        using (var recorder = new MixRecorder(path, maxDataBytes: RATE * 6))
        {
            Feed(recorder, RATE * 5 / 2, i => (i / (float)(RATE * 3), 0));
            recorder.Stop();
            Assert.Equal(new[] { path, folder["mix (2).wav"], folder["mix (3).wav"] }, recorder.Paths);
        }

        var all = new[] { "mix.wav", "mix (2).wav", "mix (3).wav" }.SelectMany(name => Read(folder[name]).Samples).ToArray();
        Assert.Equal(RATE, Read(path).Samples.Length / 2);
        Assert.Equal(RATE * 5 / 2, all.Length / 2);
        // The ramp runs on unbroken from one file into the next.
        for (var i = 0; i < all.Length / 2; i += 1009)
            Assert.Equal(i / (float)(RATE * 3), all[2 * i], 0.0001f);
    }

    [Fact]
    public void Stopping_twice_is_harmless_and_nothing_is_taken_after_stopping()
    {
        using var folder = new TempFolder();
        var path = folder["mix.wav"];
        var recorder = new MixRecorder(path);
        Feed(recorder, 100, _ => (0.1f, 0.1f));
        recorder.Stop();
        Feed(recorder, 100, _ => (0.1f, 0.1f));
        recorder.Stop();
        recorder.Dispose();

        Assert.Equal(200, Read(path).Samples.Length);
    }

    [Fact]
    public void A_folder_that_cannot_be_made_is_said_up_front()
    {
        using var folder = new TempFolder();
        // A file where the folder would need to be.
        var blocker = folder.File("taken");

        Assert.ThrowsAny<IOException>(() => new MixRecorder(Path.Combine(blocker, "mix.wav")));
    }
}
