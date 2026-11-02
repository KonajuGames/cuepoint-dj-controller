using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.Tests.Audio;

public class GainAndHeadphoneTests
{
    static TrackAnalysis Analysis(double loudness, float peak) =>
        new(BeatGrid.Constant(120, 0, 180), loudness, peak, new MusicalKey(0, false));

    [Theory]
    // Louder than the target comes down to it.
    [InlineData(-8, 0.99f, -6)]
    // Quieter goes up to it...
    [InlineData(-20, 0.25f, 6)]
    // ...but only as far as its peak allows.
    [InlineData(-20, 0.9f, 0.915)]
    // At most 12 dB up, and 24 down.
    [InlineData(-40, 0f, 12)]
    [InlineData(15, 1f, -24)]
    public void Automatic_gain_brings_songs_to_the_target_without_clipping(double loudness, float peak, double expectedDb)
    {
        Assert.Equal(expectedDb, Analysis(loudness, peak).AutoGainDb, 2);
    }

    [Fact]
    public void Automatic_gain_as_a_factor_matches_its_decibels()
    {
        Assert.Equal(0.5f, Analysis(-8, 1).AutoGain, 0.01f);
    }

    [Fact]
    public void The_headphones_hear_the_cue_the_mix_or_a_blend()
    {
        float[] cue = [0.4f, -0.4f];
        float[] master = [0.8f, 0.2f];
        var output = new float[2];

        HeadphoneMix.Mix(cue, master, output, cueMix: 0, volume: 1);
        Assert.Equal(cue, output);

        HeadphoneMix.Mix(cue, master, output, cueMix: 1, volume: 1);
        Assert.Equal(master, output);

        HeadphoneMix.Mix(cue, master, output, cueMix: 0.5f, volume: 1);
        Assert.Equal(0.6f, output[0], 0.0001f);
        Assert.Equal(-0.1f, output[1], 0.0001f);
    }

    [Fact]
    public void Headphone_volume_follows_the_fader_taper()
    {
        float[] cue = [1f, 1f];
        var output = new float[2];

        HeadphoneMix.Mix(cue, [], output, cueMix: 0, volume: 0.5f);

        Assert.Equal(Deck.VolumeTaper(0.5f), output[0], 0.0001f);
    }

    [Fact]
    public void A_short_cue_counts_as_silence_after_its_end()
    {
        var output = new float[4];

        HeadphoneMix.Mix([1f, 1f], [0f, 0f, 0f, 0f], output, cueMix: 0, volume: 1);

        Assert.Equal(new[] { 1f, 1f, 0f, 0f }, output);
    }
}
