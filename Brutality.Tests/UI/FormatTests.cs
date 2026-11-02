using Brutality.UI;

namespace Brutality.Tests.UI;

public class FormatTests
{
    [Theory]
    [InlineData(0, 0, 42, 15, "42:15")]
    [InlineData(0, 0, 0, 5, "0:05")]
    [InlineData(0, 3, 42, 15, "3:42:15")]
    [InlineData(1, 0, 0, 0, "1 day 0:00:00")]
    [InlineData(2, 3, 42, 15, "2 days 3:42:15")]
    public void A_list_total_playing_time_reads_as_minutes_hours_or_days(int days, int hours, int minutes, int seconds, string expected)
    {
        Assert.Equal(expected, SongBrowser.FormatTotal(new TimeSpan(days, hours, minutes, seconds)));
    }

    [Theory]
    [InlineData(4, "4")]
    [InlineData(0.5, "1/2")]
    [InlineData(3.5, "3.5")]
    [InlineData(2.004, "2")]
    [InlineData(6, "6")]
    [InlineData(1.33, "1.3")]
    public void A_loop_set_by_hand_says_its_length_in_beats(double beats, string expected)
    {
        Assert.Equal(expected, DeckPanel.FormatLength(beats));
    }
}
