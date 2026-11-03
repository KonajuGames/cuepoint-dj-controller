using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// The fonts the screen is drawn in.
/// </summary>
/// <param name="Large">The folder list's and song list's text when it's made large, to read from further away.</param>
sealed record Fonts(Font Small, Font Body, Font Bold, Font Heading, Font Big, Font Large);

/// <summary>
/// The whole window: the title bar across the top; each deck's waveform scrolling past beneath it; the decks either
/// side of the mixer; and filling the rest, the folder list beside the songs in whatever's chosen in it. The
/// options dialog opens over the lot. Everything is sized to the window each frame, so set
/// <see cref="ScreenSize"/> and call <see cref="Update"/> before laying out.
/// </summary>
sealed class MainScreen : Panel
{
    const float GAP = 6;
    const float WAVEFORM_HEIGHT = 66;
    const float TALL_WAVEFORM_HEIGHT = 110;

    float WaveformHeight => Options.TallWaveforms.Checked ? TALL_WAVEFORM_HEIGHT : WAVEFORM_HEIGHT;
    const float FOLDERS_WIDTH = 270;
    const float LARGE_FOLDERS_WIDTH = 340;

    readonly List<ScrollingWaveform> _waveforms = [];
    readonly List<GridEditor?> _gridEditors = [];

    /// <summary>
    /// How much a deck's scrolling waveform shows while its grid is being fixed, in seconds.
    /// </summary>
    const float GRID_EDIT_SECONDS = 3;
    readonly List<DeckPanel> _decks = [];
    readonly MixerPanel _mixer;
    readonly Panel _folders;
    readonly ScrollPanel _folderScroll;
    readonly Fonts _fonts;
    readonly Images _images;
    bool _largeListText;

    public float2 ScreenSize { get; set; } = new(1280, 720);

    public TitleBar TitleBar { get; }
    public TreeView<LibraryNode> FolderTree { get; }
    public SongBrowser Browser { get; }
    public OptionsDialog Options { get; }

    /// <summary>
    /// Whether the folder list and song list are in large text, for reading in the dark or without glasses. The
    /// folder list widens to fit.
    /// </summary>
    public bool LargeListText
    {
        get => _largeListText;
        set
        {
            _largeListText = value;
            FolderTree.Font = Browser.List.Font = value ? _fonts.Large : _fonts.Body;
            // The folder list's arrows and icons grow with its text, and the room they're given, and the indent, too.
            FolderTree.Arrows = value ? (_images.LargeTriangleRight, _images.LargeTriangleDown) : (_images.TriangleRight, _images.TriangleDown);
            FolderTree.Cell = value ? 24 : 17;
            FolderTree.Indent = value ? 24 : 18;
        }
    }

    float FoldersWidth => _largeListText ? LARGE_FOLDERS_WIDTH : FOLDERS_WIDTH;

    /// <summary>
    /// A song was dragged from the list onto a deck, to load there.
    /// </summary>
    public event Action<Deck, Song>? SongDropped;

