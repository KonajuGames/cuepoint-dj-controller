using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// A deck's controls and read-out: what's loaded, where it's up to, and a play/pause button. Call
/// <see cref="Refresh"/> each frame to pick up the deck's position.
/// </summary>
sealed class DeckPanel : Panel
{
    readonly Deck _deck;
    readonly Label _title;
    readonly Label _artist;
    readonly Label _time;
    readonly Button _playPause;

    public DeckPanel(Deck deck, Font body, Font heading, float4 muted, float4 accent)
    {
        _deck = deck;
        Spacing = 4;

        Add(new Label(heading, $"Deck {deck.Name}"));
        _title = Add(new Label(body) { Color = Colors.White });
        _artist = Add(new Label(body) { Color = muted });
        _time = Add(new Label(body) { Color = accent });

        _playPause = Add(new Button(body, "Play") { MinSize = new float2(84, 0), FocusColor = accent });
        _playPause.Clicked += _ => _deck.TogglePlay();

        Refresh();
    }

    public void Refresh()
    {
        if (!_deck.IsLoaded)
        {
            _title.Text = "No track loaded";
            _artist.Text = "";
            _time.Text = "";
            _playPause.Enabled = false;
            return;
        }

        _title.Text = _deck.Title;
        _artist.Text = _deck.Artist;

        var position = _deck.Position;
        var remaining = _deck.Duration - position;
        _time.Text = $"{Format(position)} / {Format(_deck.Duration)}   -{Format(remaining)}";

        _playPause.Enabled = true;
        _playPause.Text = _deck.IsPlaying ? "Pause" : "Play";
    }

    static string Format(TimeSpan time) =>
        time < TimeSpan.Zero ? "0:00" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}
