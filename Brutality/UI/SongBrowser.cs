using Brutal.Numerics;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// The music library as a searchable list, with a "Load to" button for each deck. It raises events rather than
/// loading anything itself; call <see cref="Update"/> each frame to bring in tags as the library reads them. With an
/// analyser, songs are analysed as they come into view, each row filling like a progress bar meanwhile, and their
/// tempo and key appear when done.
/// </summary>
sealed class SongBrowser : Panel
{
    readonly MusicLibrary _library;
    readonly LibraryAnalyser? _analyser;
    readonly Label _status;
    readonly TextBox _search;
    readonly ScrollPanel _scroll;
    readonly List<Button> _loadButtons = [];

    public ListView<Song> List { get; }

    /// <summary>
    /// A deck's "Load to" button was pressed: the song, and the deck's name.
    /// </summary>
    public event Action<Song, string>? LoadRequested;

    /// <summary>
    /// A song was double-clicked or Enter was pressed on it, leaving the choice of deck to the app.
    /// </summary>
    public event Action<Song>? SongActivated;

    public SongBrowser(
        MusicLibrary library,
        LibraryAnalyser? analyser,
        IEnumerable<string> deckNames,
        Font body,
        Font heading,
        float4 muted,
        float4 accent,
        float listHeight
    )
    {
        _library = library;
        _analyser = analyser;
        Spacing = 6;

        var top = Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
        top.Add(new Label(heading, "Library"));
        _status = top.Add(new Label(body) { Color = muted });

        List = new ListView<Song>(body)
        {
            EmptyText = "No songs match",
            RowProgress = analyser is null ? null : analyser.ProgressOf,
            ProgressColor = accent * new float4(1, 1, 1, 0.3f),
        };
        List.Columns.Add(new ListColumn<Song>("Title", 242, song => song.Title));
        List.Columns.Add(new ListColumn<Song>("Artist", 170, song => song.Artist));
        List.Columns.Add(new ListColumn<Song>("Length", 64, song => Format(song.Duration)) { AlignRight = true });
        List.Columns.Add(new ListColumn<Song>("BPM", 64, FormatBpm) { AlignRight = true });
        List.Columns.Add(new ListColumn<Song>("Key", 84, FormatKey) { AlignRight = true });

        _search = Add(
            new TextBox(body)
            {
                Width = List.ContentWidth + 6, // Lines up with the list, which has a scrollbar beside it.
                Placeholder = "Search title, artist or album",
                FocusColor = accent,
            }
        );
        _search.TextChanged += _ => Filter();

        Add(new ListHeader<Song>(List));
        _scroll = Add(new ScrollPanel { MaxHeight = listHeight, MinSize = new float2(0, listHeight) });
        _scroll.Add(List);
        List.ScrollOwner = _scroll;
        List.ItemActivated += song => SongActivated?.Invoke(song);
        List.SelectionChanged += _ => UpdateButtons();

        var buttons = Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        foreach (var name in deckNames)
        {
            var button = buttons.Add(new Button(body, $"Load to {name}") { FocusColor = accent });
            button.Clicked += _ =>
            {
                if (List.SelectedItem is { } song)
                    LoadRequested?.Invoke(song, name);
            };
            _loadButtons.Add(button);
        }

        Filter();
        UpdateButtons();
    }

    public Song? SelectedSong => List.SelectedItem;

    public void Update()
    {
        // New tags can change what matches the search, so refilter while they're arriving and a search is on.
        if (_library.ApplyReadTags() > 0 && _search.Text.Length > 0)
            Filter();

        // Songs in view (as last drawn) are analysed first, from the top down.
        if (_analyser is not null)
        {
            var (first, count) = List.VisibleRows.GetOffsetAndLength(List.Items.Count);
            if (count > 0)
                _analyser.Request(List.Items.Skip(first).Take(count).ToList());
        }

        var songs =
            _library.Error is { } error ? $"Couldn't read {_library.Root}: {error}"
            : _library.IsReadingTags ? $"{_library.Songs.Count} songs, reading tags {_library.TaggedCount}/{_library.Songs.Count}"
            : List.Items.Count == _library.Songs.Count ? $"{_library.Songs.Count} songs"
            : $"{List.Items.Count} of {_library.Songs.Count} songs";
        var analysing =
            _analyser is null || _analyser.ActiveCount == 0 ? ""
            : _analyser.QueuedCount == 0 ? $", analysing {_analyser.ActiveCount}"
            : $", analysing {_analyser.ActiveCount}, {_analyser.QueuedCount} waiting";
        _status.Text = songs + analysing;
    }

    void Filter()
    {
        var query = _search.Text.Trim();
        List.Items = query.Length == 0 ? _library.Songs : _library.Songs.Where(song => song.Matches(query)).ToList();
    }

    void UpdateButtons()
    {
        foreach (var button in _loadButtons)
            button.Enabled = List.SelectedItem is not null;
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
}
