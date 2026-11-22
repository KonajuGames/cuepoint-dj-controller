using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

public class VocalCutTests
{
    const int RATE = 48000;

    /// <summary>
    /// A second of stereo: <paramref name="middle"/> in both sides alike, and <paramref name="side"/> in one side and
    /// upside down in the other.
    /// </summary>
    static float[] Stereo(Func<int, double> middle, Func<int, double> side)
    {
        var samples = new float[RATE * 2];
        for (var i = 0; i < RATE; i++)
        {
            samples[2 * i] = (float)(middle(i) + side(i));
            samples[2 * i + 1] = (float)(middle(i) - side(i));
        }
        return samples;
    }

    static double Tone(int i, double hz) => 0.5 * Math.Sin(2 * Math.PI * hz * i / RATE);

    static unsafe float[] Through(VocalCut cut, float[] samples)
    {
        var output = (float[])samples.Clone();
        fixed (float* at = output)
        {
            for (var start = 0; start < output.Length; start += 1024)
                cut.Process(at + start, Math.Min(1024, output.Length - start), 2, RATE);
        }
        return output;
    }

    /// <summary>
    /// How loud the last half of it is, as the root of the mean square.
    /// </summary>
    static double Level(float[] samples) => Math.Sqrt(samples.Skip(samples.Length / 2).Average(sample => (double)sample * sample));

    [Fact]
    public void A_voice_in_the_middle_goes_while_the_sides_stay()
    {
        var cut = new VocalCut { On = true };
        var voice = Stereo(i => Tone(i, 1000), _ => 0);
        var sides = Stereo(_ => 0, i => Tone(i, 1000));

        Assert.True(Level(Through(cut, voice)) < 0.02 * Level(voice));
        Assert.Equal(Level(sides), Level(Through(new VocalCut { On = true }, sides)), 2);
    }

    [Fact]
    public void The_bass_and_hi_hats_in_the_middle_stay()
    {
        var bass = Stereo(i => Tone(i, 60), _ => 0);
        var hats = Stereo(i => Tone(i, 14000), _ => 0);

        Assert.True(Level(Through(new VocalCut { On = true }, bass)) > 0.85 * Level(bass));
        Assert.True(Level(Through(new VocalCut { On = true }, hats)) > 0.85 * Level(hats));
    }

    [Fact]
    public void Off_it_leaves_the_sound_alone_and_mono_is_never_touched()
    {
        var voice = Stereo(i => Tone(i, 1000), _ => 0);
        Assert.Equal(voice, Through(new VocalCut(), voice));

        var mono = Enumerable.Range(0, 1000).Select(i => (float)Tone(i, 1000)).ToArray();
        var cut = new VocalCut { On = true };
        unsafe
        {
            fixed (float* at = mono)
                cut.Process(at, mono.Length, 1, RATE);
        }
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (float)Tone(i, 1000)), mono);
    }
}
