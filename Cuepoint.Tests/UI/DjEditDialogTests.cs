using Cuepoint.Audio.Stems;
using Cuepoint.UI;

namespace Cuepoint.Tests.UI;

/// <summary>
/// The dialog asking how to make a DJ edit, built with a system font as the app builds it.
/// </summary>
public class DjEditDialogTests
{
    static IEnumerable<Element> Within(Element element) =>
        element is Panel panel ? panel.Children.SelectMany(Within).Prepend(element) : [element];

    static (DjEditDialog Dialog, ContextMenu Menu) Make()
    {
        var atlas = new FontAtlas();
        var font = atlas.Add(FontAtlas.FindSystemFont() ?? throw new InvalidOperationException("No system font"), 14);
        var root = new Root();
        var dialog = root.Add(new DjEditDialog(root, font, font, font));
        var menu = new ContextMenu(font);
        dialog.Menu = menu;
        return (dialog, menu);
    }

    static Button ButtonOf(DjEditDialog dialog, string text) => Within(dialog).OfType<Button>().First(button => button.Text == text);

    static readonly DjEditOptions Chosen = new()
    {
        IntroBars = 32,
        IntroChange = DjEditChange.Instruments,
        IntroEnd = DjEditTransition.Echo,
        IntroEndBeats = 8,
        IntroEndAcrossSong = true,
        OutroBars = 8,
        OutroChange = DjEditChange.Nothing,
        OutroStart = DjEditTransition.Filter,
        OutroStartBeats = 2,
        OutroStartUnderSong = true,
        OutroEnd = DjEditTransition.Fade,
        OutroEndBeats = 16,
        EchoBeats = 0.75,
    };

    [Fact]
    public void Every_choice_is_made_as_shown_and_every_dropdown_can_open_its_list()
    {
        var (dialog, menu) = Make();
        DjEditOptions? made = null;

        dialog.Show("Song", Chosen, replacing: null, options => made = options);
        ButtonOf(dialog, "Make").RaiseClicked();

        Assert.Equal(Chosen, made);
        Assert.False(dialog.Visible);
        var dropdowns = Within(dialog).OfType<IOpensMenu>().ToList();
        Assert.Equal(13, dropdowns.Count);
        Assert.All(dropdowns, dropdown => Assert.Same(menu, dropdown.Menu));
    }

    [Fact]
    public void Defaults_puts_every_choice_back()
    {
        var (dialog, _) = Make();

        dialog.Show("Song", Chosen, replacing: null, _ => { });
        ButtonOf(dialog, "Defaults").RaiseClicked();

        Assert.Equal(DjEditOptions.Default, dialog.Options);
    }

    [Fact]
    public void Making_one_again_says_so_and_replaces_it()
    {
        var (dialog, _) = Make();
        var made = false;

        dialog.Show("Song", DjEditOptions.Default, replacing: "Song (Cuepoint DJ Edit).flac is made again", _ => made = true);

        Assert.Contains(Within(dialog).OfType<Label>(), label => label.Text == "Song (Cuepoint DJ Edit).flac is made again");
        ButtonOf(dialog, "Replace").RaiseClicked();
        Assert.True(made);
    }

    [Fact]
    public void Choices_that_would_do_nothing_are_greyed_out()
    {
        var (dialog, _) = Make();

        dialog.Show("Song", DjEditOptions.Default, replacing: null, _ => { });

        // With everything on Auto, there's no length, where or echo to choose.
        var dropdowns = Within(dialog).OfType<Element>().Where(element => element.GetType().Name.StartsWith("Dropdown")).Select(element => (dynamic)element).ToList();
        Assert.Equal(6, dropdowns.Count(dropdown => !(bool)dropdown.Enabled));

        dialog.Show("Song", Chosen, replacing: null, _ => { });

        Assert.All(dropdowns, dropdown => Assert.True((bool)dropdown.Enabled));
    }
}
