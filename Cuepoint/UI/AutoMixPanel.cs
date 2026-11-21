using Brutal.Numerics;
using Cuepoint.Music;

namespace Cuepoint.UI;

/// <summary>
/// One line of the Auto-Mix list: a song and where it is in the list.
/// </summary>
sealed record AutoMixRow(int Index, Song Song);

/// <summary>
/// The panel beside the song list for an auto-mix, or for Up next, a queue of songs to load by hand or mix: a tab down
/// its left edge that folds it away, a small toolbar saying how far through it is with a play/pause button, and the
/// songs in order, the one playing picked out and those played or loaded next dimmed. Songs dragged onto it from the
/// song list go on the end; those still to come can be dragged into another order, or taken out with Delete. It
/// raises events rather than doing anything itself. Call <see cref="Update"/> once a frame.
/// </summary>
sealed class AutoMixPanel : Panel
{
    /// <summary>
    /// How wide it is open, and how wide only its tab is.
    /// </summary>
    public const float OPEN_WIDTH = 344;

    public const float TAB_WIDTH = 20;

    const float PAD = 8;

    readonly Button _tab;
    readonly Label _name;
    readonly Label _track;
    readonly Label _time;
    readonly Button _play;
    readonly Button _close;
    readonly Dropdown<SongOrder?> _order;
    readonly ListHeader<AutoMixRow> _header;
    readonly ScrollPanel _scroll;
    readonly Label _status;
    readonly Image _playIcon;
    readonly Image _pauseIcon;
    readonly Image _foldIcon;
    readonly Image _unfoldIcon;
    readonly ListColumn<AutoMixRow> _titleColumn;
    IReadOnlyList<Song> _listed = [];
    string _listedName = "";
    int _shownIndex = -1;

    public ListView<AutoMixRow> List { get; }

    /// <summary>
    /// The mix it shows.
    /// </summary>
    public AutoMix? Mix { get; set; }

    public float2 FixedSize { get; set; } = new(OPEN_WIDTH, 400);

    /// <summary>
    /// Whether it's folded away to its tab.
    /// </summary>
    public bool Collapsed { get; set; }

    /// <summary>
    /// The play/pause button was pressed.
    /// </summary>
    public event Action? PlayPressed;

    /// <summary>
    /// The cross was pressed, to close it.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// An order was chosen from the Order list, to put the songs still to come in.
    /// </summary>
    public event Action<SongOrder>? OrderRequested;

    /// <summary>
    /// Where the Order list opens.
    /// </summary>
    public ContextMenu? Menu
    {
        get => _order.Menu;
        set => _order.Menu = value;
    }

    /// <summary>
    /// Songs were dropped on it from the song list, to go on the end.
    /// </summary>
    public event Action<IReadOnlyList<Song>>? SongsDropped;

    public AutoMixPanel(Font body, Font bold, Images images)
    {
        Background = Theme.Panel;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;
        (_playIcon, _pauseIcon, _foldIcon, _unfoldIcon) = (images.LargeTriangleRight, images.Pause, images.TriangleRight, images.TriangleLeft);

        Button Flat(string tooltip, Image icon) =>
            Add(
                new Button(body, "")
                {
                    Icon = icon,
                    Padding = 0,
                    Background = Colors.Transparent,
                    HoverBackground = Theme.Tint(0.1f),
                    PressedBackground = Theme.Tint(0.05f),
                    TextColor = Theme.Muted,
                    Tooltip = tooltip,
                }
            );

        _tab = Flat("Fold Auto-Mix away, or bring it back. It carries on either way", _foldIcon);
        _tab.Clicked += _ => Collapsed = !Collapsed;
        _name = Add(new Label(bold) { Color = Theme.Text });
        _track = Add(new Label(body) { Color = Theme.Text });
        _time = Add(new Label(body) { Color = Theme.Muted });
        _play = Flat("Play or pause the auto-mix", _playIcon);
        _play.TextColor = Theme.Accent;
        _play.Clicked += _ => PlayPressed?.Invoke();
        _close = Flat("Close Auto-Mix", images.Cross);
        _close.Clicked += _ => CloseRequested?.Invoke();
        _order = Add(
            new Dropdown<SongOrder?>(body)
            {
                Width = 116,
                Padding = 4,
                Items = () => Enum.GetValues<SongOrder>().Select(how => (SongOrder?)how).ToList(),
                Text = how => how is { } order ? MixOrder.Name(order) : "Order...",
                Tooltip =
                    "Put the songs still to come in another order: for mixing, each by the one that mixes best after it in key, tempo and energy, or by title, artist, tempo, key or energy, or shuffled",
            }
        );
        _order.Chosen += how =>
        {
            // It does what's chosen, then it's ready to choose again.
            _order.Selected = null;
            if (how is { } order)
                OrderRequested?.Invoke(order);
        };

        List = new ListView<AutoMixRow>(body)
        {
            EmptyText = "Drag songs here",
            AlternateRow = Theme.AlternateRow,
            CanReorder = true,
            DragLabel = row => row.Song.Title,
        };
        List.Reordered += (rows, before) => Mix?.Move(rows.Select(row => row.Index).ToList(), before);
        List.DeleteRequested += rows => Mix?.Remove(rows.Select(row => row.Index));
        float4? Pick(AutoMixRow row) =>
            Mix is not { } mix ? null
            : row.Index == mix.CurrentIndex && mix.IsRunning ? Theme.Accent
            : !mix.IsWaiting(row.Index) ? Theme.Muted
            : null;
        _titleColumn = new ListColumn<AutoMixRow>("Title", 160, row => row.Song.Title) { Color = Pick, Resizable = false };
        List.Columns.Add(new ListColumn<AutoMixRow>("#", 36, row => (row.Index + 1).ToString()) { AlignRight = true, Color = Pick, Resizable = false });
        List.Columns.Add(_titleColumn);
        List.Columns.Add(new ListColumn<AutoMixRow>("Length", 76, row => Length(row.Song)) { AlignRight = true, Color = Pick, Resizable = false });
        List.Columns.Add(new ListColumn<AutoMixRow>("BPM", 62, row => row.Song.Analysis is { } analysis ? $"{analysis.Bpm:F1}" : "") { AlignRight = true, Color = Pick, Resizable = false });
        _header = Add(new ListHeader<AutoMixRow>(List));
        _scroll = Add(new ScrollPanel());
        _scroll.Add(List);
        List.ScrollOwner = _scroll;
        _status = Add(new Label(body) { Color = Theme.Muted });
    }

