using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// The on-screen overlay: a stats panel in the top-left, the song browser beside it, a scrolling event log with a note
/// box in the top-right, the decks in the bottom-left and a controls hint in the bottom-right.
/// </summary>
sealed class Hud
{
    static readonly float4 PanelBackground = Colors.Hex(0x15151C, 0.82f);
    static readonly float4 PanelBorder = Colors.Hex(0xEB7828, 0.9f);
    static readonly float4 Muted = Colors.Hex(0x9A9AA8);
    static readonly float4 Accent = Colors.Hex(0xEB7828);

    public Root Ui { get; } = new();

    readonly Label _resolution;
    readonly Label _frameRate;
    readonly ScrollPanel _log;
    readonly Font _body;
    readonly Font _title;
    readonly List<DeckPanel> _decks = [];
    Panel? _deckRow;
    SongBrowser? _browser;
    int2 _lastFramebufferSize;

    double _sampleStart;
    int _framesSinceSample;

    public Hud(Font body, Font title, string deviceName)
    {
        _body = body;
        _title = title;

        var stats = Ui.Add(
            new Panel
            {
                Position = new float2(16, 16),
                Padding = 12,
                Spacing = 4,
                Background = PanelBackground,
                BorderColor = PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(260, 0),
            }
        );
        stats.Add(new Label(title, "System"));
        stats.Add(new Label(body, deviceName) { Color = Muted });
        _resolution = AddRow(stats, body, "Resolution");
        _frameRate = AddRow(stats, body, "Frame rate");

        var logPanel = Ui.Add(
            new Panel
            {
                Anchor = Anchor.TopRight,
                Position = new float2(16, 16),
                Padding = 12,
                Spacing = 6,
                Background = PanelBackground,
                BorderColor = PanelBorder,
                BorderWidth = 1,
            }
        );
        logPanel.Add(new Label(title, "Event log"));
        _log = logPanel.Add(
            new ScrollPanel
            {
                // Long lines (an error, a long note) scroll sideways rather than widening the panel.
                MaxWidth = 280,
                MaxHeight = 150,
                MinSize = new float2(240, 0),
                Spacing = 2,
                StickToEnd = true,
            }
        );
        Log($"GPU: {deviceName}");
        Log("Scroll here with the mouse wheel; Shift+wheel or a tilt wheel scrolls sideways");

        var note = logPanel.Add(
            new TextBox(body)
            {
                Width = 240,
                Placeholder = "Type a note, then Enter",
                FocusColor = Accent,
            }
        );
        note.Submitted += box =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
                return;
            Log($"Note: {box.Text.Trim()}");
            box.Text = "";
        };

        var hint = Ui.Add(
            new Panel
            {
                Anchor = Anchor.BottomRight,
                Position = new float2(16, 16),
                Padding = 8,
                Background = PanelBackground,
            }
        );
        hint.Add(
            new Label(
                body,
                "1 / 2  Load selected song to deck A / B\nZ / X  Cue / Play deck A\nN / M  Cue / Play deck B\n"
                    + "Tab  Next control\nEnter  Press, submit or load\nEsc  Leave a box, or quit"
            )
            {
                Color = Muted,
            }
        );
    }

    /// <summary>
    /// Shows <paramref name="library"/> as a song browser at the top, between the stats panel and the event log,
    /// with a "Load to" button for each named deck. Songs in view are given to <paramref name="analyser"/>.
    /// </summary>
    public SongBrowser AddBrowser(
        Music.MusicLibrary library,
        Music.LibraryAnalyser? analyser,
        IEnumerable<string> deckNames
    )
    {
        _browser = Ui.Add(
            new SongBrowser(library, analyser, deckNames, _body, _title, Muted, Accent, listHeight: 248)
            {
                Position = new float2(292, 16),
                Padding = 12,
                Background = PanelBackground,
                BorderColor = PanelBorder,
                BorderWidth = 1,
            }
        );
        return _browser;
    }

    /// <summary>
    /// Adds the crossfader to the deck row, after any decks added so far; call it between the left and right decks.
    /// </summary>
    public void AddCrossfader(AudioEngine engine)
    {
        var panel = DeckRow.Add(
            new Panel
            {
                Padding = 12,
                Spacing = 10,
                Background = PanelBackground,
                BorderColor = PanelBorder,
                BorderWidth = 1,
            }
        );
        panel.Add(new Label(_title, "Mix"));
        panel.Add(new Label(_body, "Crossfader") { Color = Muted });

        var row = panel.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        row.Add(new Label(_body, "A") { Color = Muted });
        var slider = row.Add(
            new Slider
            {
                Length = 140,
                Thickness = 22,
                HandleLength = 12,
                DefaultValue = 0.5f,
                Value = engine.Crossfader,
                FocusColor = Accent,
            }
        );
        slider.Marks.Add(0.5f);
        slider.Changed += s => engine.Crossfader = s.Value;
        row.Add(new Label(_body, "B") { Color = Muted });

        panel.Add(new Label(_body, "Double-click to centre") { Color = Muted });
    }

    /// <summary>
    /// The decks and the crossfader, side by side along the bottom-left in the order they're added.
    /// </summary>
    Panel DeckRow =>
        _deckRow ??= Ui.Add(
            new Panel
            {
                Anchor = Anchor.BottomLeft,
                Position = new float2(16, 16),
                Direction = Direction.Horizontal,
                Spacing = 16,
            }
        );

    /// <summary>
    /// Shows <paramref name="deck"/>'s controls in the bottom-left, kept up to date by <see cref="Update"/>.
    /// </summary>
    public void AddDeck(Deck deck)
    {
        var panel = DeckRow.Add(
            new DeckPanel(deck, _body, _title, Muted, Accent)
            {
                Padding = 12,
                Background = PanelBackground,
                BorderColor = PanelBorder,
                BorderWidth = 1,
            }
        );
        _decks.Add(panel);
    }

    /// <summary>
    /// Adds a timestamped line to the event log, dropping the oldest past a limit.
    /// </summary>
    public void Log(string message)
    {
        const int MAX_ENTRIES = 200;
        if (_log.Children.Count >= MAX_ENTRIES)
            _log.Children.RemoveAt(0);
        _log.Add(new Label(_body, $"{DateTime.Now:HH:mm:ss}  {message}") { Color = Muted });
    }

    /// <summary>
    /// Adds a "name  value" line to <paramref name="parent"/> and returns the value's label, in the accent colour.
    /// </summary>
    static Label AddRow(Panel parent, Font font, string name, string value = "")
    {
        var row = parent.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        row.Add(new Label(font, name) { Color = Muted });
        return row.Add(new Label(font, value) { Color = Accent });
    }

    public void Update(double time, int2 framebufferSize)
    {
        _resolution.Text = $"{framebufferSize.X} × {framebufferSize.Y}";
        foreach (var deck in _decks)
            deck.Refresh();
        _browser?.Update();
        if (framebufferSize != _lastFramebufferSize)
        {
            Log($"Framebuffer is {framebufferSize.X} × {framebufferSize.Y}");
            _lastFramebufferSize = framebufferSize;
        }

        // Averaged over half a second, so the number is readable.
        _framesSinceSample++;
        var elapsed = time - _sampleStart;
        if (elapsed >= 0.5)
        {
            var fps = _framesSinceSample / elapsed;
            _frameRate.Text = $"{fps:F0} fps  ({1000 / fps:F2} ms)";
            _sampleStart = time;
            _framesSinceSample = 0;
        }
    }
}
