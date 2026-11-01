using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// A deck's controls and read-out, in the deck's colour: its letter, what's loaded, the tempo where it's playing and
/// its key, how far in it is and how long's left, the whole song's waveform to see and seek in with its hotcues
/// marked, Cue and Play, a loop of so many beats to start and stop, halve and double, a pad for each hotcue (press an
/// empty one to set it where the deck is, a set one to jump there, and Shift-click or right-click one to clear it),
/// and the tempo fader: down is faster, as on a turntable, double-click puts it back to zero, and the button under it
/// changes its range. The tempo and key shown are what's coming out, fader and all.
/// <para>
/// The controls you reach for while mixing (the tempo fader, the tempo and key, the loop) sit on the side nearest
/// the mixer, and the song's details, Cue and Play on the outer side. A deck to the right of the mixer is
/// <see cref="Mirrored"/>, so its layout is the left deck's reflected. The waveform still runs left to right, as
/// time does, and the pads still count 1 to 8.
/// </para>
/// Sized by whoever lays it out, with <see cref="Width"/>. Call <see cref="Refresh"/> once a frame.
/// </summary>
sealed class DeckPanel : Panel
{
    public const float HEIGHT = 200;
    const float PAD = 10;

    readonly Deck _deck;
    readonly float4 _color;
    readonly Badge _badge;
    readonly Label _title;
    readonly Label _artist;
    readonly Label _bpm;
    readonly Label _key;
    readonly Label _elapsed;
    readonly Label _remaining;
    readonly Label _gain;
    readonly Label _cuePoint;
    readonly Label _loading;
    readonly WaveformBar _overview;
    readonly Button _cue;
    readonly Button _playPause;
    readonly List<HotcuePad> _pads = [];
    readonly Button _halveLoop;
    readonly Button _loop;
    readonly Button _doubleLoop;
    readonly Label _tempoReadout;
    readonly Slider _tempoFader;
    readonly Button _tempoRange;

    /// <summary>
    /// The width of the tempo fader's column down the right-hand side, and the space before it.
    /// </summary>
    const float TEMPO_COLUMN = 48;
    const float TEMPO_GAP = 10;

    const float PAD_GAP = 4;
    const float PAD_HEIGHT = 28;
    const float ROW_GAP = 6;

    public float Width { get; set; } = 480;

    /// <summary>
    /// Laid out as a reflection, for a deck to the right of the mixer: tempo fader on the left, details on the
    /// right.
    /// </summary>
    public bool Mirrored { get; set; }

