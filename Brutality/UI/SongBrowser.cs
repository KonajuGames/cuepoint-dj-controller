using Brutal.Numerics;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// The music library as a searchable list, with a "Load to" button for each deck. It raises events rather than
/// loading anything itself; call <see cref="Update"/> each frame to bring in tags as the library reads them.
/// </summary>
sealed class SongBrowser : Panel
{
    readonly MusicLibrary _library;
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
        IEnumerable<string> deckNames,
        Font body,
        Font heading,
        float4 muted,
        float4 accent,
        float listHeight
    )
    {
        _library = library;
        Spacing = 6;

        var top = Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
        top.Add(new Label(heading, "Library"));
        _status = top.Add(new Label(body) { Color = muted });

        List = new ListView<Song>(body) { EmptyText = "No songs match" };
        List.Columns.Add(new ListColumn<Song>("Title", 300, song => song.Title));
        List.Columns.Add(new ListColumn<Song>("Artist", 200, song => song.Artist));
        List.Columns.Add(new ListColumn<Song>("Length", 70, song => Format(song.Duration)) { AlignRight = true });

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

        _status.Text =
            _library.Error is { } error ? $"Couldn't read {_library.Root}: {error}"
            : _library.IsReadingTags ? $"{_library.Songs.Count} songs, reading tags {_library.TaggedCount}/{_library.Songs.Count}"
            : List.Items.Count == _library.Songs.Count ? $"{_library.Songs.Count} songs"
            : $"{List.Items.Count} of {_library.Songs.Count} songs";
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
}
