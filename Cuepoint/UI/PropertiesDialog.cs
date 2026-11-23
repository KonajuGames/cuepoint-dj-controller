using Brutal.Numerics;
using Cuepoint.Music;

namespace Cuepoint.UI;

/// <summary>
/// A song's properties, in a box over the dimmed screen: its details, which Edit details changes, and the data kept
/// for it, from its analysis and cues to its stems, which Delete stems frees the room of. Escape, Close or a click on
/// the dimmed screen puts it away.
/// </summary>
sealed class PropertiesDialog : Panel
{
    const float NAME_WIDTH = 90;
    const float VALUE_WIDTH = 460;

    readonly Root _root;
    readonly Font _body;
    readonly Panel _box;
    readonly Label _heading;
    readonly Panel _details;
    readonly Panel _stored;
    readonly Label _problem;
    readonly Button _edit;
    readonly Button _deleteStems;
    Action _editDetails = () => { };
    Action _deleteStemsChosen = () => { };

    public float2 ScreenSize { get; set; }

    /// <summary>
    /// The song shown, by path, while it's up; null when it isn't.
    /// </summary>
    public string? SongPath { get; private set; }

    public PropertiesDialog(Root root, Font body, Font bold, Font heading)
    {
        _root = root;
        _body = body;
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
                MinSize = new float2(NAME_WIDTH + VALUE_WIDTH + 52, 0),
            }
        );
        _heading = _box.Add(new Label(heading) { Color = Theme.Text, MaxWidth = NAME_WIDTH + VALUE_WIDTH + 12 });
        _box.Add(new Label(bold, "Details") { Color = Theme.Text });
        _details = _box.Add(new Panel { Spacing = 4 });
        _box.Add(new Label(bold, "Data kept for it") { Color = Theme.Text });
        _stored = _box.Add(new Panel { Spacing = 4 });
        _problem = _box.Add(new Label(body) { Color = Theme.Warning, MaxWidth = NAME_WIDTH + VALUE_WIDTH + 12, Wrap = true });

        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        _edit = buttons.Add(new Button(bold, "Edit details...") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        _deleteStems = buttons.Add(
            new Button(bold, "Delete stems...")
            {
                MinSize = new float2(90, 0),
                Background = Theme.Danger,
                HoverBackground = Theme.DangerHover,
                PressedBackground = Theme.DangerPressed,
                FocusColor = Theme.Focus,
            }
        );
        var close = buttons.Add(new Button(bold, "Close") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        _edit.Clicked += _ => _editDetails();
        _deleteStems.Clicked += _ => _deleteStemsChosen();
        close.Clicked += _ => Close();
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    // A press on the box itself, around its text, mustn't count as a click on the dimmed screen.
    bool _pressedInBox;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override bool IsFocusable => Visible;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// Shows <paramref name="properties"/> of the song at <paramref name="path"/>, or shows them again, as they are
    /// now, if it's up. <paramref name="editDetails"/> is run by Edit details, which is left off if it's null; and
    /// <paramref name="deleteStems"/> by Delete stems, there while the song has stems.
    /// </summary>
    public void Show(string path, TrackProperties properties, Action? editDetails, Action deleteStems)
    {
        var showing = Visible;
        SongPath = path;
        _heading.Text = properties.Title;
        Fill(_details, properties.Details);
        Fill(_stored, properties.Stored);
        _edit.Enabled = editDetails is not null;
        _editDetails = editDetails ?? (() => { });
        _deleteStems.Visible = properties.StemsBytes is not null;
        _deleteStems.Text = properties.StemsBytes is { } bytes ? $"Delete stems ({TrackProperties.Size(bytes)})..." : "Delete stems...";
        _deleteStemsChosen = deleteStems;
        _problem.Text = "";
        Visible = true;
        if (!showing)
            _root.Focus(this);
    }

    /// <summary>
    /// Says, under the rows, what went wrong, leaving the dialog up.
    /// </summary>
    public void ShowProblem(string problem) => _problem.Text = problem;

    void Fill(Panel rows, IReadOnlyList<(string Name, string Value)> values)
    {
        rows.Children.Clear();
        foreach (var (name, value) in values)
        {
            var row = rows.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
            row.Add(new Panel { MinSize = new float2(NAME_WIDTH, 0) }).Add(new Label(_body, name) { Color = Theme.Muted });
            row.Add(new Label(_body, value) { Color = Theme.Text, MaxWidth = VALUE_WIDTH, Wrap = true });
        }
    }

    /// <summary>
    /// The rows shown, as name and value, details first; for tests.
    /// </summary>
    internal IEnumerable<(string Name, string Value)> Rows =>
        _details
            .Children.Concat(_stored.Children)
            .OfType<Panel>()
            .Select(row => (((Label)((Panel)row.Children[0]).Children[0]).Text, ((Label)row.Children[1]).Text));

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        SongPath = null;
        if (_root.Focused == this || _root.Focused is { } focused && IsInside(focused))
            _root.Focus(null);
    }

    bool IsInside(Element element) => _box.Children.OfType<Panel>().Any(panel => panel.Children.Contains(element));

    // Escape puts it away whatever has the focus, even nothing, once a dialog over it has gone.
    public override bool PreviewKeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        Close();
        return true;
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
