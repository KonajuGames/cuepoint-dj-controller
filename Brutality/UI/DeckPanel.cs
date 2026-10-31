using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// A deck's controls and read-out: what's loaded, where it's up to and its cue point, a progress bar to seek with,
/// Cue and Play/Pause buttons, and a volume fader. Call <see cref="Refresh"/> each frame to pick up the deck's
/// position.
/// </summary>
sealed class DeckPanel : Panel
{
    const float INFO_WIDTH = 300;

    readonly Deck _deck;
    readonly Label _title;
    readonly Label _artist;
    readonly Label _time;
    readonly Label _loading;
    readonly Slider _progress;
    readonly Button _cue;
    readonly Button _playPause;
    readonly Slider _volume;

    public DeckPanel(Deck deck, Font body, Font heading, float4 muted, float4 accent)
    {
        _deck = deck;
        Direction = Direction.Horizontal;
        Spacing = 14;

        var info = Add(new Panel { Spacing = 4, MinSize = new float2(INFO_WIDTH, 0) });
        info.Add(new Label(heading, $"Deck {deck.Name}"));
        _title = info.Add(new Label(body) { Color = Colors.White });
        _artist = info.Add(new Label(body) { Color = muted });
        _time = info.Add(new Label(body) { Color = accent });
        // Always there, empty when not loading, so the panel doesn't change height as loads come and go.
        _loading = info.Add(new Label(body) { Color = muted });

        // Fills as the song plays, with a thin playhead and a mark at the cue point; click or drag to seek.
        _progress = info.Add(
            new Slider
            {
                Length = INFO_WIDTH,
                Thickness = 14,
                TrackThickness = 6,
                HandleLength = 3,
                ShowFill = true,
                FillColor = accent * new float4(1, 1, 1, 0.6f),
                HandleColor = Colors.White,
                FocusColor = accent,
            }
        );
        _progress.Changed += slider => _deck.Seek(TimeSpan.FromSeconds(slider.Value));

        var buttons = info.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });

        // Cue acts on press and release rather than click, since holding it previews from the cue point.
        _cue = buttons.Add(new Button(body, "Cue") { MinSize = new float2(84, 0), FocusColor = accent });
        _cue.PressStarted += _ => _deck.CueDown();
        _cue.PressEnded += _ => _deck.CueUp();

        _playPause = buttons.Add(new Button(body, "Play") { MinSize = new float2(84, 0), FocusColor = accent });
        _playPause.Clicked += _ => _deck.TogglePlay();

        // A channel fader: up is louder, and double-clicking puts it back to full.
        var fader = Add(new Panel { Spacing = 6 });
        fader.Add(new Label(body, "Vol") { Color = muted });
        _volume = fader.Add(
            new Slider
            {
                Orientation = Orientation.Vertical,
                Length = 150,
                Thickness = 22,
                HandleLength = 12,
                DefaultValue = 1,
                Value = deck.Volume,
                FillColor = accent,
                ShowFill = true,
                FocusColor = accent,
            }
        );
        _volume.Changed += slider => _deck.Volume = slider.Value;

        Refresh();
    }

    public void Refresh()
    {
        _loading.Text = _deck.LoadingName is { } loading ? $"Loading {loading}…" : "";
        if (!_volume.IsDragging)
            _volume.Value = _deck.Volume;

        if (!_deck.IsLoaded)
        {
            _title.Text = _deck.IsLoading ? "Loading…" : "No track loaded";
            _artist.Text = "";
            _time.Text = "";
            _cue.Enabled = false;
            _playPause.Enabled = false;
            _progress.Enabled = false;
            _progress.Max = 1;
            _progress.Value = 0;
            _progress.Marks.Clear();
            return;
        }

        _title.Text = _deck.Title;
        _artist.Text = _deck.Artist;

        var position = _deck.Position;
        var remaining = _deck.Duration - position;
        _time.Text =
            $"{Format(position)} / {Format(_deck.Duration)}   -{Format(remaining)}   Cue {Format(_deck.CuePoint)}";

        _progress.Enabled = true;
        _progress.Max = (float)_deck.Duration.TotalSeconds;
        // While it's being dragged the slider leads and the deck follows, so don't pull it back.
        if (!_progress.IsDragging)
            _progress.Value = (float)position.TotalSeconds;
        _progress.Marks.Clear();
        _progress.Marks.Add((float)_deck.CuePoint.TotalSeconds);

        _cue.Enabled = true;
        _playPause.Enabled = true;
        // A cue preview is playing, but Play would latch it rather than pause, so it still offers Play.
        _playPause.Text = _deck.IsPlaying && !_deck.IsCuePreviewing ? "Pause" : "Play";
    }

    static string Format(TimeSpan time) =>
        time < TimeSpan.Zero ? "0:00" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}