    /// <param name="padKeys">The keys that press the hotcue pads, in order, to label them with.</param>
    public DeckPanel(Deck deck, Font small, Font body, Font bold, Font big, IReadOnlyList<string>? padKeys = null)
    {
        _deck = deck;
        _color = Theme.DeckColor(deck.Name);
        Background = Theme.Panel;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;

        _badge = Add(new Badge(bold, deck.Name, _color, Theme.TitleBar, 30));
        _title = Add(new Label(bold) { Color = Theme.Text });
        _artist = Add(new Label(body) { Color = Theme.Muted });
        _bpm = Add(new Label(big) { Color = Theme.Text });
        _key = Add(new Label(bold));
        _elapsed = Add(new Label(body) { Color = Theme.Text });
        _remaining = Add(new Label(body) { Color = _color });
        _gain = Add(new Label(body) { Color = Theme.Muted });
        _cuePoint = Add(new Label(body) { Color = Theme.Muted });
        _loading = Add(new Label(body) { Color = Theme.Muted });

        // The song's waveform, coloured as it plays, with a thin playhead and a mark at the cue point; click or drag
        // to seek.
        _overview = Add(
            new WaveformBar
            {
                Thickness = 36,
                TrackThickness = 6,
                HandleLength = 2,
                ShowFill = true,
                FillColor = Theme.Fade(_color, 0.6f),
                PlayedColor = _color,
                UnplayedColor = Colors.Hex(0xFFFFFF, 0.3f),
                HandleColor = Colors.White,
                FocusColor = Theme.Focus,
                FlagFont = small,
            }
        );
        _overview.Changed += slider => _deck.Seek(TimeSpan.FromSeconds(slider.Value));

        // Cue acts on press and release rather than click, since holding it previews from the cue point.
        _cue = Add(new Button(bold, "CUE") { MinSize = new float2(72, 30), FocusColor = Theme.Focus });
        _cue.PressStarted += _ => _deck.CueDown();
        _cue.PressEnded += _ => _deck.CueUp();

        _playPause = Add(new Button(bold, "PLAY") { MinSize = new float2(96, 30), FocusColor = Theme.Focus });
        _playPause.Clicked += _ => _deck.TogglePlay();

        // The loop: halve it, start or stop it (its size on the button), double it.
        _halveLoop = Add(new Button(bold, "\u25C4") { MinSize = new float2(30, 30), FocusColor = Theme.Focus });
        _halveLoop.Clicked += _ => _deck.HalveLoop();
        _loop = Add(new Button(bold, "LOOP 4") { MinSize = new float2(96, 30), FocusColor = Theme.Focus });
        _loop.Clicked += _ => _deck.ToggleLoop();
        _doubleLoop = Add(new Button(bold, "\u25BA") { MinSize = new float2(30, 30), FocusColor = Theme.Focus });
        _doubleLoop.Clicked += _ => _deck.DoubleLoop();

        // The tempo fader runs from +range at the bottom to -range at the top, so pulling it down speeds up.
        _tempoReadout = Add(new Label(small) { Color = Theme.Muted });
        _tempoFader = Add(
            new Slider
            {
                Orientation = Orientation.Vertical,
                Min = (float)deck.TempoRange,
                Max = -(float)deck.TempoRange,
                Value = (float)deck.Tempo,
                DefaultValue = 0,
                Thickness = 22,
                HandleLength = 10,
                FocusColor = Theme.Focus,
            }
        );
        _tempoFader.Marks.Add(0);
        _tempoFader.Changed += slider => _deck.Tempo = slider.Value;
        _tempoRange = Add(new Button(small, "±8%") { MinSize = new float2(TEMPO_COLUMN, 22), Padding = 3, FocusColor = Theme.Focus });
        _tempoRange.Clicked += _ =>
        {
            var ranges = Deck.TempoRanges;
            var next = ranges[(ranges.ToList().IndexOf(_deck.TempoRange) + 1) % ranges.Count];
            _deck.TempoRange = next;
        };

        for (var i = 0; i < Deck.HOTCUE_COUNT; i++)
        {
            var index = i;
            var pad = Add(
                new HotcuePad(bold, i + 1)
                {
                    Color = Theme.HotcueColor(i),
                    FocusColor = Theme.Focus,
                    KeyHint = padKeys is not null && i < padKeys.Count ? padKeys[i] : null,
                    HintFont = small,
                }
            );
            pad.Triggered += _ => _deck.HotcueDown(index);
            pad.Deleted += _ => _deck.DeleteHotcue(index);
            _pads.Add(pad);
        }

        Refresh();
    }

