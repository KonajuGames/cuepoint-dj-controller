using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

public class MixAheadTests
{
    [Fact]
    public void Only_what_the_device_falls_short_of_is_mixed_ahead()
    {
        // Windows' shared mode keeps 480 at 48 kHz, whatever's asked.
        Assert.Equal(1568, AudioOutput.MixAheadFor(2048, (480, 48000)));
        Assert.Equal(0, AudioOutput.MixAheadFor(256, (480, 48000)));
        // A device that took the size asked for.
        Assert.Equal(0, AudioOutput.MixAheadFor(1024, (1024, 48000)));
        // At the device's own rate, counted at the mix's: 441 at 44.1 kHz is 480 at 48.
        Assert.Equal(544, AudioOutput.MixAheadFor(1024, (441, 44100)));
    }

    [Fact]
    public void Nothing_is_mixed_ahead_without_a_size_or_a_known_buffer()
    {
        Assert.Equal(0, AudioOutput.MixAheadFor(null, (480, 48000)));
        Assert.Equal(0, AudioOutput.MixAheadFor(2048, null));
    }
}
