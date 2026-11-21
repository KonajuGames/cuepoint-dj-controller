using Brutal.Numerics;

namespace Cuepoint.UI;

/// <summary>
/// A song's title, artist and album to change, in a box over the dimmed screen: Tab between them, Enter or Save to
/// write them into the file's tags, Escape or Cancel to leave them. Whatever stops them being saved is shown under the
/// boxes, and the dialog stays up.
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
    Action<string, string, string> _save = (_, _, _) => { };

    public float2 ScreenSize { get; set; }

    public DetailsDialog(Root root, Font body, Font bold, Font heading)
    {
        _root = root;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.6f);

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
            row.Add(new Panel { MinSize = new float2(60, 0) }).Add(new Label(body, name) { Color = Theme.Muted });
            var box = row.Add(new TextBox(body) { Width = 340, FocusColor = Theme.Focus });
            box.Submitted += _ => Save();
            box.Cancelled += _ => Close();
            box.TextChanged += _ => _problem!.Text = "";
            return box;
        }
        _title = Field("Title");
        _artist = Field("Artist");
        _album = Field("Album");
        _problem = _box.Add(new Label(body) { Color = Colors.Hex(0xEE6666), MaxWidth = 420 });
        _box.Add(new Label(body, "Saved in the file's tags; its analysis and cues stay.") { Color = Theme.Muted, MaxWidth = 420 });
        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        var save = buttons.Add(new Button(bold, "Save") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        var cancel = buttons.Add(new Button(bold, "Cancel") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        save.Clicked += _ => Save();
        cancel.Clicked += _ => Close();
    }

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// Shows a song's details to change, starting in its title. <paramref name="save"/> takes the title, artist and
    /// album, and may throw <see cref="InvalidOperationException"/> to say why it couldn't, leaving the dialog up.
    /// </summary>
    public void Show(string title, string artist, string album, Action<string, string, string> save)
    {
        _heading.Text = "Edit details";
        (_title.Text, _artist.Text, _album.Text) = (title, artist, album);
        _save = save;
        _problem.Text = "";
        Visible = true;
        _root.Focus(_title);
        _title.SelectAll();
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    void Save()
    {
        if (string.IsNullOrWhiteSpace(_title.Text))
        {
            _problem.Text = "A song needs a title";
            return;
        }
        try
        {
            _save(_title.Text.Trim(), _artist.Text.Trim(), _album.Text.Trim());
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
