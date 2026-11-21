using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

public class BeatCounterTests
{
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void The_next_cue_is_the_nearest_ahead_of_either_kind()
    {
        TimeSpan?[] hotcues = [S(30), null, S(12)];
        TimeSpan[] memory = [S(5), S(20)];
        Assert.Equal((12.0, 2), ScrollingWaveform.NextCue(hotcues, memory, 10));
        Assert.Equal((20.0, -1), ScrollingWaveform.NextCue(hotcues, memory, 12));
        Assert.Null(ScrollingWaveform.NextCue(hotcues, memory, 31));
    }

    [Fact]
    public void A_hotcue_wins_over_a_memory_cue_in_the_same_place()
    {
        Assert.Equal((12.0, 0), ScrollingWaveform.NextCue([S(12)], [S(12)], 10));
        // Or a millisecond off it, as one set by hand on the beat would be.
        Assert.Equal(0, ScrollingWaveform.NextCue([S(12.001)], [S(12)], 10)?.Hotcue);
    }
}