    public MainScreen(
        AudioEngine engine,
        IReadOnlyList<Deck> decks,
        MusicLibrary library,
        LibraryAnalyser analyser,
        IReadOnlyList<LibraryNode> folders,
        string databasePath,
        Fonts fonts,
        Images images,
        Func<int, IReadOnlyList<string>>? hotcueKeys = null,
        Func<int, DeckKeyHints?>? deckKeys = null,
        Func<Deck, GridEditor?>? gridEditors = null,
        Func<Deck, RollPads?>? rollPads = null,
        Func<Deck, JumpPads?>? jumpPads = null
    )
    {
        Background = Theme.Background;
        _fonts = fonts;
        _images = images;

        TitleBar = Add(new TitleBar(engine, decks, fonts.Small, fonts.Body, fonts.Bold, images));

        foreach (var deck in decks)
        {
            var editor = gridEditors?.Invoke(deck);
            var waveform = Add(new ScrollingWaveform(deck, Theme.DeckColor(deck.Name), fonts.Small) { Editor = editor });
            _waveforms.Add(waveform);
            _gridEditors.Add(editor);
        }

        // Left to right: the first half of the decks, the mixer, the rest.
        // Decks right of the mixer are laid out as reflections, so each deck's mixing controls sit nearest the mixer.
        var deckPanels = decks
            .Select((deck, i) => new DeckPanel(deck, fonts.Small, fonts.Body, fonts.Bold, fonts.Big, hotcueKeys?.Invoke(i), deckKeys?.Invoke(i), gridEditors?.Invoke(deck), images, rollPads?.Invoke(deck), jumpPads?.Invoke(deck))
            {
                Mirrored = i >= (decks.Count + 1) / 2,
            })
            .ToList();
        _mixer = new MixerPanel(engine, decks, fonts.Small, fonts.Bold, images, decks.Select((_, i) => deckKeys?.Invoke(i)?.Pfl).ToList());
        for (var i = 0; i < deckPanels.Count; i++)
        {
            if (i == (deckPanels.Count + 1) / 2)
                Add(_mixer);
            _decks.Add(Add(deckPanels[i]));
            deckPanels[i].SongDropped += (panel, song) => SongDropped?.Invoke(panel.Deck, song);
        }
        if (deckPanels.Count < 2)
            Add(_mixer);

        _folders = Add(new Panel { Background = Theme.Panel, BorderColor = Theme.PanelBorder, BorderWidth = 1, Padding = 6 });
        _folderScroll = _folders.Add(new ScrollPanel());
        FolderTree = _folderScroll.Add(
            new TreeView<LibraryNode>(fonts.Body)
            {
                Text = node => node.Name,
                Children = node => node.ChildrenInBackground,
                CanExpand = node => node.CanExpand,
                Icon = node => IconFor(node, _largeListText ? images.LargeFolders : images.Folders),
                Arrows = (images.TriangleRight, images.TriangleDown),
                Roots = folders,
                ScrollOwner = _folderScroll,
                TextColor = Theme.Text,
            }
        );
        FolderTree.ScrollOwner = _folderScroll;

        Browser = Add(new SongBrowser(library, analyser, decks.Select(deck => deck.Name), fonts.Body, fonts.Bold, images));
        Browser.DecksOf = song =>
        {
            List<string>? on = null;
            foreach (var deck in decks)
            {
                if (deck.IsLoaded && string.Equals(deck.FilePath, song.Path, StringComparison.OrdinalIgnoreCase))
                    (on ??= []).Add(deck.Name);
            }
            return on ?? (IReadOnlyList<string>)[];
        };
        FolderTree.SelectionChanged += node =>
        {
            if (node.HasSongs)
                Browser.Source = node;
        };

        Options = Add(new OptionsDialog(engine, library.Root, databasePath, fonts.Body, fonts.Bold, fonts.Heading));

        // The wheel over a waveform zooms both together, as the options' slider does: up shows less of the song, closer.
        foreach (var waveform in _waveforms)
            waveform.Zoomed += notches => Options.WaveformSeconds.Value *= MathF.Pow(0.85f, notches);
    }

    /// <summary>
    /// Puts <paramref name="message"/> on the status line, with the time.
    /// </summary>
    public Deck? DeckAt(float2 point) => _decks.FirstOrDefault(panel => panel.Visible && panel.Contains(point))?.Deck;

    public void Log(string message) => Browser.Status = $"{DateTime.Now:HH:mm:ss}  {message}";

    public void Update()
    {
        TitleBar.Update();
        foreach (var deck in _decks)
            deck.Refresh();
        _mixer.Refresh();
        Browser.Update();
    }

    /// <summary>
    /// A node's icon in the folder list, in a colour for its kind: the headings over the drives, virtual folders,
    /// lists and filters have their kind's icon, dimmer.
    /// </summary>
    /// <summary>
    /// The node at the top of the folder list that <paramref name="node"/> is under, or is.
    /// </summary>
    static LibraryNode Top(LibraryNode node)
    {
        while (node.Parent is { } parent)
            node = parent;
        return node;
    }