    public void Refresh()
    {
        RefreshTempo();
        _loading.Text = _deck.LoadingName is { } loading ? $"Loading {loading}…" : "";

        if (!_deck.IsLoaded)
        {
            _title.Text = _deck.IsLoading ? "Loading…" : "No track loaded";
            _artist.Text = "";
            _bpm.Text = "";
            _key.Text = "";
            _elapsed.Text = "";
            _remaining.Text = "";
            _gain.Text = "";
            _cuePoint.Text = "";
            _cue.Enabled = false;
            _playPause.Enabled = false;
            _playPause.Text = "PLAY";
            _overview.Enabled = false;
            _overview.Max = 1;
            _overview.Value = 0;
            _overview.Marks.Clear();
            _overview.Waveform = null;
            _overview.Grid = null;
            _overview.Flags = [];
            _overview.Region = null;
            foreach (var pad in _pads)
                (pad.Enabled, pad.IsSet) = (false, false);
            RefreshLoop();
            return;
        }

        _title.Text = _deck.Title;
        _artist.Text = _deck.Artist;

        var position = _deck.Position;
        _elapsed.Text = $"{Format(position)} / {Format(_deck.Duration)}";
        _remaining.Text = $"-{Format(_deck.Duration - position)}";
        _cuePoint.Text = $"Cue {Format(_deck.CuePoint)}";

        // The tempo where the deck is, which moves on through a song whose tempo varies; "~" says it does.
        if (_deck.Analysis is { } analysis)
        {
            // As heard: the fader speeds the tempo up and, past about 3%, moves the key a semitone or more.
            var key = _deck.Key ?? analysis.Key;
            _bpm.Text = $"{(analysis.Grid.IsVariable ? "~" : "")}{_deck.Bpm ?? analysis.Bpm:F2}";
            _key.Text = $"{key.Camelot}  {key.ShortName}{(_deck.KeyShift != 0 ? $"  ({_deck.KeyShift:+0;-0})" : "")}";
            _key.Color = Theme.CamelotColor(key.CamelotNumber);
            _gain.Text = $"Auto gain {analysis.AutoGainDb:+0.0;-0.0;0.0} dB";
        }
        else
        {
            _bpm.Text = "";
            _key.Text = "";
            _gain.Text = "Analysing…";
        }

        _overview.Enabled = true;
        _overview.Waveform = _deck.Waveform;
        _overview.Grid = _deck.Analysis?.Grid;
        _overview.Max = (float)_deck.Duration.TotalSeconds;
        // While it's being dragged the slider leads and the deck follows, so don't pull it back.
        if (!_overview.IsDragging)
            _overview.Value = (float)position.TotalSeconds;
        _overview.Marks.Clear();
        _overview.Marks.Add((float)_deck.CuePoint.TotalSeconds);

        var flags = new List<(float, float4, string)>();
        for (var i = 0; i < _pads.Count; i++)
        {
            var hotcue = _deck.Hotcues[i];
            (_pads[i].Enabled, _pads[i].IsSet) = (true, hotcue is not null);
            if (hotcue is { } at)
                flags.Add(((float)at.TotalSeconds, Theme.HotcueColor(i), (i + 1).ToString()));
        }
        _overview.Flags = flags;
        _overview.Region = _deck.Loop is { } loop ? ((float)loop.Start.TotalSeconds, (float)loop.End.TotalSeconds) : null;
        RefreshLoop();

        _cue.Enabled = true;
        _playPause.Enabled = true;
        // A cue preview is playing, but Play would latch it rather than pause, so it still offers Play.
        var playing = _deck.IsPlaying && !_deck.IsCuePreviewing;
        _playPause.Text = playing ? "PAUSE" : "PLAY";
        _playPause.Background = playing ? Theme.Mix(Theme.PanelRaised, _color, 0.55f) : Colors.Hex(0x2A2A35);
        _playPause.HoverBackground = playing ? Theme.Mix(Theme.PanelRaised, _color, 0.7f) : Colors.Hex(0x3A3A48);
    }

    /// <summary>
    /// The tempo fader follows the deck, which keyboard or code may have moved, except while it's being dragged.
    /// </summary>
    void RefreshTempo()
    {
        _tempoFader.Min = (float)_deck.TempoRange;
        _tempoFader.Max = -(float)_deck.TempoRange;
        if (!_tempoFader.IsDragging)
            _tempoFader.Value = (float)_deck.Tempo;
        _tempoReadout.Text = $"{_deck.Tempo * 100:+0.0;-0.0;0.0}%";
        _tempoReadout.Color = _deck.Tempo == 0 ? Theme.Muted : Theme.Text;
        _tempoRange.Text = $"±{_deck.TempoRange * 100:0}%";
    }

