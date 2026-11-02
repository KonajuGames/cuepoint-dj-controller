using Brutality.Audio;

namespace Brutality.Tests.Audio;

public class MasterLimiterTests
{
    const int RATE = AudioOutput.RATE;
    const int LOOKAHEAD = MasterLimiter.LOOKAHEAD;

    /// <summary>
    /// Runs <paramref name="input"/> through the limiter in buffers of 512 frames, as the audio thread would.
    /// </summary>
    static float[] Run(MasterLimiter limiter, float[] input)
    {
        var output = (float[])input.Clone();
        for (var at = 0; at < output.Length; at += 1024)
            limiter.Process(output.AsSpan(at, Math.Min(1024, output.Length - at)));
        return output;
    }

    static float[] Stereo(int frames, Func<int, (float Left, float Right)> frame)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            (samples[2 * i], samples[2 * i + 1]) = frame(i);
        return samples;
    }

    static float Sine(double hz, int frame) => (float)Math.Sin(2 * Math.PI * hz * frame / RATE);

    [Fact]
    public void Music_under_the_ceiling_comes_through_untouched_but_for_the_lookahead()
    {
        var input = Stereo(RATE, i => (0.85f * Sine(440, i), 0.7f * Sine(330, i)));
        var output = Run(new MasterLimiter(), input);

        for (var i = 0; i < 2 * LOOKAHEAD; i++)
            Assert.Equal(0, output[i]);
        for (var i = 2 * LOOKAHEAD; i < input.Length; i++)
            Assert.Equal(input[i - 2 * LOOKAHEAD], output[i]);
    }

    [Fact]
    public void Loud_decks_together_never_go_past_the_ceiling()
    {
        var random = new Random(1);
        var input = Stereo(RATE * 3, i =>
        {
            // Two loud decks a beat apart, and now and then a spike far over full scale.
            var spike = random.Next(3000) == 0 ? 3f : 0f;
            return (0.9f * Sine(55, i) + 0.9f * Sine(57, i) + spike, 0.9f * Sine(110, i) + 0.8f * Sine(61, i) - spike);
        });
        var limiter = new MasterLimiter();
        var output = Run(limiter, input);

        Assert.True(input.Max(MathF.Abs) > 2);
        Assert.All(output, sample => Assert.True(MathF.Abs(sample) <= MasterLimiter.CEILING + 1e-6f, $"{sample} is over the ceiling"));
        Assert.True(limiter.TakeReduction() < 0.5f);
        Assert.Equal(1, limiter.TakeReduction());
    }

    [Fact]
    public void Turns_the_level_down_smoothly_rather_than_in_steps()
    {
        var input = Stereo(RATE * 2, i => i < RATE / 2 ? (0.5f * Sine(100, i), 0.5f * Sine(100, i)) : (2f * Sine(100, i), 2f * Sine(100, i)));
        var output = Run(new MasterLimiter(), input);

        // The gain applied is the output over the input it came from, wherever that isn't near a zero crossing.
        double? last = null;
        for (var i = 2 * LOOKAHEAD; i < input.Length; i += 2)
        {
            var source = input[i - 2 * LOOKAHEAD];
            if (MathF.Abs(source) < 0.2f)
            {
                last = null;
                continue;
            }
            var gain = output[i] / source;
            if (last is { } previous)
                Assert.True(Math.Abs(gain - previous) < 0.01, $"the gain jumped from {previous} to {gain}");
            last = gain;
        }
    }

    [Fact]
    public void Lets_the_level_back_up_once_the_loud_part_is_over()
    {
        var input = Stereo(RATE * 2, i => i < RATE ? (2f * Sine(100, i), 2f * Sine(100, i)) : (0.5f * Sine(440, i), 0.5f * Sine(440, i)));
        var output = Run(new MasterLimiter(), input);

        // Most of the way back a quarter of a second in, and all the way by a second in.
        float PeakNear(double seconds)
        {
            var from = (int)(seconds * RATE) + LOOKAHEAD;
            var peak = 0f;
            for (var i = from; i < from + 200; i++)
                peak = MathF.Max(peak, MathF.Abs(output[2 * i]));
            return peak;
        }
        Assert.True(PeakNear(1.25) > 0.4f);
        Assert.Equal(0.5f, PeakNear(1.9), 0.001f);
    }

    [Fact]
    public void Turned_off_it_only_clamps_at_full_scale()
    {
        var limiter = new MasterLimiter { Enabled = false };
        var output = Run(limiter, Stereo(1000, _ => (0.95f, 1.5f)));

        Assert.Equal(0.95f, output[2 * LOOKAHEAD]);
        Assert.Equal(1f, output[2 * LOOKAHEAD + 1]);
        Assert.Equal(1, limiter.TakeReduction());
    }

    [Fact]
    public void Something_that_is_not_a_number_comes_out_as_silence()
    {
        var output = Run(new MasterLimiter(), Stereo(1000, i => i == 10 ? (float.NaN, 0.5f) : (0.5f, 0.5f)));

        Assert.All(output, sample => Assert.False(float.IsNaN(sample)));
    }

    [Fact]
    public void Holds_the_cue_back_exactly_as_long_as_the_mix()
    {
        // Each frame numbered, so where a number comes out says how far it was held back.
        var cue = Stereo(1000, i => (i, -i));
        var limiter = new MasterLimiter();
        for (var at = 0; at < cue.Length; at += 300)
            limiter.DelayCue(cue.AsSpan(at, Math.Min(300, cue.Length - at)));

        Assert.Equal(0, cue[2 * (LOOKAHEAD - 1)]);
        for (var frame = LOOKAHEAD; frame < 1000; frame++)
        {
            Assert.Equal(frame - LOOKAHEAD, cue[2 * frame]);
            Assert.Equal(-(frame - LOOKAHEAD), cue[2 * frame + 1]);
        }
    }
}
