using Brutality.Audio;

namespace Brutality.Tests.Audio;

public class DeckEffectsTests
{
    const int RATE = 48000;

    /// <summary>
    /// Runs <paramref name="mono"/> through the effect in blocks, as the audio thread would, and gives back what came
    /// out.
    /// </summary>
    static unsafe float[] Through(DeckEffects effects, float[] mono, int block = 512)
    {
        var output = (float[])mono.Clone();
        fixed (float* at = output)
        {
            for (var start = 0; start < output.Length; start += block)
                effects.Process(at + start, Math.Min(block, output.Length - start), 1, RATE);
        }
        return output;
    }

    static float[] Impulse(double seconds, int at = 1000)
    {
        var samples = new float[(int)(seconds * RATE)];
        samples[at] = 1;
        return samples;
    }

    static DeckEffects Echo() => new() { Kind = DeckEffect.Echo, On = true, Level = 0.6f, BeatSeconds = 0.5, Beats = 0.5 };

    [Fact]
    public void An_echo_repeats_the_sound_its_beats_later_each_quieter()
    {
        // Half a beat at 120 BPM: a quarter of a second.
        var output = Through(Echo(), Impulse(1));

        var gap = RATE / 4;
        Assert.Equal(1, output[1000], 3);
        Assert.Equal(0.6f, output[1000 + gap], 2);
        Assert.Equal(0.6f * 0.55f, output[1000 + 2 * gap], 2);
        Assert.Equal(0, output[1000 + gap / 2], 3);
    }

    [Fact]
    public void A_switched_off_echo_rings_on_but_takes_in_nothing_new()
    {
        var effects = Echo();
        var first = Through(effects, Impulse(0.5));
        Assert.Equal(0.6f, first[1000 + RATE / 4], 2);

        effects.On = false;
        var later = Through(effects, Impulse(0.6, at: 5000));

        // The first impulse's third repeat still comes; the new one isn't repeated.
        Assert.True(MathF.Abs(later[1000 + 3 * RATE / 4 - first.Length]) > 0.05f);
        Assert.Equal(0, later[5000 + RATE / 4], 3);
    }

    [Fact]
    public void An_effect_never_switched_on_leaves_the_sound_alone()
    {
        foreach (var kind in Enum.GetValues<DeckEffect>())
        {
            var sound = Enumerable.Range(0, RATE / 2).Select(i => MathF.Sin(i * 0.05f)).ToArray();

            Assert.Equal(sound, Through(new DeckEffects { Kind = kind, Level = 1 }, sound));
        }
    }

    [Fact]
    public void A_reverb_leaves_a_tail_that_dies_away_once_switched_off()
    {
        var effects = new DeckEffects { Kind = DeckEffect.Reverb, On = true, Level = 1 };
        var output = Through(effects, Impulse(1));
        Assert.True(output.Skip(RATE / 2).Take(RATE / 10).Max(MathF.Abs) > 1e-4f);

        effects.On = false;
        Through(effects, new float[8 * RATE]);
        var sound = Enumerable.Range(0, 4096).Select(i => MathF.Sin(i * 0.05f)).ToArray();

        Assert.Equal(sound, Through(effects, sound));
    }

    [Fact]
    public void A_flanger_combs_the_sound_and_switched_off_fades_back_to_it()
    {
        var effects = new DeckEffects { Kind = DeckEffect.Flanger, On = true, Level = 1, BeatSeconds = 0.5 };
        var sound = Enumerable.Range(0, RATE).Select(i => MathF.Sin(i * 0.3f) * 0.5f).ToArray();
        var flanged = Through(effects, sound);
        Assert.True(flanged.Zip(sound).Max(pair => MathF.Abs(pair.First - pair.Second)) > 0.1f);

        effects.On = false;
        Through(effects, sound.Take(4096).ToArray());

        Assert.Equal(sound, Through(effects, sound));
    }

    [Fact]
    public void Each_effect_keeps_its_own_beats_from_the_lengths_there_are()
    {
        var effects = new DeckEffects();
        Assert.Equal(0.5, effects.Beats);

        effects.Beats = 0.7;
        Assert.Equal(0.75, effects.Beats);
        effects.StepBeats(1);
        Assert.Equal(1, effects.Beats);
        effects.StepBeats(100);
        Assert.Equal(16, effects.Beats);

        effects.Kind = DeckEffect.Flanger;
        Assert.Equal(8, effects.Beats);
        effects.Kind = DeckEffect.Reverb;
        Assert.False(effects.HasBeats);
        effects.Kind = DeckEffect.Echo;
        Assert.Equal(16, effects.Beats);
    }

    [Fact]
    public void An_echo_longer_than_its_line_holds_stops_at_four_seconds()
    {
        // Sixteen beats at 60 BPM is sixteen seconds; it's held to four.
        var effects = new DeckEffects { On = true, Level = 1, BeatSeconds = 1, Beats = 16 };

        var output = Through(effects, Impulse(4.5));

        Assert.Equal(1, output[1000 + 4 * RATE], 1);
    }
}
