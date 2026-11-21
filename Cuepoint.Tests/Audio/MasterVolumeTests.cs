using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

[Collection(AudioEngineTests.NAME)]
public sealed unsafe class MasterVolumeTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0.5, 0.25)]
    [InlineData(0, 0)]
    public void The_knob_turns_down_as_a_volume_pot_does(float position, float gain)
    {
        Assert.Equal(gain, AudioEngine.MasterGain(position), 6);
    }

    [Fact]
    public void The_mix_eases_to_a_new_volume_then_holds_it()
    {
        using var engine = new AudioEngine();
        var buffer = new float[8];
        void Apply()
        {
            Array.Fill(buffer, 1f);
            fixed (float* samples = buffer)
                engine.ApplyMaster(samples, buffer.Length, 2);
        }

        // At full, untouched.
        Apply();
        Assert.All(buffer, sample => Assert.Equal(1, sample));

        // Turned halfway: down to a quarter over one buffer, both channels of a frame alike, then held there.
        engine.MasterVolume = 0.5f;
        Apply();
        Assert.Equal(buffer[0], buffer[1]);
        Assert.True(buffer[0] < 1 && buffer[0] > buffer[2]);
        Assert.Equal(0.25f, buffer[^1], 6);
        Apply();
        Assert.All(buffer, sample => Assert.Equal(0.25f, sample, 6));
    }

    [Fact]
    public void The_booth_hears_the_mix_at_its_own_volume_held_within_full_scale()
    {
        float[] mix = [0.5f, -0.5f, 1.8f, -1.8f];
        var booth = new float[4];

        AudioEngine.BoothMix(mix, booth, 1);
        Assert.Equal([0.5f, -0.5f, 1, -1], booth);

        // Halfway is a quarter, through the faders' taper.
        AudioEngine.BoothMix(mix, booth, 0.5f);
        Assert.Equal(0.125f, booth[0], 6);
        Assert.Equal(0.45f, booth[2], 6);
    }
}
