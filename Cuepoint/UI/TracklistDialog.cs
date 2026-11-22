using Brutal.Numerics;
using Cuepoint.Music;

namespace Cuepoint.UI;

/// <summary>
/// A recording's tracklist to change, in a box over the dimmed screen: each song's time into the recording, artist
/// and title, a song taken out with its cross or put in with Add a song, then Save to write its cue sheets and text
/// again, or Escape or Cancel to leave it. Whatever stops it being saved is shown under the list, and the dialog
/// stays up.
/// </summary>
sealed class TracklistDialog : Panel
{
    const float TIME_WIDTH = 80;
    const float ARTIST_WIDTH = 200;
    const float TITLE_WIDTH = 260;

    readonly Root _root;
    readonly Font _body;
    readonly Panel _box;
    readonly Label _heading;
    readonly ScrollPanel _scroll;
    readonly Panel _rows;
    readonly Label _empty;
    readonly Label _problem;
    readonly List<Row> _list = [];
    Func<IReadOnlyList<RecordedTrack>, string?> _save = _ => null;
    bool _hours;

    /// <summary>
    /// One song's row: its boxes, and the time it had, kept exactly unless what's typed changes.
    /// </summary>
    sealed record Row(Panel Panel, TextBox Time, TextBox Artist, TextBox Title, TimeSpan Was, string WasText);

    public float2 ScreenSize { get; set; }

    public TracklistDialog(Root root, Font body, Font bold, Font heading)
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
            }
        );
        _heading = _box.Add(new Label(heading) { Color = Theme.Text });
        var columns = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        columns.Add(new Panel { MinSize = new float2(TIME_WIDTH, 0) }).Add(new Label(body, "Time") { Color = Theme.Muted });
        columns.Add(new Panel { MinSize = new float2(ARTIST_WIDTH, 0) }).Add(new Label(body, "Artist") { Color = Theme.Muted });
        columns.Add(new Panel { MinSize = new float2(TITLE_WIDTH, 0) }).Add(new Label(body, "Title") { Color = Theme.Muted });
        _scroll = _box.Add(new ScrollPanel { MaxHeight = 380 });
        _rows = _scroll.Add(new Panel { Spacing = 6 });
        _empty = _box.Add(new Label(body, "No songs in it yet") { Color = Theme.Faint });

        var add = _box.Add(new Button(body, "Add a song") { MinSize = new float2(110, 0), FocusColor = Theme.Focus });
        add.Clicked += _ => AddRow(new RecordedTrack(_list.Count > 0 ? ParsedOr(_list[^1]) + TimeSpan.FromMinutes(3) : TimeSpan.Zero, "", ""), focus: true);
        _problem = _box.Add(new Label(body) { Color = Theme.Warning, MaxWidth = 560, Wrap = true });
        _box.Add(
            new Label(body, "Times are from the start of the recording, as 4:05 or 1:04:05. Saving writes the cue sheet beside each of its files and the tracklist beside the first.")
            {
                Color = Theme.Muted,
                MaxWidth = 560,
                Wrap = true,
            }
        );
        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        var save = buttons.Add(new Button(bold, "Save") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        var cancel = buttons.Add(new Button(bold, "Cancel") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });
        save.Clicked += _ => Save();
        cancel.Clicked += _ => Close();
    }

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override Cursor Cursor => Cursor.Arrow;

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        Close();
        return true;
    }

    /// <summary>
    /// Shows <paramref name="tracks"/>, the tracklist of the recording called <paramref name="name"/>, to change.
    /// <paramref name="save"/> takes the songs in order and says why it couldn't save them, or null once it has.
    /// </summary>
    public void Show(string name, IReadOnlyList<RecordedTrack> tracks, Func<IReadOnlyList<RecordedTrack>, string?> save)
    {
        _heading.Text = $"Tracklist of {name}";
        _save = save;
        _problem.Text = "";
        _hours = tracks.Count > 0 && tracks[^1].At.TotalHours >= 1;
        _rows.Children.Clear();
        _list.Clear();
        foreach (var track in tracks)
            AddRow(track, focus: false);
        _scroll.ScrollTo(float2.Zero);
        Visible = true;
        _root.Focus(_list.Count > 0 ? _list[0].Artist : null);
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    /// <summary>
    /// The songs as they're typed now, for tests: their time, artist and title boxes.
    /// </summary>
    internal IReadOnlyList<(TextBox Time, TextBox Artist, TextBox Title)> Rows => _list.Select(row => (row.Time, row.Artist, row.Title)).ToList();

    /// <summary>
    /// Takes out the song in row <paramref name="index"/>, as its cross does.
    /// </summary>
    internal void RemoveRow(int index)
    {
        var row = _list[index];
        _list.RemoveAt(index);
        _rows.Children.Remove(row.Panel);
        _problem.Text = "";
    }

    void AddRow(RecordedTrack track, bool focus)
    {
        var panel = _rows.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        TextBox Box(string text, float width)
        {
            var box = panel.Add(new TextBox(_body) { Text = text, Width = width, FocusColor = Theme.Focus });
            box.Submitted += _ => Save();
            box.Cancelled += _ => Close();
            box.TextChanged += _ => _problem.Text = "";
            return box;
        }
        var text = RecordingTracklist.Clock(track.At, _hours || track.At.TotalHours >= 1);
        var row = new Row(panel, Box(text, TIME_WIDTH), Box(track.Artist, ARTIST_WIDTH), Box(track.Title, TITLE_WIDTH), track.At, text);
        var remove = panel.Add(new Button(_body, "×") { MinSize = new float2(28, 0), FocusColor = Theme.Focus, Tooltip = "Take this song out of the tracklist" });
        remove.Clicked += _ => RemoveRow(_list.IndexOf(row));
        _list.Add(row);
        if (focus)
        {
            _root.Focus(row.Time);
            row.Time.SelectAll();
            _scroll.ScrollTo(new float2(0, float.MaxValue));
        }
    }

    // What's typed, or the time it had if that's not been changed, kept to the frame rather than the second.
    static TimeSpan? Parsed(Row row) => row.Time.Text.Trim() == row.WasText ? row.Was : RecordingTracklist.ParseClock(row.Time.Text);

    static TimeSpan ParsedOr(Row row) => Parsed(row) ?? row.Was;

    void Save()
    {
        var tracks = new List<RecordedTrack>();
        for (var i = 0; i < _list.Count; i++)
        {
            var row = _list[i];
            if (Parsed(row) is not { } at)
            {
                _problem.Text = $"Song {i + 1}: \"{row.Time.Text.Trim()}\" isn't a time; give it as 4:05, or 1:04:05";
                _root.Focus(row.Time);
                return;
            }
            if (string.IsNullOrWhiteSpace(row.Title.Text))
            {
                _problem.Text = $"Song {i + 1} needs a title";
                _root.Focus(row.Title);
                return;
            }
            tracks.Add(new RecordedTrack(at, row.Artist.Text.Trim(), row.Title.Text.Trim()));
        }
        if (_save(tracks.OrderBy(track => track.At).ToList()) is { } error)
        {
            _problem.Text = error;
            return;
        }
        Close();
    }

    public override float2 Measure()
    {
        _problem.Visible = _problem.Text.Length > 0;
        _empty.Visible = _list.Count == 0;
        _scroll.Visible = _list.Count > 0;
        _scroll.MaxHeight = MathF.Max(120, ScreenSize.Y * 0.5f);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 4)));
    }
}
