using Brutal.Numerics;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// The songs in whatever's chosen in the folder list, searchable, with a "Load" button for each deck and a status
/// line along the bottom. Songs are analysed as they come into view, each row filling like a progress bar meanwhile,
/// and their tempo and key appear when done. It raises events rather than loading anything itself. Sized by whoever
/// lays it out, with <see cref="FixedSize"/>; call <see cref="Update"/> once a frame.
/// </summary>
sealed class SongBrowser : Panel
{
    const float PAD = 10;

    /// <summary>
    /// How often songs from a filter or virtual folder are worked out again, as songs are analysed and decks change.
    /// </summary>
    static readonly TimeSpan LiveRefresh = TimeSpan.FromSeconds(1);

    readonly MusicLibrary _library;
    readonly LibraryAnalyser _analyser;
    readonly Label _heading;
    readonly Label _count;
    readonly TextBox _search;
    readonly List<Button> _loadButtons = [];
    readonly ListHeader<Song> _header;
    readonly ScrollPanel _scroll;
    readonly Label _status;
    readonly ListColumn<Song> _titleColumn;
    LibraryNode? _source;
    IReadOnlyList<Song> _sourceSongs = [];
    DateTime _refreshedAt;

    public ListView<Song> List { get; }

    public float2 FixedSize { get; set; } = new(800, 400);

    /// <summary>
    /// A deck's "Load" button was pressed: the song, and the deck's name.
    /// </summary>
    public event Action<Song, string>? LoadRequested;

    /// <summary>
    /// A song was double-clicked or Enter was pressed on it, leaving the choice of deck to the app.
    /// </summary>
    public event Action<Song>? SongActivated;

    /// <summary>
    /// Delete was pressed on songs listed from a virtual folder, to take them out of it.
    /// </summary>
    public event Action<VirtualFolder, IReadOnlyList<Song>>? RemoveRequested;

    /// <summary>
    /// Songs listed from a virtual folder were dragged to a new place in it: before the song at that index in the
    /// folder's list, or at the end for its count.
    /// </summary>
    public event Action<VirtualFolder, IReadOnlyList<Song>, int>? MoveRequested;

    /// <summary>
    /// Songs were right-clicked: the selection, and where, for a menu.
    /// </summary>
    public event Action<IReadOnlyList<Song>, float2>? SongsSecondaryClicked;

    /// <summary>
    /// Whether a song counts as played this session, for its tick in the first column.
    /// </summary>
    public Func<Song, bool> IsPlayed { get; set; } = _ => false;

    public SongBrowser(MusicLibrary library, LibraryAnalyser analyser, IEnumerable<string> deckNames, Font body, Font bold, Images images)
    {
        _library = library;
        _analyser = analyser;
        Background = Theme.Panel;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;

        _heading = Add(new Label(bold) { Color = Theme.Text });
        _count = Add(new Label(body) { Color = Theme.Muted });
        _search = Add(new TextBox(body) { Width = 280, Placeholder = "Search title, artist or album", FocusColor = Theme.Focus });
        _search.TextChanged += _ => Filter();

        List = new ListView<Song>(body)
        {
            EmptyText = "No songs here",
            DragLabel = song => $"{song.Artist} - {song.Title}",
            RowProgress = analyser.ProgressOf,
            ProgressColor = Theme.Fade(Theme.Accent, 0.1f),
            AlternateRow = Colors.Hex(0xFFFFFF, 0.03f),
        };
        _titleColumn = new ListColumn<Song>("Title", 300, song => song.Title);
        List.Columns.Add(
            new ListColumn<Song>("", 30, _ => "")
            {
                Icon = song => IsPlayed(song) ? (images.Played, Theme.Played) : null,
            }
        );
        List.Columns.Add(_titleColumn);
        List.Columns.Add(new ListColumn<Song>("Artist", 220, song => song.Artist));
        List.Columns.Add(new ListColumn<Song>("Album", 200, song => song.Album) { Color = _ => Theme.Muted });
        List.Columns.Add(new ListColumn<Song>("Key", 84, FormatKey) { Color = KeyColor });
        List.Columns.Add(new ListColumn<Song>("Length", 66, song => Format(song.Duration)) { AlignRight = true });
        List.Columns.Add(new ListColumn<Song>("BPM", 70, FormatBpm) { AlignRight = true });

        foreach (var name in deckNames)
        {
            var button = Add(new Button(body, $"Load {name}") { FocusColor = Theme.Focus, TextColor = Theme.DeckColor(name) });
            button.Clicked += _ =>
            {
                if (List.SelectedItem is { } song)
                    LoadRequested?.Invoke(song, name);
            };
            _loadButtons.Add(button);
        }

        _header = Add(new ListHeader<Song>(List));
        _scroll = Add(new ScrollPanel());
        _scroll.Add(List);
        List.ScrollOwner = _scroll;
        List.ItemActivated += song => SongActivated?.Invoke(song);
        List.DeleteRequested += songs =>
        {
            if (_source?.Folder is { } folder)
                RemoveRequested?.Invoke(folder, songs);
        };
        List.ItemsSecondaryClicked += (songs, at) => SongsSecondaryClicked?.Invoke(songs, at);
        List.Reordered += (songs, before) =>
        {
            if (_source?.Folder is { } folder)
                MoveRequested?.Invoke(folder, songs, before);
        };
        List.SelectionChanged += _ => UpdateButtons();

        _status = Add(new Label(body) { Color = Theme.Muted });
        UpdateButtons();
    }

    public Song? SelectedSong => List.SelectedItem;

