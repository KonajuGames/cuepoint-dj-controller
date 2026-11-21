using Brutal.Numerics;
using Cuepoint.Audio;
using Cuepoint.Music;

namespace Cuepoint.UI;

/// <summary>
/// The fonts the screen is drawn in.
/// </summary>
/// <param name="Large">The folder list's and song list's text when it's made large, to read from further away.</param>
/// <param name="SmallBold">The deck letters on the scrolling waveforms.</param>
/// <param name="Badge">The deck letters in their coloured squares: as heavy as the system has.</param>
sealed record Fonts(Font Small, Font Body, Font Bold, Font Heading, Font Big, Font Large, Font SmallBold, Font Badge);

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
    /// How well the first two decks' beats line up, between their waveforms; null with fewer than two decks.
    /// </summary>
    readonly PhaseMeter? _phaseMeter;

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

    /// <summary>
    /// Cuepoint's own widgets in the title bar: the decks' beats, the master level, recording, OS2L and headphones.
    /// </summary>
    public TitleBarItems TitleItems { get; }
    public TreeView<LibraryNode> FolderTree { get; }
    public SongBrowser Browser { get; }

    /// <summary>
    /// The panel that slides in to the right of the song list for an auto-mix.
    /// </summary>
    public AutoMixPanel AutoMix { get; }

    // Whether it's open, and how much width it's taking now, as it slides.
    bool _autoMixOpen;
    float _autoMixWidth;
    long _lastSlide = Environment.TickCount64;

    /// <summary>
    /// How quickly the panel slides: how much of the way it goes in a second, as an exponent.
    /// </summary>
    const float SLIDE_RATE = 16;
    public OptionsDialog Options { get; }

    /// <summary>
    /// Every keyboard shortcut, opened with ? or from the options.
    /// </summary>
    public ShortcutsDialog Shortcuts { get; }

    /// <summary>
    /// The keys to change, opened from the shortcuts or the options.
    /// </summary>
    public KeysDialog Keys { get; }

    readonly Func<int, IReadOnlyList<string>>? _hotcueKeys;
    readonly Func<int, DeckKeyHints?>? _deckKeys;

    /// <summary>
    /// Labels the decks' and mixer's buttons with their keys again, after they've been changed.
    /// </summary>
    public void RefreshKeys()
    {
        for (var i = 0; i < _decks.Count; i++)
            _decks[i].SetKeys(_deckKeys?.Invoke(i), _hotcueKeys?.Invoke(i));
        _mixer.SetPflKeys(_decks.Select((_, i) => _deckKeys?.Invoke(i)?.Pfl).ToList());
    }

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
            FolderTree.Font = Browser.List.Font = AutoMix.List.Font = value ? _fonts.Large : _fonts.Body;
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
        Func<Deck, JumpPads?>? jumpPads = null,
        Func<Deck, SamplerPads?>? samplerPads = null
    )
    {
        Background = Theme.Background;
        _fonts = fonts;
        _images = images;

        // The wordmark in the two decks' colours, as the logo is: "Cue" in A's blue, "point" in B's red.
        TitleBar = Add(new TitleBar(fonts.Body, fonts.Bold, images.Logo, images.Cog, ("Cue", AppTheme.DeckA), ("point", AppTheme.DeckB)));
        TitleItems = new TitleBarItems(TitleBar, engine, decks, fonts.Small, fonts.Body, fonts.Bold, images);

        foreach (var deck in decks)
        {
            var editor = gridEditors?.Invoke(deck);
            var waveform = Add(new ScrollingWaveform(deck, AppTheme.DeckColor(deck.Name), fonts.Small, fonts.SmallBold) { Editor = editor });
            _waveforms.Add(waveform);
            _gridEditors.Add(editor);
        }
        if (decks.Count >= 2)
            _phaseMeter = Add(new PhaseMeter(decks[0], decks[1], fonts.Small));

        // Left to right: the first half of the decks, the mixer, the rest.
        // Decks right of the mixer are laid out as reflections, so each deck's mixing controls sit nearest the mixer.
        var deckPanels = decks
            .Select((deck, i) => new DeckPanel(deck, fonts.Small, fonts.Body, fonts.Bold, fonts.Big, fonts.Badge, hotcueKeys?.Invoke(i), deckKeys?.Invoke(i), gridEditors?.Invoke(deck), images, rollPads?.Invoke(deck), jumpPads?.Invoke(deck), samplerPads?.Invoke(deck))
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

        AutoMix = Add(new AutoMixPanel(fonts.Body, fonts.Bold, images) { Visible = false });

        Options = Add(new OptionsDialog(engine, library.Root, databasePath, fonts.Body, fonts.Bold, fonts.Heading));
        Shortcuts = Add(new ShortcutsDialog(fonts.Body, fonts.Bold, fonts.Heading));
        Keys = Add(new KeysDialog(fonts.Body, fonts.Bold, fonts.Heading));
        (_hotcueKeys, _deckKeys) = (hotcueKeys, deckKeys);
        Shortcuts.ChangeKeysClicked += () =>
        {
            Shortcuts.Close();
            Keys.Open();
        };
        Options.ChangeKeysClicked += () =>
        {
            Options.Close();
            Keys.Open();
        };
        Options.ShortcutsClicked += () =>
        {
            Options.Close();
            Shortcuts.Toggle();
        };

        // The wheel over a waveform zooms both together, as the options' slider does: up shows less of the song, closer.
        foreach (var waveform in _waveforms)
            waveform.Zoomed += notches => Options.WaveformSeconds.Value *= MathF.Pow(0.85f, notches);
    }

    /// <summary>
    /// Puts <paramref name="message"/> on the status line, with the time.
    /// </summary>
    public Deck? DeckAt(float2 point) => _decks.FirstOrDefault(panel => panel.Visible && panel.Contains(point))?.Deck;

    /// <summary>
    /// Shows <paramref name="cover"/> on <paramref name="deck"/>'s panel (see <see cref="DeckPanel.Cover"/>).
    /// </summary>
    public void ShowCover(Deck deck, (int Texture, int Width, int Height)? cover)
    {
        foreach (var panel in _decks)
            if (panel.Deck == deck)
                panel.Cover = cover;
    }

    public void Log(string message) => Browser.Status = $"{DateTime.Now:HH:mm:ss}  {message}";

    /// <summary>
    /// Slides the Auto-Mix panel in, opened out.
    /// </summary>
    public void ShowAutoMix()
    {
        _autoMixOpen = AutoMix.Visible = true;
        AutoMix.Collapsed = false;
    }

    /// <summary>
    /// Slides the Auto-Mix panel away.
    /// </summary>
    public void HideAutoMix() => _autoMixOpen = false;

    void SlideAutoMix()
    {
        var now = Environment.TickCount64;
        var seconds = (now - _lastSlide) / 1000f;
        _lastSlide = now;
        var target = !_autoMixOpen ? 0 : AutoMix.Collapsed ? AutoMixPanel.TAB_WIDTH : AutoMixPanel.OPEN_WIDTH;
        _autoMixWidth += (target - _autoMixWidth) * (1 - MathF.Exp(-SLIDE_RATE * seconds));
        if (MathF.Abs(target - _autoMixWidth) < 1)
            _autoMixWidth = target;
        if (_autoMixWidth == 0 && !_autoMixOpen)
            AutoMix.Visible = false;
    }

    public void Update()
    {
        SlideAutoMix();
        AutoMix.Update();
        TitleBar.Update();
        TitleItems.Update();
        _phaseMeter?.Update();
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
            LibraryNodeKind.Drive => (icons.Drive, AppTheme.OnPage(Colors.Hex(0x8FA3BF))),
            LibraryNodeKind.Folder => (icons.Folder, AppTheme.OnPage(Colors.Hex(0xD9B45A))),
            LibraryNodeKind.List => (node.Name == "History" ? icons.History : icons.List, AppTheme.OnPage(Colors.Hex(0x5AC8D9))),
            LibraryNodeKind.VirtualFolders => (icons.Playlist, Theme.Muted),
            LibraryNodeKind.VirtualFolder => (icons.Playlist, AppTheme.OnPage(Colors.Hex(0x6CCB7A))),
            LibraryNodeKind.Filter => (icons.Filter, AppTheme.OnPage(Colors.Hex(0xB57BE0))),
            LibraryNodeKind.SmartPlaylists => (icons.Filter, Theme.Muted),
            LibraryNodeKind.Imported => (icons.Playlist, AppTheme.OnPage(Colors.Hex(0xE0A35A))),
            LibraryNodeKind.PlayedSet => (icons.History, AppTheme.OnPage(Colors.Hex(0x5AC8D9))),
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
        if (_phaseMeter is not null)
            _phaseMeter.Width = waveformWidth;

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
        Browser.FixedSize = new float2(width - FoldersWidth - 3 * GAP - (_autoMixWidth > 0 ? _autoMixWidth + GAP : 0), browserHeight);
        AutoMix.FixedSize = new float2(AutoMixPanel.OPEN_WIDTH, browserHeight);
        Options.ScreenSize = ScreenSize;
        Shortcuts.ScreenSize = ScreenSize;
        Keys.ScreenSize = ScreenSize;

        foreach (var child in Children)
        {
            if (child.Visible)
                child.Measure();
        }
        return Size = ScreenSize;
    }

    float WaveformsTop => TitleBar.HEIGHT + GAP;

    float DecksTop => WaveformsTop + _waveforms.Count * (WaveformHeight + 2) - 2 + PhaseMeterSpace + GAP;

    /// <summary>
    /// The room the phase meter takes between the first two waveforms, with a gap below it.
    /// </summary>
    float PhaseMeterSpace => _phaseMeter is null ? 0 : PhaseMeter.HEIGHT + 2;

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
            AutoMix.Menu = value;
        }
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        TitleBar.Arrange(position);

        var y = position.Y + WaveformsTop;
        for (var i = 0; i < _waveforms.Count; i++)
        {
            _waveforms[i].Arrange(new float2(position.X + GAP, y));
            y += WaveformHeight + 2;
            if (i == 0 && _phaseMeter is not null)
            {
                _phaseMeter.Arrange(new float2(position.X + GAP, y));
                y += PhaseMeterSpace;
            }
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

        // Hanging off the right of the window for as much of it as isn't slid in yet.
        if (AutoMix.Visible)
            AutoMix.Arrange(new float2(MathF.Round(position.X + ScreenSize.X - GAP - _autoMixWidth), position.Y + BrowserTop));
        if (Options.Visible)
            Options.Arrange(position);
        if (Shortcuts.Visible)
            Shortcuts.Arrange(position);
        if (Keys.Visible)
            Keys.Arrange(position);
    }
}
