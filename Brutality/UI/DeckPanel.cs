using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// A deck's controls and read-out, in the deck's colour: its letter, what's loaded, the tempo where it's playing and
/// its key, how far in it is and how long's left, the whole song's waveform to see and seek in, and Cue and Play.
/// Sized by whoever lays it out, with <see cref="Width"/>. Call <see cref="Refresh"/> once a frame.
/// </summary>
sealed class DeckPanel : Panel
{
    public const float HEIGHT = 160;
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

    public float Width { get; set; } = 480;

    public DeckPanel(Deck deck, Font body, Font bold, Font big)
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
            }
        );
        _overview.Changed += slider => _deck.Seek(TimeSpan.FromSeconds(slider.Value));

        // Cue acts on press and release rather than click, since holding it previews from the cue point.
        _cue = Add(new Button(bold, "CUE") { MinSize = new float2(72, 30), FocusColor = Theme.Focus });
        _cue.PressStarted += _ => _deck.CueDown();
        _cue.PressEnded += _ => _deck.CueUp();

        _playPause = Add(new Button(bold, "PLAY") { MinSize = new float2(96, 30), FocusColor = Theme.Focus });
        _playPause.Clicked += _ => _deck.TogglePlay();

        Refresh();
    }

    public void Refresh()
    {
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
            _bpm.Text = $"{(analysis.Grid.IsVariable ? "~" : "")}{analysis.Grid.BpmAt(position.TotalSeconds):F2}";
            _key.Text = $"{analysis.Key.Camelot}  {analysis.Key.ShortName}";
            _key.Color = Theme.CamelotColor(analysis.Key.CamelotNumber);
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
        _overview.Max = (float)_deck.Duration.TotalSeconds;
        // While it's being dragged the slider leads and the deck follows, so don't pull it back.
        if (!_overview.IsDragging)
            _overview.Value = (float)position.TotalSeconds;
        _overview.Marks.Clear();
        _overview.Marks.Add((float)_deck.CuePoint.TotalSeconds);

        _cue.Enabled = true;
        _playPause.Enabled = true;
        // A cue preview is playing, but Play would latch it rather than pause, so it still offers Play.
        var playing = _deck.IsPlaying && !_deck.IsCuePreviewing;
        _playPause.Text = playing ? "PAUSE" : "PLAY";
        _playPause.Background = playing ? Theme.Mix(Theme.PanelRaised, _color, 0.55f) : Colors.Hex(0x2A2A35);
        _playPause.HoverBackground = playing ? Theme.Mix(Theme.PanelRaised, _color, 0.7f) : Colors.Hex(0x3A3A48);
    }

    public override float2 Measure()
    {
        // The title gets what the tempo and key on the right leave it.
        _bpm.Measure();
        _key.Measure();
        var right = MathF.Max(MathF.Max(_bpm.Size.X, _key.Size.X), 90);
        _title.MaxWidth = _artist.MaxWidth = MathF.Max(40, Width - 2 * PAD - 40 - right - 16);
        _overview.Length = Width - 2 * PAD;
        _loading.MaxWidth = Width - 2 * PAD - 72 - 96 - 24 - 90;

        foreach (var child in Children)
            child.Measure();
        return Size = new float2(Width, HEIGHT);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var left = position.X + PAD;
        var right = position.X + Width - PAD;
        var top = position.Y + PAD;
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));

        Place(_badge, left, top + 2);
        Place(_title, left + 40, top);
        Place(_artist, left + 40, top + _title.Size.Y + 1);
        Place(_bpm, right - _bpm.Size.X, top - 6);
        Place(_key, right - _key.Size.X, top + _bpm.Size.Y - 6);

        var timesY = top + 52;
        Place(_elapsed, left, timesY);
        Place(_remaining, left + _elapsed.Size.X + 12, timesY);
        Place(_gain, right - _gain.Size.X, timesY);

        Place(_overview, left, timesY + _elapsed.Size.Y + 4);

        var buttonsY = position.Y + HEIGHT - PAD - _cue.Size.Y;
        Place(_cue, left, buttonsY);
        Place(_playPause, left + _cue.Size.X + 8, buttonsY);
        Place(_loading, left + _cue.Size.X + 8 + _playPause.Size.X + 12, buttonsY + (_cue.Size.Y - _loading.Size.Y) / 2);
        Place(_cuePoint, right - _cuePoint.Size.X, buttonsY + (_cue.Size.Y - _cuePoint.Size.Y) / 2);
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