    /// <summary>
    /// What to list the songs of: a folder, virtual folder or filter from the folder list.
    /// </summary>
    public LibraryNode? Source
    {
        get => _source;
        set
        {
            _source = value;
            _sourceSongs = value?.Songs ?? [];
            _refreshedAt = DateTime.UtcNow;
            _heading.Text = value?.Name ?? "";
            Filter();
            _scroll.ScrollTo(float2.Zero);
        }
    }

    /// <summary>
    /// Lists the source's songs again, keeping the place in the list, after they've changed (a virtual folder edited,
    /// say) or it's been renamed.
    /// </summary>
    public void Reload()
    {
        if (_source is null)
            return;
        _sourceSongs = _source.Songs;
        _refreshedAt = DateTime.UtcNow;
        _heading.Text = _source.Name;
        Filter();
    }

    /// <summary>
    /// The line along the bottom, for news such as a song failing to load.
    /// </summary>
    public string Status
    {
        get => _status.Text;
        set => _status.Text = value;
    }

    public void Update()
    {
        if (_source is { IsLive: true } live && DateTime.UtcNow - _refreshedAt >= LiveRefresh)
        {
            _sourceSongs = live.Songs;
            _refreshedAt = DateTime.UtcNow;
            Filter();
        }

        // Songs in view (as last drawn) are analysed first, from the top down.
        var (first, count) = List.VisibleRows.GetOffsetAndLength(List.Items.Count);
        if (count > 0)
            _analyser.Request(List.Items.Skip(first).Take(count).ToList());

        var songs = List.Items.Count == _sourceSongs.Count ? $"{_sourceSongs.Count} songs" : $"{List.Items.Count} of {_sourceSongs.Count} songs";
        var tags = _library.IsReadingTags ? $", reading tags {_library.TaggedCount}/{_library.Songs.Count}" : "";
        var analysing =
            _analyser.ActiveCount == 0 ? ""
            : _analyser.QueuedCount == 0 ? $", analysing {_analyser.ActiveCount}"
            : $", analysing {_analyser.ActiveCount}, {_analyser.QueuedCount} waiting";
        _count.Text = songs + tags + analysing;
    }

    /// <summary>
    /// Shows the source's songs again, as tags arriving can change what matches the search.
    /// </summary>
    public void Refilter()
    {
        if (_search.Text.Length > 0)
            Filter();
    }

    void Filter()
    {
        var query = _search.Text.Trim();
        List.Items = query.Length == 0 ? _sourceSongs : _sourceSongs.Where(song => song.Matches(query)).ToList();

        // A virtual folder's songs can be put in a new order, but only seeing them all: dropping between two songs a
        // search has brought together would be ambiguous.
        List.CanReorder = _source?.Folder is not null && query.Length == 0;
    }

    void UpdateButtons()
    {
        foreach (var button in _loadButtons)
            button.Enabled = List.SelectedItem is not null;
    }

    public override float2 Measure()
    {
        var inner = FixedSize - new float2(2 * PAD, 2 * PAD);

        // The title takes whatever width the other columns leave, less the scrollbar's.
        var others = List.Columns.Where(column => column != _titleColumn).Sum(column => column.Width);
        _titleColumn.Width = MathF.Max(120, inner.X - _scroll.ScrollbarHitWidth - others);

        foreach (var child in Children)
        {
            if (child != _scroll)
                child.Measure();
        }

        var listHeight = inner.Y - _search.Size.Y - 6 - _header.Size.Y - 6 - _status.Size.Y;
        _scroll.MinSize = new float2(inner.X, listHeight);
        _scroll.MaxWidth = inner.X;
        _scroll.MaxHeight = listHeight;
        _scroll.Measure();
        _heading.MaxWidth = MathF.Max(60, inner.X - _search.Size.X - _loadButtons.Sum(button => button.Size.X + 6) - 24 - _count.Size.X);
        return Size = FixedSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));
        var left = position.X + PAD;
        var right = position.X + FixedSize.X - PAD;
        var y = position.Y + PAD;

        var rowHeight = _search.Size.Y;
        Place(_heading, left, y + (rowHeight - _heading.Size.Y) / 2);
        Place(_count, left + _heading.Size.X + 12, y + (rowHeight - _count.Size.Y) / 2);
        var x = right;
        for (var i = _loadButtons.Count - 1; i >= 0; i--)
        {
            x -= _loadButtons[i].Size.X;
            Place(_loadButtons[i], x, y + (rowHeight - _loadButtons[i].Size.Y) / 2);
            x -= 6;
        }
        Place(_search, x - 6 - _search.Size.X, y);

        y += rowHeight + 6;
        Place(_header, left, y);
        y += _header.Size.Y;
        Place(_scroll, left, y);
        y += _scroll.Size.Y + 6;
        Place(_status, left, y);
    }

    static string Format(TimeSpan? time) => time is { } t ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : "";

    /// <summary>
    /// The main tempo, with "~" before it if the tempo varies through the song.
    /// </summary>
    static string FormatBpm(Song song) =>
        song.Analysis is { } analysis ? $"{(analysis.Grid.IsVariable ? "~" : "")}{analysis.Bpm:F1}"
        : song.AnalysisError is not null ? "–"
        : "";

    /// <summary>
    /// The Camelot code first, since that's what mixing goes by, then the key itself.
    /// </summary>
    static string FormatKey(Song song) =>
        song.Analysis is { } analysis ? $"{analysis.Key.Camelot}  {analysis.Key.ShortName}"
        : song.AnalysisError is not null ? "–"
        : "";

    static float4? KeyColor(Song song) =>
        song.Analysis is { } analysis ? Theme.CamelotColor(analysis.Key.CamelotNumber) : null;
}