    void RefreshLoop()
    {
        _loop.Text = $"LOOP {FormatBeats(_deck.LoopBeats)}";
        var canLoop = _deck.CanLoop;
        _loop.Enabled = canLoop;
        _halveLoop.Enabled = _doubleLoop.Enabled = canLoop;
        var looping = _deck.IsLooping;
        _loop.Background = looping ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.6f) : Colors.Hex(0x2A2A35);
        _loop.HoverBackground = looping ? Theme.Mix(Theme.PanelRaised, Theme.Loop, 0.75f) : Colors.Hex(0x3A3A48);
        _loop.TextColor = looping ? Theme.TitleBar : Theme.Text;
    }

    /// <summary>
    /// A loop's length as DJs say it: "4", or "1/2" for half a beat.
    /// </summary>
    public static string FormatBeats(double beats) => beats >= 1 ? $"{beats:0}" : $"1/{Math.Round(1 / beats):0}";

    public override float2 Measure()
    {
        // The title gets what the tempo and key on the right leave it.
        _bpm.Measure();
        _key.Measure();
        var right = MathF.Max(MathF.Max(_bpm.Size.X, _key.Size.X), 90);
        var content = Width - 2 * PAD - TEMPO_COLUMN - TEMPO_GAP;
        _title.MaxWidth = _artist.MaxWidth = MathF.Max(40, content - 40 - right - 16);
        _overview.Length = content;
        // The pads share the width between them; the loading line gets what the buttons either side leave it.
        var padWidth = MathF.Floor((Width - 2 * PAD - (_pads.Count - 1) * PAD_GAP) / _pads.Count);
        foreach (var pad in _pads)
            pad.FixedSize = new float2(padWidth, PAD_HEIGHT);
        _loading.MaxWidth = MathF.Max(0, content - 72 - 8 - 96 - 24 - (30 + 4 + 96 + 4 + 30) - 12);

        // The fader fills its column between the readout above and the range button below, down to the transport.
        _tempoReadout.Measure();
        _tempoRange.Measure();
        var columnHeight = HEIGHT - 2 * PAD - PAD_HEIGHT - ROW_GAP;
        _tempoFader.Length = columnHeight - _tempoReadout.Size.Y - 4 - _tempoRange.Size.Y - 6;

        foreach (var child in Children)
            child.Measure();
        return Size = new float2(Width, HEIGHT);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var top = position.Y + PAD;

        // The tempo column is on the side nearest the mixer; the rest of the deck is the content beside it.
        var columnLeft = Mirrored ? position.X + PAD : position.X + Width - PAD - TEMPO_COLUMN;
        var contentLeft = Mirrored ? columnLeft + TEMPO_COLUMN + TEMPO_GAP : position.X + PAD;
        var contentRight = Mirrored ? position.X + Width - PAD : columnLeft - TEMPO_GAP;

        // Within the content, things go in from the outer edge (the song's details, the transport) or from the inner
        // edge beside the tempo column (its readouts, the loop), whichever side that is for this deck.
        float Outer(float offset, Element child) => Mirrored ? contentRight - offset - child.Size.X : contentLeft + offset;
        float Inner(float offset, Element child) => Mirrored ? contentLeft + offset : contentRight - offset - child.Size.X;
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));

        Place(_badge, Outer(0, _badge), top + 2);
        Place(_title, Outer(40, _title), top);
        Place(_artist, Outer(40, _artist), top + _title.Size.Y + 1);
        Place(_bpm, Inner(0, _bpm), top - 6);
        Place(_key, Inner(0, _key), top + _bpm.Size.Y - 6);

        var timesY = top + 52;
        Place(_elapsed, Outer(0, _elapsed), timesY);
        Place(_remaining, Outer(_elapsed.Size.X + 12, _remaining), timesY);
        Place(_cuePoint, Outer(_elapsed.Size.X + 12 + _remaining.Size.X + 16, _cuePoint), timesY);
        Place(_gain, Inner(0, _gain), timesY);

        Place(_overview, contentLeft, timesY + _elapsed.Size.Y + 4);

        // From the bottom: the hotcue pads across the whole width, in order, and above them the transport and the
        // loop.
        var padsY = position.Y + HEIGHT - PAD - PAD_HEIGHT;
        var x = position.X + PAD;
        foreach (var pad in _pads)
        {
            Place(pad, x, padsY);
            x += pad.Size.X + PAD_GAP;
        }

        var buttonsY = padsY - ROW_GAP - _cue.Size.Y;
        Place(_cue, Outer(0, _cue), buttonsY);
        Place(_playPause, Outer(_cue.Size.X + 8, _playPause), buttonsY);
        Place(_loading, Outer(_cue.Size.X + 8 + _playPause.Size.X + 12, _loading), buttonsY + (_cue.Size.Y - _loading.Size.Y) / 2);

        // The loop's buttons keep their order, halve on the left, whichever side the group is on.
        var loopWidth = _halveLoop.Size.X + 4 + _loop.Size.X + 4 + _doubleLoop.Size.X;
        var loopLeft = Mirrored ? contentLeft : contentRight - loopWidth;
        Place(_halveLoop, loopLeft, buttonsY);
        Place(_loop, loopLeft + _halveLoop.Size.X + 4, buttonsY);
        Place(_doubleLoop, loopLeft + _halveLoop.Size.X + 4 + _loop.Size.X + 4, buttonsY);

        Place(_tempoReadout, columnLeft + (TEMPO_COLUMN - _tempoReadout.Size.X) / 2, top - 2);
        var faderTop = top - 2 + _tempoReadout.Size.Y + 4;
        Place(_tempoFader, columnLeft + (TEMPO_COLUMN - _tempoFader.Size.X) / 2, faderTop);
        Place(_tempoRange, columnLeft, faderTop + _tempoFader.Size.Y + 6);
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        // A strip of the deck's colour along the top, so which deck is which reads at a glance.
        batch.AddRect(ScreenPosition, new float2(Size.X, 2), _color);
        DrawChildren(batch);
    }

    static string Format(TimeSpan time) =>
        time < TimeSpan.Zero ? "0:00" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}
