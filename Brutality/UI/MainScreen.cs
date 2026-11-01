using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// The fonts the screen is drawn in.
/// </summary>
sealed record Fonts(Font Small, Font Body, Font Bold, Font Heading, Font Big);

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
    const float FOLDERS_WIDTH = 270;

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

    public float2 ScreenSize { get; set; } = new(1280, 720);

    public TitleBar TitleBar { get; }
    public TreeView<LibraryNode> FolderTree { get; }
    public SongBrowser Browser { get; }
    public OptionsDialog Options { get; }

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
        Func<Deck, RollPads?>? rollPads = null
    )
    {
        Background = Theme.Background;

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
            .Select((deck, i) => new DeckPanel(deck, fonts.Small, fonts.Body, fonts.Bold, fonts.Big, hotcueKeys?.Invoke(i), deckKeys?.Invoke(i), gridEditors?.Invoke(deck), images, rollPads?.Invoke(deck))
            {
                Mirrored = i >= (decks.Count + 1) / 2,
            })
            .ToList();
        _mixer = new MixerPanel(engine, decks, fonts.Small, fonts.Bold, decks.Select((_, i) => deckKeys?.Invoke(i)?.Pfl).ToList());
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
                Children = node => node.Children,
                CanExpand = node => node.CanExpand,
                IconColor = IconFor,
                Arrows = (images.TriangleRight, images.TriangleDown),
                Roots = folders,
                ScrollOwner = _folderScroll,
                TextColor = Theme.Text,
            }
        );
        FolderTree.ScrollOwner = _folderScroll;

        Browser = Add(new SongBrowser(library, analyser, decks.Select(deck => deck.Name), fonts.Body, fonts.Bold, images));
        FolderTree.SelectionChanged += node =>
        {
            if (node.HasSongs)
                Browser.Source = node;
        };

        Options = Add(new OptionsDialog(engine, library.Root, databasePath, fonts.Body, fonts.Bold, fonts.Heading));
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

    static float4? IconFor(LibraryNode node) =>
        node.Kind switch
        {
            LibraryNodeKind.Library => Theme.Accent,
            LibraryNodeKind.Drive => Colors.Hex(0x8FA3BF),
            LibraryNodeKind.Folder => Colors.Hex(0xD9B45A),
            LibraryNodeKind.List => Colors.Hex(0x5AC8D9),
            LibraryNodeKind.VirtualFolder => Colors.Hex(0x6CCB7A),
            LibraryNodeKind.Filter => Colors.Hex(0xB57BE0),
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
            waveform.FixedSize = new float2(waveformWidth, WAVEFORM_HEIGHT);
            waveform.PixelsPerSecond = waveformWidth / (_gridEditors[i] is { Active: true } ? GRID_EDIT_SECONDS : Options.WaveformSeconds.Value);
        }

        var deckWidth = (width - 2 * GAP - MixerPanel.WIDTH - GAP * (_decks.Count)) / Math.Max(1, _decks.Count);
        foreach (var deck in _decks)
            deck.Width = MathF.Floor(deckWidth);

        var browserTop = BrowserTop;
        var browserHeight = MathF.Max(120, ScreenSize.Y - browserTop - GAP);
        var folderInner = new float2(FOLDERS_WIDTH - 2 * _folders.Padding - 2, browserHeight - 2 * _folders.Padding - 2);
        _folderScroll.MinSize = folderInner;
        _folderScroll.MaxWidth = folderInner.X;
        _folderScroll.MaxHeight = folderInner.Y;
        FolderTree.MinWidth = folderInner.X - _folderScroll.ScrollbarHitWidth;
        Browser.FixedSize = new float2(width - FOLDERS_WIDTH - 3 * GAP, browserHeight);
        Options.ScreenSize = ScreenSize;

        foreach (var child in Children)
        {
            if (child.Visible)
                child.Measure();
        }
        return Size = ScreenSize;
    }

    float WaveformsTop => TitleBar.HEIGHT + GAP;

    float DecksTop => WaveformsTop + _waveforms.Count * (WAVEFORM_HEIGHT + 2) - 2 + GAP;

    float BrowserTop => DecksTop + DeckPanel.HEIGHT + GAP;

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        TitleBar.Arrange(position);

        var y = position.Y + WaveformsTop;
        foreach (var waveform in _waveforms)
        {
            waveform.Arrange(new float2(position.X + GAP, y));
            y += WAVEFORM_HEIGHT + 2;
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
        Browser.Arrange(new float2(position.X + GAP + FOLDERS_WIDTH + GAP, position.Y + BrowserTop));
        if (Options.Visible)
            Options.Arrange(position);
    }
}