    static (Image, float4)? IconFor(LibraryNode node, FolderIcons icons) =>
        node.Kind switch
        {
            LibraryNodeKind.Library => (icons.Library, Theme.Accent),
            LibraryNodeKind.Drive => (icons.Drive, Colors.Hex(0x8FA3BF)),
            LibraryNodeKind.Folder => (icons.Folder, Colors.Hex(0xD9B45A)),
            LibraryNodeKind.List => (node.Name == "History" ? icons.History : icons.List, Colors.Hex(0x5AC8D9)),
            LibraryNodeKind.VirtualFolders => (icons.Playlist, Theme.Muted),
            LibraryNodeKind.VirtualFolder => (icons.Playlist, Colors.Hex(0x6CCB7A)),
            LibraryNodeKind.Filter => (icons.Filter, Colors.Hex(0xB57BE0)),
            LibraryNodeKind.Imported => (icons.Playlist, Colors.Hex(0xE0A35A)),
            LibraryNodeKind.Group => node.Name switch
            {
                "Local drives" => (icons.Drive, Theme.Muted),
                "Lists" => (icons.List, Theme.Muted),
                // Other DJ software's, and the collections and folders in them.
                _ when Top(node).Name is "Rekordbox" or "Serato" => (icons.Playlist, Theme.Muted),
                _ => (icons.Filter, Theme.Muted),
            },
            _ => null,
        };

    public override float2 Measure()
    {
        var width = ScreenSize.X;
        TitleBar.Width = width;

        var waveformWidth = width - 2 * GAP;
        for (var i = 0; i < _waveforms.Count; i++)
        {
            // Fixing a deck's grid, its waveform zooms right in, to put beats on hits to the millisecond.
            var waveform = _waveforms[i];
            waveform.FixedSize = new float2(waveformWidth, WaveformHeight);
            waveform.Colours = Options.WaveformColours.Selected;
            waveform.ShowBeatGrid = Options.WaveformBeatGrid.Checked;
            waveform.PixelsPerSecond = waveformWidth / (_gridEditors[i] is { Active: true } ? GRID_EDIT_SECONDS : Options.WaveformSeconds.Value);
        }

        var deckWidth = (width - 2 * GAP - MixerPanel.WIDTH - GAP * (_decks.Count)) / Math.Max(1, _decks.Count);
        foreach (var deck in _decks)
        {
            deck.Width = MathF.Floor(deckWidth);
            deck.WaveformColours = Options.WaveformColours.Selected;
        }
        _mixer.Height = DecksHeight;

        var browserTop = BrowserTop;
        var browserHeight = MathF.Max(120, ScreenSize.Y - browserTop - GAP);
        var folderInner = new float2(FoldersWidth - 2 * _folders.Padding - 2, browserHeight - 2 * _folders.Padding - 2);
        _folderScroll.MinSize = folderInner;
        _folderScroll.MaxWidth = folderInner.X;
        _folderScroll.MaxHeight = folderInner.Y;
        FolderTree.MinWidth = folderInner.X - _folderScroll.ScrollbarHitWidth;
        Browser.FixedSize = new float2(width - FoldersWidth - 3 * GAP, browserHeight);
        Options.ScreenSize = ScreenSize;

        foreach (var child in Children)
        {
            if (child.Visible)
                child.Measure();
        }
        return Size = ScreenSize;
    }

    float WaveformsTop => TitleBar.HEIGHT + GAP;

    float DecksTop => WaveformsTop + _waveforms.Count * (WaveformHeight + 2) - 2 + GAP;

    /// <summary>
    /// As tall as the tallest deck, which is taller with its buttons in two rows on a narrow window.
    /// </summary>
    float DecksHeight => _decks.Count == 0 ? DeckPanel.HEIGHT : _decks.Max(deck => deck.Height);

    float BrowserTop => DecksTop + DecksHeight + GAP;

    /// <summary>
    /// Where the decks' pad mode lists open.
    /// </summary>
    public ContextMenu? Menu
    {
        set
        {
            foreach (var deck in _decks)
                deck.Menu = value;
        }
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        TitleBar.Arrange(position);

        var y = position.Y + WaveformsTop;
        foreach (var waveform in _waveforms)
        {
            waveform.Arrange(new float2(position.X + GAP, y));
            y += WaveformHeight + 2;
        }

        // The decks and mixer in the order they were added, left to right.
        var x = position.X + GAP;
        foreach (var child in Children)
        {
            if (child is DeckPanel or MixerPanel)
            {
                child.Arrange(new float2(x, position.Y + DecksTop));
                x += child.Size.X + GAP;
            }
        }

        _folders.Arrange(new float2(position.X + GAP, position.Y + BrowserTop));
        Browser.Arrange(new float2(position.X + GAP + FoldersWidth + GAP, position.Y + BrowserTop));
        if (Options.Visible)
            Options.Arrange(position);
    }
}
