using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// A deck's controls and read-out: what's loaded, where it's up to and its cue point, with Cue and Play/Pause
/// buttons. Call <see cref="Refresh"/> each frame to pick up the deck's position.
/// </summary>
sealed class DeckPanel : Panel
{
    readonly Deck _deck;
    readonly Label _title;
    readonly Label _artist;
    readonly Label _time;
    readonly Label _loading;
    readonly Button _cue;
    readonly Button _playPause;

    public DeckPanel(Deck deck, Font body, Font heading, float4 muted, float4 accent)
    {
        _deck = deck;
        Spacing = 4;

        Add(new Label(heading, $"Deck {deck.Name}"));
        _title = Add(new Label(body) { Color = Colors.White });
        _artist = Add(new Label(body) { Color = muted });
        _time = Add(new Label(body) { Color = accent });
        // Always there, empty when not loading, so the panel doesn't change height as loads come and go.
        _loading = Add(new Label(body) { Color = muted });

        var buttons = Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });

        // Cue acts on press and release rather than click, since holding it previews from the cue point.
        _cue = buttons.Add(new Button(body, "Cue") { MinSize = new float2(84, 0), FocusColor = accent });
        _cue.PressStarted += _ => _deck.CueDown();
        _cue.PressEnded += _ => _deck.CueUp();

        _playPause = buttons.Add(new Button(body, "Play") { MinSize = new float2(84, 0), FocusColor = accent });
        _playPause.Clicked += _ => _deck.TogglePlay();

        Refresh();
    }

    public void Refresh()
    {
        // Loading happens in the background, and whatever's on the deck carries on meanwhile.
        _loading.Text = _deck.LoadingName is { } loading ? $"Loading {loading}…" : "";

        if (!_deck.IsLoaded)
        {
            _title.Text = _deck.IsLoading ? "Loading…" : "No track loaded";
            _artist.Text = "";
            _time.Text = "";
            _cue.Enabled = false;
            _playPause.Enabled = false;
            return;
        }

        _title.Text = _deck.Title;
        _artist.Text = _deck.Artist;

        var position = _deck.Position;
        var remaining = _deck.Duration - position;
        _time.Text =
            $"{Format(position)} / {Format(_deck.Duration)}   -{Format(remaining)}   Cue {Format(_deck.CuePoint)}";

        _cue.Enabled = true;
        _playPause.Enabled = true;
        // A cue preview is playing, but Play would latch it rather than pause, so it still offers Play.
        _playPause.Text = _deck.IsPlaying && !_deck.IsCuePreviewing ? "Pause" : "Play";
    }

    static string Format(TimeSpan time) =>
        time < TimeSpan.Zero ? "0:00" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}