    // Songs from the song list, not this list's own rows, which it reorders itself.
    public override bool CanDrop(object payload, float2 point) => payload is IReadOnlyList<Song> { Count: > 0 };

    public override void Drop(object payload, float2 point)
    {
        if (payload is IReadOnlyList<Song> { Count: > 0 } songs)
            SongsDropped?.Invoke(songs);
    }

    public override void Draw(Batch batch)
    {
        base.Draw(batch);
        if (IsDropTarget)
        {
            batch.AddRect(ScreenPosition, Size, Theme.Fade(Theme.Accent, 0.1f));
            batch.AddBorder(ScreenPosition, Size, 2, Theme.Accent);
        }
    }

    static string Length(Song song) => song.Duration is { } time ? $"{(int)time.TotalMinutes}:{time.Seconds:00}" : "";

    static string Clock(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    public void Update()
    {
        _tab.Icon = Collapsed ? _unfoldIcon : _foldIcon;
        if (Mix is not { } mix)
            return;
        if (mix.Songs != _listed)
        {
            // An edit keeps the place; another list starts at its top.
            _listed = mix.Songs;
            List.Items = mix.Songs.Select((song, i) => new AutoMixRow(i, song)).ToList();
            if (mix.Name != _listedName)
                _scroll.ScrollTo(float2.Zero);
            _listedName = mix.Name;
        }
        _name.Text = mix.Name;
        _track.Text = $"{(mix.CurrentIndex < 0 ? 0 : mix.CurrentIndex + 1)} / {mix.Songs.Count}";
        _time.Text = $"{Clock(mix.Elapsed)} / {Clock(mix.Total)}";
        _play.Icon = mix.IsRunning && !mix.IsPaused ? _pauseIcon : _playIcon;
        _status.Text = mix.IsPaused ? "Paused" : mix.Status;

        // The song playing is kept in view, as it changes.
        if (mix.CurrentIndex != _shownIndex)
        {
            _shownIndex = mix.CurrentIndex;
            if (_shownIndex >= 0)
                _scroll.ScrollTo(new float2(0, MathF.Max(0, (_shownIndex - 2) * List.RowHeight)));
        }
    }

    public override float2 Measure()
    {
        var inner = FixedSize - new float2(TAB_WIDTH + 2 * PAD, 2 * PAD);
        _titleColumn.Width = MathF.Max(80, inner.X - _scroll.ScrollbarHitWidth - List.Columns.Where(column => column != _titleColumn).Sum(column => column.Width));
        foreach (var child in Children)
        {
            if (child != _scroll)
                child.Measure();
        }
        _name.MaxWidth = inner.X - 28;
        _name.Measure();
        _status.MaxWidth = inner.X;
        _status.Measure();

        var toolbar = MathF.Max(_track.Size.Y, 24);
        var listHeight = MathF.Max(40, inner.Y - _name.Size.Y - 4 - toolbar - 6 - _header.Size.Y - 6 - _status.Size.Y);
        _scroll.MinSize = new float2(inner.X, listHeight);
        _scroll.MaxWidth = inner.X;
        _scroll.MaxHeight = listHeight;
        _scroll.Measure();
        return Size = FixedSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));

        _tab.MinSize = new float2(TAB_WIDTH, FixedSize.Y);
        _tab.Measure();
        Place(_tab, position.X, position.Y);

        var left = position.X + TAB_WIDTH + PAD;
        var right = position.X + FixedSize.X - PAD;
        var y = position.Y + PAD;

        _close.MinSize = new float2(20, 20);
        _close.Measure();
        Place(_close, right - 20, y);
        Place(_name, left, y + (20 - _name.Size.Y) / 2);
        y += MathF.Max(_name.Size.Y, 20) + 4;

        const float BUTTON = 24;
        _play.MinSize = new float2(BUTTON, BUTTON);
        _play.Measure();
        Place(_track, left, y + (BUTTON - _track.Size.Y) / 2);
        Place(_time, left + MathF.Max(64, _track.Size.X + 14), y + (BUTTON - _time.Size.Y) / 2);
        Place(_play, right - BUTTON, y);
        _order.Measure();
        Place(_order, right - BUTTON - 8 - _order.Size.X, y + (BUTTON - _order.Size.Y) / 2);
        y += BUTTON + 6;

        Place(_header, left, y);
        y += _header.Size.Y;
        Place(_scroll, left, y);
        y += _scroll.Size.Y + 6;
        Place(_status, left, y);
    }
}
