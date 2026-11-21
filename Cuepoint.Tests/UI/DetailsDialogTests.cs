using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

/// <summary>
/// The details dialog for one song or several, built with a system font as the app builds it.
/// </summary>
public class DetailsDialogTests
{
    static IEnumerable<Element> Within(Element element) =>
        element is Panel panel ? panel.Children.SelectMany(Within).Prepend(element) : [element];

    static (DetailsDialog Dialog, Root Root) Make()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        var root = new Root();
        var dialog = root.Add(new DetailsDialog(root, font, font, font));
        return (dialog, root);
    }

    static Button Save(DetailsDialog dialog) => Within(dialog).OfType<Button>().First(button => button.Text == "Save");

    static List<TextBox> Boxes(DetailsDialog dialog) => Within(dialog).OfType<TextBox>().ToList();

    [Fact]
    public void Several_songs_start_from_what_they_share_and_leave_a_blank_box_as_each_song_s_own()
    {
        var (dialog, _) = Make();
        (string Artist, string Album)? saved = null;

        dialog.ShowMany(3, "Avicii", null, (artist, album) => saved = (artist, album));

        Assert.True(dialog.IsForMany);
        var (title, artistBox, albumBox) = (Boxes(dialog)[0], Boxes(dialog)[1], Boxes(dialog)[2]);
        Assert.Equal("Avicii", artistBox.Text);
        Assert.Equal("", albumBox.Text);
        Assert.Contains("Various", albumBox.Placeholder);

        albumBox.Text = " True ";
        Save(dialog).RaiseClicked();

        Assert.Equal(("Avicii", "True"), saved);
        Assert.False(dialog.Visible);
        Assert.Equal("", title.Text);
    }

    [Fact]
    public void Several_songs_with_both_boxes_blank_have_nothing_to_change()
    {
        var (dialog, _) = Make();
        var saved = false;
        dialog.ShowMany(2, null, null, (_, _) => saved = true);

        Save(dialog).RaiseClicked();

        Assert.False(saved);
        Assert.True(dialog.Visible);
    }

    [Fact]
    public void One_song_still_needs_a_title()
    {
        var (dialog, _) = Make();
        dialog.ShowMany(2, null, null, (_, _) => { });
        dialog.Close();
        var saved = false;
        dialog.Show("", "Artist", "Album", (_, _, _) => saved = true);

        Assert.False(dialog.IsForMany);
        Save(dialog).RaiseClicked();

        Assert.False(saved);
        Assert.Equal("", Boxes(dialog)[1].Placeholder);
    }
}
