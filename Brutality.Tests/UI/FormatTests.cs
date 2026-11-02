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
}
