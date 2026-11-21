using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

public class MicMixTests
{
    const int RATE = 48000;

    static float[] Steady(double seconds, float value) => Enumerable.Repeat(value, (int)(seconds * RATE) * 2).ToArray();

    [Fact]
    public void The_voice_goes_in_at_its_level_and_the_music_stays_put_without_talkover()
    {
        var mix = Steady(0.1, 0.5f);
        new MicMix().Mix(mix, Steady(0.1, 0.2f), 2, RATE, 0.5f, talkover: false);

        Assert.All(mix, sample => Assert.Equal(0.6f, sample, 4));
    }

    [Fact]
    public void Talkover_turns_the_music_down_while_spoken_into_and_back_up_after()
    {
        var mixer = new MicMix();
        var music = Steady(0.5, 0.5f);
        mixer.Mix(music, Steady(0.5, 0.1f), 2, RATE, 1, talkover: true);

        // Down to a quarter within a few hundredths of a second, under the voice.
        Assert.Equal(0.5f * MicMix.TALKOVER_GAIN + 0.1f, music[^1], 3);
        Assert.Equal(MicMix.TALKOVER_GAIN, mixer.Duck, 3);

        // Held a moment once the voice stops, so words don't pump it, then back up over most of a second.
        var after = Steady(2, 0.5f);
        mixer.Mix(after, Steady(2, 0), 2, RATE, 1, talkover: true);
        Assert.Equal(0.5f * MicMix.TALKOVER_GAIN, after[(int)(0.2 * RATE) * 2], 3);
        Assert.Equal(0.5f, after[^1], 3);
        Assert.Equal(1, mixer.Duck);
    }

    [Fact]
    public void Quiet_breathing_doesn_t_set_it_off()
    {
        var music = Steady(0.5, 0.5f);
        var mixer = new MicMix();
        mixer.Mix(music, Steady(0.5, 0.005f), 2, RATE, 1, talkover: true);

        Assert.Equal(1, mixer.Duck);
    }
}
