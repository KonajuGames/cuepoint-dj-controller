using Brutal.Numerics;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// The songs in whatever's chosen in the folder list, searchable, with a "Load" button for each deck and a status
/// line along the bottom. Songs are analysed as they come into view, each row filling like a progress bar meanwhile,
/// and their tempo and key appear when done. Clicking a column's heading sorts by it, A to Z, then Z to A, then back
/// to the order they came in; a column's edge drags to resize it, and right-clicking the headings chooses which
/// columns show (see <see cref="Layout"/>). It raises events rather than loading anything itself. Sized by whoever
/// lays it out, with <see cref="FixedSize"/>; call <see cref="Update"/> once a frame.
/// </summary>
sealed class SongBrowser : Panel
{
    const float PAD = 10;

    /// <summary>
    /// How often songs from a filter or virtual folder are worked out again, as songs are analysed and decks change.
    /// </summary>
    static readonly TimeSpan LiveRefresh = TimeSpan.FromSeconds(1);

    /// <summary>
    /// What columns started at before they were widened to fit the large text. A layout kept with one of these was
    /// never resized by hand, since every width used to be kept, so it takes the column's default now instead.
    /// </summary>
    static readonly Dictionary<string, float> FormerDefaultWidths = new() { ["Key"] = 84, ["Length"] = 66, ["BPM"] = 70 };

    readonly MusicLibrary _library;
    readonly LibraryAnalyser _analyser;
    readonly Label _heading;
    readonly Label _count;
    readonly TextBox _search;
    readonly Button _clearSearch;
    readonly BulkProgress _bulkProgress;
    readonly Button _cancelBulk;
    readonly List<Button> _loadButtons = [];
    readonly ListHeader<Song> _header;
    readonly ScrollPanel _scroll;
    readonly Label _status;
    readonly ListColumn<Song> _titleColumn;

    // Every column there is, in order, shown or not, and how wide each starts.
    readonly List<ListColumn<Song>> _columns = [];
    readonly Dictionary<ListColumn<Song>, float> _defaultWidths = [];
    readonly HashSet<ListColumn<Song>> _hidden = [];
    ListColumn<Song>? _sortColumn;
    bool _sortDescending;
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
    /// Analyse All was stopped with the cross by its progress bar: how many songs were left unanalysed.
    /// </summary>
    public event Action<int>? AnalyseAllCancelled;

    /// <summary>
    /// The column headings were right-clicked, where, for a menu of <see cref="ColumnMenu"/>.
    /// </summary>
    public event Action<float2>? ColumnMenuRequested;

    /// <summary>
    /// Whether a song counts as played this session, for its tick in its column.
    /// </summary>
    public Func<Song, bool> IsPlayed { get; set; } = _ => false;

    /// <summary>
    /// The decks a song's loaded on, by name, for their letters in the first column.
    /// </summary>
    public Func<Song, IReadOnlyList<string>> DecksOf { get; set; } = _ => [];

