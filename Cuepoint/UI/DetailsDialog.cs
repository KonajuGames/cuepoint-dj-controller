using Brutal.Numerics;

namespace Cuepoint.UI;

/// <summary>
/// A song's title, artist and album to change, in a box over the dimmed screen, or several songs' artist and album
/// together: Tab between them, Enter or Save to write them into the files' tags, Escape or Cancel to leave them.
/// Whatever stops them being saved is shown under the boxes, and the dialog stays up.
/// </summary>
sealed class DetailsDialog : Panel
{
    readonly Root _root;
    readonly Panel _box;
    readonly Label _heading;
    readonly Label _problem;
    readonly TextBox _title;
    readonly TextBox _artist;
    readonly TextBox _album;
    readonly TextBox _genre;
    readonly Panel _titleRow;
    readonly Label _note;
    Action<string, string, string, string> _save = (_, _, _, _) => { };
    bool _many;

    public float2 ScreenSize { get; set; }

    public DetailsDialog(Root root, Font body, Font bold, Font heading)
    {
        _root = root;
        Visible = false;
        Background = Theme.Scrim(0.6f);

        _box = Add(
            new Panel
            {
                Padding = 20,
                Spacing = 10,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(460, 0),
            }
        );
        _heading = _box.Add(new Label(heading) { Color = Theme.Text });
        TextBox Field(string name)
        {
            var row = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            _lastRow = row;
            row.Add(new Panel { MinSize = new float2(60, 0) }).Add(new Label(body, name) { Color = Theme.Muted });
            var box = row.Add(new TextBox(body) { Width = 340, FocusColor = Theme.Focus });
            box.Submitted += _ => Save();
            box.Cancelled += _ => Close();
            box.TextChanged += _ => _problem!.Text = "";
            return box;
        }
        _title = Field("Title");
        _titleRow = _lastRow!;
        _artist = Field("Artist");
        _album = Field("Album");
        _genre = Field("Genre");
        _problem = _box.Add(new Label(body) { Color = Theme.Warning, MaxWidth = 420, Wrap = true });
        _note = _box.Add(new Label(body) { Color = Theme.Muted, MaxWidth = 420, Wrap = true });
        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        var save = buttons.Add(new Button(bold, "Save") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        var cancel = buttons.Add(new Button(bold, "Cancel") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        save.Clicked += _ => Save();
        cancel.Clicked += _ => Close();
    }

    Panel? _lastRow;

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// Shows a song's details to change, starting in its title. <paramref name="save"/> takes the title, artist, album
    /// and genre, and may throw <see cref="InvalidOperationException"/> to say why it couldn't, leaving the dialog up.
    /// </summary>
    public void Show(string title, string artist, string album, string genre, Action<string, string, string, string> save)
    {
        _heading.Text = "Edit details";
        (_title.Text, _artist.Text, _album.Text, _genre.Text) = (title, artist, album, genre);
        (_artist.Placeholder, _album.Placeholder, _genre.Placeholder) = ("", "", "");
        _note.Text = "Saved in the file's tags; its analysis and cues stay.";
        (_many, _titleRow.Visible) = (false, true);
        _save = save;
        _problem.Text = "";
        Visible = true;
        _root.Focus(_title);
        _title.SelectAll();
    }

    /// <summary>
    /// Shows <paramref name="count"/> songs' artist, album and genre to change together, each starting with what they
    /// all share, or blank if they differ. <paramref name="save"/> takes the artist, album and genre, any blank to
    /// leave each song's own, and may throw <see cref="InvalidOperationException"/> to say why it couldn't.
    /// </summary>
    public void ShowMany(int count, string? artist, string? album, string? genre, Action<string, string, string> save)
    {
        const string VARIOUS = "Various: left as they are";
        _heading.Text = $"Edit details of {count} songs";
        (_title.Text, _artist.Text, _album.Text, _genre.Text) = ("", artist ?? "", album ?? "", genre ?? "");
        (_artist.Placeholder, _album.Placeholder, _genre.Placeholder) = (artist is null ? VARIOUS : "", album is null ? VARIOUS : "", genre is null ? VARIOUS : "");
        _note.Text = "Saved in each file's tags; their titles, analysis and cues stay. A box left blank leaves each song's own.";
        (_many, _titleRow.Visible) = (true, false);
        _save = (_, newArtist, newAlbum, newGenre) => save(newArtist, newAlbum, newGenre);
        _problem.Text = "";
        Visible = true;
        _root.Focus(_artist);
        _artist.SelectAll();
    }

    /// <summary>
    /// Whether it's showing several songs' details, for tests.
    /// </summary>
    internal bool IsForMany => _many;

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    void Save()
    {
        if (!_many && string.IsNullOrWhiteSpace(_title.Text))
        {
            _problem.Text = "A song needs a title";
            return;
        }
        if (_many && string.IsNullOrWhiteSpace(_artist.Text) && string.IsNullOrWhiteSpace(_album.Text) && string.IsNullOrWhiteSpace(_genre.Text))
        {
            _problem.Text = "Nothing to change: give them an artist, an album or a genre";
            return;
        }
        try
        {
            _save(_title.Text.Trim(), _artist.Text.Trim(), _album.Text.Trim(), _genre.Text.Trim());
            Close();
        }
        catch (InvalidOperationException e)
        {
            _problem.Text = e.Message;
        }
    }

    public override float2 Measure()
    {
        _problem.Visible = _problem.Text.Length > 0;
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 3)));
    }
}
