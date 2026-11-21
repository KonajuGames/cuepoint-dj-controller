using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

public class PhaseMeterTests
{
    [Fact]
    public void It_eases_towards_each_reading()
    {
        var shown = PhaseMeter.Ease((0.1, 0.05), (0.2, 0.1), 0.5);
        Assert.Equal(0.15, shown!.Value.Beats, 9);
        Assert.Equal(0.075, shown.Value.Seconds, 9);
    }

    [Fact]
    public void It_goes_straight_across_the_ends_and_after_a_jump()
    {
        // Just under half a beat ahead to just under half behind is a hair further on, not a sweep back across.
        Assert.Equal((-0.48, -0.24), PhaseMeter.Ease((0.48, 0.24), (-0.48, -0.24), 0.5));
        Assert.Equal((0.4, 0.2), PhaseMeter.Ease(null, (0.4, 0.2), 0.5));
    }

    [Fact]
    public void It_shows_nothing_once_a_song_has_no_grid() => Assert.Null(PhaseMeter.Ease((0.1, 0.05), null, 0.5));
}
