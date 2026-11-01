using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// The mixer between the decks: for each, its level as it plays and its volume fader, in its colour; the crossfader
/// across the bottom; and a switch for automatic gain in the middle. Call <see cref="Refresh"/> once a frame.
/// </summary>
sealed class MixerPanel : Panel
{
    public const float WIDTH = 300;
    const float PAD = 10;

    readonly AudioEngine _engine;
    readonly Label _heading;
    readonly List<(Deck Deck, Label Name, LevelMeter Meter, Slider Fader)> _channels = [];
    readonly Slider _crossfader;
    readonly Label _crossLeft;
    readonly Label _crossRight;
    readonly Button _autoGain;

    public MixerPanel(AudioEngine engine, IReadOnlyList<Deck> decks, Font small, Font bold)
    {
        _engine = engine;
        Background = Theme.Panel;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;

        _heading = Add(new Label(small, "MIXER") { Color = Theme.Muted });
        foreach (var deck in decks)
        {
            var color = Theme.DeckColor(deck.Name);
            var name = Add(new Label(bold, deck.Name) { Color = color });
            var meter = Add(new LevelMeter(2) { Length = 124, BarThickness = 4, BarGap = 2, Segments = 26 });

            // A channel fader: up is louder, and double-clicking puts it back to full.
            var fader = Add(
                new Slider
                {
                    Orientation = Orientation.Vertical,
                    Length = 124,
                    Thickness = 22,
                    HandleLength = 12,
                    DefaultValue = 1,
                    Value = deck.Volume,
                    FillColor = color,
                    ShowFill = true,
                    FocusColor = Theme.Focus,
                }
            );
            fader.Changed += slider => deck.Volume = slider.Value;
            _channels.Add((deck, name, meter, fader));
        }

        _crossLeft = Add(new Label(bold, decks.Count > 0 ? decks[0].Name : "") { Color = decks.Count > 0 ? Theme.DeckColor(decks[0].Name) : Theme.Muted });
        _crossRight = Add(new Label(bold, decks.Count > 1 ? decks[^1].Name : "") { Color = decks.Count > 1 ? Theme.DeckColor(decks[^1].Name) : Theme.Muted });
        _crossfader = Add(
            new Slider
            {
                Length = 150,
                Thickness = 22,
                HandleLength = 12,
                DefaultValue = 0.5f,
                Value = engine.Crossfader,
                FocusColor = Theme.Focus,
            }
        );
        _crossfader.Marks.Add(0.5f);
        _crossfader.Changed += slider => engine.Crossfader = slider.Value;

        _autoGain = Add(new Button(small, "AUTO GAIN") { FocusColor = Theme.Focus, MinSize = new float2(90, 26) });
        _autoGain.Clicked += _ => engine.AutoGain = !engine.AutoGain;

        Refresh();
    }

    public void Refresh()
    {
        foreach (var (deck, _, meter, fader) in _channels)
        {
            var (left, right) = deck.Meter.TakePeaks();
            meter.Push(left, right);
            if (!fader.IsDragging)
                fader.Value = deck.Volume;
        }
        if (!_crossfader.IsDragging)
            _crossfader.Value = _engine.Crossfader;

        _autoGain.Background = _engine.AutoGain ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.55f) : Colors.Hex(0x2A2A35);
        _autoGain.HoverBackground = _engine.AutoGain ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.7f) : Colors.Hex(0x3A3A48);
        _autoGain.TextColor = _engine.AutoGain ? Colors.White : Theme.Muted;
    }

    public override float2 Measure()
    {
        foreach (var child in Children)
            child.Measure();
        return Size = new float2(WIDTH, DeckPanel.HEIGHT);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));
        var centre = position.X + WIDTH / 2;
        var top = position.Y + PAD;

        Place(_heading, centre - _heading.Size.X / 2, top - 2);

        // Each channel's meter on the outside, its fader inside, its letter over them; the left deck's on the
        // left.
        var stripsY = top + 20;
        for (var i = 0; i < _channels.Count; i++)
        {
            var (_, name, meter, fader) = _channels[i];
            var onLeft = i < (_channels.Count + 1) / 2;
            var meterX = onLeft ? position.X + PAD + 6 : position.X + WIDTH - PAD - 6 - meter.Size.X;
            var faderX = onLeft ? meterX + meter.Size.X + 10 : meterX - 10 - fader.Size.X;
            Place(meter, meterX, stripsY);
            Place(fader, faderX, stripsY);
            var columnLeft = MathF.Min(meterX, faderX);
            var columnRight = MathF.Max(meterX + meter.Size.X, faderX + fader.Size.X);
            Place(name, (columnLeft + columnRight - name.Size.X) / 2, top - 4);
        }

        Place(_autoGain, centre - _autoGain.Size.X / 2, stripsY + 24);

        var crossY = position.Y + DeckPanel.HEIGHT - PAD - _crossfader.Size.Y;
        Place(_crossfader, centre - _crossfader.Size.X / 2, crossY);
        Place(_crossLeft, centre - _crossfader.Size.X / 2 - 8 - _crossLeft.Size.X, crossY + (_crossfader.Size.Y - _crossLeft.Size.Y) / 2);
        Place(_crossRight, centre + _crossfader.Size.X / 2 + 8, crossY + (_crossfader.Size.Y - _crossRight.Size.Y) / 2);
    }
}
