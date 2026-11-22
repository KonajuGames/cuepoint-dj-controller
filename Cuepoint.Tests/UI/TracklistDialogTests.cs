using Cuepoint.Music;
using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

/// <summary>
/// Editing a recording's tracklist in its dialog, built with a system font as the app builds it.
/// </summary>
public class TracklistDialogTests
{
    static IEnumerable<Element> Within(Element element) =>
        element is Panel panel ? panel.Children.SelectMany(Within).Prepend(element) : [element];

    static TracklistDialog Make()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        var root = new Root();
        return root.Add(new TracklistDialog(root, font, font, font));
    }

    static void Click(TracklistDialog dialog, string text) => Within(dialog).OfType<Button>().First(button => button.Text == text).RaiseClicked();

    static readonly RecordedTrack[] Tracks =
    [
        new(TimeSpan.FromSeconds(0), "Avicii", "Hey Brother"),
        new(TimeSpan.FromSeconds(245.37), "Adele", "Rolling in the Deep"),
        new(TimeSpan.FromSeconds(500), "Alan Walker", "Faded"),
    ];

    [Fact]
    public void Songs_changed_taken_out_and_put_in_are_saved_in_order_with_untouched_times_kept_exactly()
    {
        var dialog = Make();
        IReadOnlyList<RecordedTrack>? saved = null;
        dialog.Show("Mix", Tracks, tracks =>
        {
            saved = tracks;
            return null;
        });
        Assert.Equal("4:05", dialog.Rows[1].Time.Text);

        dialog.Rows[2].Title.Text = "Faded (Remix)";
        dialog.RemoveRow(0);
        Click(dialog, "Add a song");
        (dialog.Rows[2].Time.Text, dialog.Rows[2].Artist.Text, dialog.Rows[2].Title.Text) = ("1:30", "Sia", "Titanium");
        Click(dialog, "Save");

        Assert.False(dialog.Visible);
        Assert.Equal(
            [
                new RecordedTrack(TimeSpan.FromSeconds(90), "Sia", "Titanium"),
                new RecordedTrack(TimeSpan.FromSeconds(245.37), "Adele", "Rolling in the Deep"),
                new RecordedTrack(TimeSpan.FromSeconds(500), "Alan Walker", "Faded (Remix)"),
            ],
            saved
        );
    }

    [Fact]
    public void A_time_that_isn_t_one_or_a_missing_title_stops_it_saving()
    {
        var dialog = Make();
        var saves = 0;
        dialog.Show("Mix", Tracks, _ =>
        {
            saves++;
            return null;
        });

        dialog.Rows[1].Time.Text = "4:65";
        Click(dialog, "Save");
        Assert.True(dialog.Visible);
        Assert.Contains(Within(dialog).OfType<Label>(), label => label.Text.Contains("isn't a time"));

        dialog.Rows[1].Time.Text = "4:06";
        dialog.Rows[2].Title.Text = " ";
        Click(dialog, "Save");
        Assert.Contains(Within(dialog).OfType<Label>(), label => label.Text.Contains("needs a title"));
        Assert.Equal(0, saves);
    }

    [Fact]
    public void A_save_that_fails_says_why_and_stays_up()
    {
        var dialog = Make();
        dialog.Show("Mix", Tracks, _ => "Couldn't save it: the disk's full");

        Click(dialog, "Save");

        Assert.True(dialog.Visible);
        Assert.Contains(Within(dialog).OfType<Label>(), label => label.Text == "Couldn't save it: the disk's full");
    }
}