    public SongBrowser(MusicLibrary library, LibraryAnalyser analyser, IEnumerable<string> deckNames, Font body, Font bold, Images images)
    {
        _library = library;
        _analyser = analyser;
        Background = Theme.Panel;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;

        _heading = Add(new Label(bold) { Color = Theme.Text });
        _count = Add(new Label(body) { Color = Theme.Muted });
        _search = Add(new TextBox(body) { Width = 280, Placeholder = "Search title, artist or album", FocusColor = Theme.Focus, ClearOnEscape = true });
        _search.TextChanged += _ => Filter();

        // A cross at the search box's end empties it, while there's anything to empty.
        _clearSearch = Add(
            new Button(body, "")
            {
                Icon = images.Cross,
                Padding = 0,
                Background = Colors.Transparent,
                HoverBackground = Colors.Hex(0xFFFFFF, 0.1f),
                PressedBackground = Colors.Hex(0xFFFFFF, 0.05f),
                TextColor = Theme.Muted,
                Visible = false,
                Tooltip = "Clear the search",
            }
        );
        _clearSearch.Clicked += _ =>
        {
            _search.Text = "";
            Filter();
        };

        // While Analyse All runs, how far through it is, with a cross at the end to stop it.
        _bulkProgress = Add(new BulkProgress(body) { Visible = false });
        _cancelBulk = Add(
            new Button(body, "")
            {
                Icon = images.Cross,
                Padding = 0,
                Background = Colors.Transparent,
                HoverBackground = Colors.Hex(0xFFFFFF, 0.1f),
                PressedBackground = Colors.Hex(0xFFFFFF, 0.05f),
                TextColor = Theme.Muted,
                Visible = false,
                Tooltip = "Stop analysing all; songs on screen are still analysed",
            }
        );
        _cancelBulk.Clicked += _ => AnalyseAllCancelled?.Invoke(_analyser.CancelBulk());

        List = new ListView<Song>(body)
        {
            EmptyText = "No songs here",
            DragLabel = song => $"{song.Artist} - {song.Title}",
            RowProgress = analyser.ProgressOf,
            ProgressColor = Theme.Fade(Theme.Accent, 0.1f),
            AlternateRow = Colors.Hex(0xFFFFFF, 0.03f),
        };
        // The title takes whatever width the rest leave, so it's the one column that isn't sized by hand.
        _titleColumn = new ListColumn<Song>("Title", 300, song => song.Title) { SortKey = song => Text(song.Title), Resizable = false };
        _columns.Add(
            new ListColumn<Song>("", 26, song => string.Concat(DecksOf(song)))
            {
                Id = "Deck",
                Color = song => DecksOf(song) is [var deck, ..] ? Theme.DeckColor(deck) : null,
                SortKey = song => DecksOf(song) is { Count: > 0 } decks ? string.Concat(decks) : null,
                Resizable = false,
            }
        );
        _columns.Add(
            new ListColumn<Song>("", 30, _ => "")
            {
                Id = "Played",
                Icon = song => IsPlayed(song) ? (images.Played, Theme.Played) : null,
                SortKey = song => IsPlayed(song) ? 0 : null,
                Resizable = false,
            }
        );
        _columns.Add(_titleColumn);
        _columns.Add(new ListColumn<Song>("Artist", 220, song => song.Artist) { SortKey = song => Text(song.Artist) });
        _columns.Add(new ListColumn<Song>("Album", 200, song => song.Album) { Color = _ => Theme.Muted, SortKey = song => Text(song.Album) });
        // Wide enough for the large list text, too.
        _columns.Add(new ListColumn<Song>("Key", 112, FormatKey) { Color = KeyColor, SortKey = KeyOrder });
        _columns.Add(new ListColumn<Song>("Length", 80, song => Format(song.Duration)) { AlignRight = true, SortKey = song => song.Duration });
        _columns.Add(new ListColumn<Song>("BPM", 86, FormatBpm) { AlignRight = true, SortKey = song => song.Analysis?.Bpm });
        foreach (var column in _columns)
            _defaultWidths[column] = column.Width;
        ShowColumns();

        foreach (var name in deckNames)
        {
            var button = Add(
                new Button(body, $"Load {name}") { FocusColor = Theme.Focus, TextColor = Theme.DeckColor(name), Tooltip = $"Load the chosen song onto deck {name}" }
            );
            button.Clicked += _ =>
            {
                if (List.SelectedItem is { } song)
                    LoadRequested?.Invoke(song, name);
            };
            _loadButtons.Add(button);
        }

        _header = Add(new ListHeader<Song>(List));
        _header.ColumnClicked += column =>
        {
            // A to Z, then Z to A, then as they came.
            if (column != _sortColumn)
                (_sortColumn, _sortDescending) = (column, false);
            else if (!_sortDescending)
                _sortDescending = true;
            else
                _sortColumn = null;
            Filter();
        };
        _header.MenuRequested += at => ColumnMenuRequested?.Invoke(at);
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
            if (_source is not null)
                _source.SongsLoaded -= SourceSongsLoaded;
            _source = value;
            if (value is not null)
                value.SongsLoaded += SourceSongsLoaded;
            _sourceSongs = value?.Songs ?? [];
            _refreshedAt = DateTime.UtcNow;
            _heading.Text = value?.Name ?? "";
            Filter();
            _scroll.ScrollTo(float2.Zero);
        }
    }

    // A folder's songs, looked for in the background, have come in.
    void SourceSongsLoaded(LibraryNode node)
    {
        if (node == _source)
            Reload();
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

        _bulkProgress.Visible = _cancelBulk.Visible = _analyser.IsBulkActive;
        _bulkProgress.Done = _analyser.BulkDone;
        _bulkProgress.Total = _analyser.BulkTotal;

        var songs = List.Items.Count == _sourceSongs.Count ? $"{_sourceSongs.Count} songs" : $"{List.Items.Count} of {_sourceSongs.Count} songs";

        // How long the songs listed play for, of those whose lengths are known (read with their tags).
        var total = TimeSpan.Zero;
        foreach (var song in List.Items)
            total += song.Duration ?? TimeSpan.Zero;
        if (total > TimeSpan.Zero)
            songs += ", " + FormatTotal(total);
        // Nothing listed yet while a folder's being looked through; it says so rather than that there's nothing there.
        var loading = _source is { IsLoadingSongs: true };
        List.EmptyText = loading ? "Looking through the folder..." : "No songs here";
        var tags =
            loading ? ", looking through the folder"
            : _library.IsScanning ? ", looking through the library"
            : _library.IsReadingTags ? $", reading tags {_library.TaggedCount}/{_library.Songs.Count}"
            : "";
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
        _clearSearch.Visible = _search.Text.Length > 0;
        var query = _search.Text.Trim();
        IEnumerable<Song> songs = query.Length == 0 ? _sourceSongs : _sourceSongs.Where(song => song.Matches(query));

        // Sorted, those with nothing to sort by go last either way, and ties keep the order they came in.
        if (_sortColumn?.SortKey is { } key)
        {
            var keyed = songs.Select(song => (Song: song, Key: key(song))).OrderBy(pair => pair.Key is null);
            songs = (_sortDescending ? keyed.ThenByDescending(pair => pair.Key) : keyed.ThenBy(pair => pair.Key)).Select(pair => pair.Song);
        }
        List.Items = songs as IReadOnlyList<Song> ?? songs.ToList();
        _header.SortColumn = _sortColumn;
        _header.SortDescending = _sortDescending;

        // A virtual folder's songs can be put in a new order, but only seeing them all, in their own order: dropping
        // between two songs a search or sort has brought together would be ambiguous.
        List.CanReorder = _source?.Folder is not null && query.Length == 0 && _sortColumn is null;
    }

    /// <summary>
    /// Lists the columns not hidden, in their order.
    /// </summary>
    void ShowColumns()
    {
        List.Columns.Clear();
        List.Columns.AddRange(_columns.Where(column => !_hidden.Contains(column)));
    }

    /// <summary>
    /// The choices for the column headings' menu: each column but the title, ticked if it shows, to show or hide it;
    /// and putting them all back as they began.
    /// </summary>
    public IReadOnlyList<MenuItem> ColumnMenu()
    {
        var items = _columns
            .Where(column => column != _titleColumn)
            .Select(column => new MenuItem(
                column.Id,
                () =>
                {
                    if (!_hidden.Remove(column))
                        _hidden.Add(column);
                    if (_hidden.Contains(_sortColumn!))
                    {
                        _sortColumn = null;
                        Filter();
                    }
                    ShowColumns();
                },
                Checked: !_hidden.Contains(column)
            ))
            .ToList();
        items.Add(
            new MenuItem(
                "Reset columns",
                () =>
                {
                    _hidden.Clear();
                    foreach (var column in _columns)
                        column.Width = _defaultWidths[column];
                    ShowColumns();
                }
            )
        );
        return items;
    }

    /// <summary>
    /// What the songs are sorted by, the columns' widths and which are hidden, to keep between runs. Only widths
    /// changed by hand are kept, so the rest follow their defaults if those change. Set, anything it doesn't know (a
    /// column since gone) is passed over, and any column it doesn't mention is as it began.
    /// </summary>
    public BrowserLayout Layout
    {
        get =>
            new()
            {
                SortColumn = _sortColumn?.Id,
                SortDescending = _sortDescending,
                ColumnWidths = _columns
                    .Where(column => column.Resizable && column.Width != _defaultWidths[column])
                    .ToDictionary(column => column.Id, column => column.Width),
                HiddenColumns = _columns.Where(_hidden.Contains).Select(column => column.Id).ToList(),
            };
        set
        {
            _hidden.Clear();
            foreach (var column in _columns)
            {
                column.Width =
                    column.Resizable
                    && value.ColumnWidths.TryGetValue(column.Id, out var width)
                    && width > 0
                    && FormerDefaultWidths.GetValueOrDefault(column.Id) != width
                        ? MathF.Max(column.MinWidth, width)
                        : _defaultWidths[column];
                if (column != _titleColumn && value.HiddenColumns.Contains(column.Id))
                    _hidden.Add(column);
            }
            _sortColumn = _columns.FirstOrDefault(column => column.Id == value.SortColumn && column.SortKey is not null && !_hidden.Contains(column));
            _sortDescending = _sortColumn is not null && value.SortDescending;
            ShowColumns();
            Filter();
        }
    }

    // Text to sort by, none for empty, so songs without (an album, say) go last.
    static string? Text(string text) => text.Length > 0 ? text : null;

    /// <summary>
    /// Around the Camelot wheel: 1A, 1B, 2A, and so on.
    /// </summary>
    static IComparable? KeyOrder(Song song) =>
        song.Analysis is { } analysis ? analysis.Key.CamelotNumber * 2 + (analysis.Key.Camelot.EndsWith('B') ? 1 : 0) : null;

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
        var bulk = _bulkProgress.Visible ? _bulkProgress.Size.X + 4 + _search.Size.Y - 6 + 12 : 0;
        _heading.MaxWidth = MathF.Max(60, inner.X - _search.Size.X - _loadButtons.Sum(button => button.Size.X + 6) - 24 - _count.Size.X - bulk);
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
        _clearSearch.MinSize = new float2(_search.Size.Y - 6, _search.Size.Y - 6);
        _clearSearch.Measure();
        Place(_clearSearch, x - 6 - _clearSearch.Size.X - 3, y + 3);

        // The progress bar and its cross just before the search box, as tall as the cross.
        var square = _search.Size.Y - 6;
        _cancelBulk.MinSize = new float2(square, square);
        _cancelBulk.Measure();
        _bulkProgress.Height = square;
        _bulkProgress.Measure();
        var cancelLeft = x - 6 - _search.Size.X - 12 - square;
        Place(_cancelBulk, cancelLeft, y + 3);
        Place(_bulkProgress, cancelLeft - 4 - _bulkProgress.Size.X, y + 3);

        y += rowHeight + 6;
        Place(_header, left, y);
        y += _header.Size.Y;
        Place(_scroll, left, y);
        y += _scroll.Size.Y + 6;
        Place(_status, left, y);
    }

    static string Format(TimeSpan? time) => time is { } t ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : "";

    /// <summary>
    /// A total playing time: 42:15 under an hour, 3:42:15 under a day, then 2 days 3:42:15.
    /// </summary>
    internal static string FormatTotal(TimeSpan total) =>
        total.TotalHours < 1 ? $"{total.Minutes}:{total.Seconds:00}"
        : total.TotalDays < 1 ? $"{total.Hours}:{total.Minutes:00}:{total.Seconds:00}"
        : $"{total.Days} {(total.Days == 1 ? "day" : "days")} {total.Hours}:{total.Minutes:00}:{total.Seconds:00}";

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

/// <summary>
/// How far through an Analyse All run the song list is: a bar filling as songs are done, with how many over it.
/// </summary>
sealed class BulkProgress(Font font) : Element
{
    public float Width { get; set; } = 220;
    public float Height { get; set; } = 20;
    public int Done { get; set; }
    public int Total { get; set; }

    public override float2 Measure() => Size = new float2(Width, Height);

    public override void Draw(Batch batch)
    {
        batch.AddRect(ScreenPosition, Size, Theme.Well);
        var fraction = Total > 0 ? Math.Clamp((float)Done / Total, 0, 1) : 0;
        if (fraction > 0)
            batch.AddRect(ScreenPosition, new float2(MathF.Round(Size.X * fraction), Size.Y), Theme.Fade(Theme.Accent, 0.55f));
        batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);

        // The count centred, by its capitals, so it sits in the middle of the bar.
        var text = $"Analysing {Done} of {Total}";
        var x = MathF.Round(ScreenPosition.X + (Size.X - font.Measure(text).X) / 2);
        var y = MathF.Round(ScreenPosition.Y + Size.Y / 2 + BeatTrackers.CapHeight(font) / 2 - font.Ascender);
        batch.AddText(font, text, new float2(x, y), Theme.Text);
    }
}
