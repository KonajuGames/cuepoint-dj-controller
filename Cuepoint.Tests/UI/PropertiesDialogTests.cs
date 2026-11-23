using Cuepoint.Music;
using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

/// <summary>
/// A song's properties: its details and the data kept for it, with its stems to delete.
/// </summary>
public class PropertiesDialogTests
{
    static IEnumerable<Element> Within(Element element) =>
        element is Panel panel ? panel.Children.SelectMany(Within).Prepend(element) : [element];

    static (PropertiesDialog Dialog, Root Root) Make()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        var root = new Root();
        var dialog = root.Add(new PropertiesDialog(root, font, font, font));
        return (dialog, root);
    }

    static Button ButtonStarting(PropertiesDialog dialog, string text) => Within(dialog).OfType<Button>().First(button => button.Text.StartsWith(text, StringComparison.Ordinal));

    static Song Song() =>
        new(@"C:\Music\Levels.mp3", 8_400_000, new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), "Levels", "Avicii", "") { Duration = TimeSpan.FromSeconds(202) };

    static StoredData Stored(long? stems = null) =>
        new(@"C:\Users\Me\AppData\Local\Cuepoint\library.json", 3, 1, 0, false, @"C:\stems\ABC.stems", stems, null, null);

    [Fact]
    public void A_song_s_details_and_what_s_kept_for_it_are_shown()
    {
        var (dialog, _) = Make();
        var song = Song();
        song.Notes = new SongNotes(Rating: 4, Color: "red");

        dialog.Show(song.Path, TrackProperties.Of(song, Stored(150L * 1024 * 1024)), () => { }, () => { });

        var rows = dialog.Rows.ToDictionary(row => row.Name, row => row.Value);
        Assert.Equal("Levels", rows["Title"]);
        Assert.Equal("None", rows["Album"]);
        Assert.Equal("3:22", rows["Length"]);
        Assert.Equal("8.0 MB", rows["Size"]);
        Assert.Equal("Not analysed yet", rows["Analysis"]);
        Assert.Equal("3 hot cues, 1 memory cue", rows["Cues"]);
        Assert.Equal("Rated ****, red", rows["Notes"]);
        Assert.Equal(@"150.0 MB, in C:\stems\ABC.stems", rows["Stems"]);
        Assert.False(rows.ContainsKey("DJ edit"));
        Assert.Equal(song.Path, dialog.SongPath);
    }

    [Fact]
    public void Its_buttons_edit_the_details_and_delete_the_stems_while_there_are_some()
    {
        var (dialog, _) = Make();
        var song = Song();
        var (edited, deleted) = (0, 0);

        dialog.Show(song.Path, TrackProperties.Of(song, Stored(2048)), () => edited++, () => deleted++);
        ButtonStarting(dialog, "Edit details").RaiseClicked();
        ButtonStarting(dialog, "Delete stems").RaiseClicked();

        Assert.Equal((1, 1), (edited, deleted));
        Assert.Equal("Delete stems (2 KB)...", ButtonStarting(dialog, "Delete stems").Text);

        // Shown again once they're gone: nothing left to delete.
        dialog.Show(song.Path, TrackProperties.Of(song, Stored()), () => edited++, () => deleted++);
        Assert.False(ButtonStarting(dialog, "Delete stems").Visible);
        Assert.StartsWith("None", dialog.Rows.Single(row => row.Name == "Stems").Value);
    }

    [Fact]
    public void Escape_puts_it_away()
    {
        var (dialog, root) = Make();
        dialog.Show(Song().Path, TrackProperties.Of(Song(), Stored()), null, () => { });

        Assert.False(ButtonStarting(dialog, "Edit details").Enabled);
        root.KeyDown(Key.Escape, Modifiers.None);

        Assert.False(dialog.Visible);
        Assert.Null(dialog.SongPath);
    }

    [Theory]
    [InlineData(512, "512 bytes")]
    [InlineData(4096, "4 KB")]
    [InlineData(5L * 1024 * 1024 + 512 * 1024, "5.5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void Sizes_read_in_whichever_unit_suits(long bytes, string expected) => Assert.Equal(expected, TrackProperties.Size(bytes));
}
