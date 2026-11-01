using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// The mixer between the decks, laid out as a DJ mixer is. Down the middle, the names of each channel's EQ and
/// filter, with each deck's controls on its own side: bass, mids and treble from killed on the left, through
/// unchanged in the middle, to boosted on the right, and the filter, a low-pass to the left and a high-pass to the
/// right. Double-click any of them to put it back in the middle. At the outer edges, each deck's level as it plays
/// and its volume fader; the crossfader across the bottom; and a switch for automatic gain. All in each deck's colour.
/// Call <see cref="Refresh"/> once a frame.
/// </summary>
sealed class MixerPanel : Panel
{
    public const float WIDTH = 340;
    const float PAD = 10;
    const float ROW = 24;
    const float EQ_LENGTH = 72;
    const float LABEL_COLUMN = 48;

    /// <summary>
    /// A channel's controls: its EQ and filter, top to bottom as they're labelled, then its meter and fader.
    /// </summary>
    sealed record Channel(Deck Deck, Label Name, Slider[] Eq, LevelMeter Meter, Slider Fader);

    static readonly string[] EqNames = ["HIGH", "MID", "LOW", "FILTER"];

    readonly AudioEngine _engine;
    readonly Label _heading;
    readonly Label[] _eqLabels;
    readonly List<Channel> _channels = [];
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
        _eqLabels = EqNames.Select(name => Add(new Label(small, name) { Color = Theme.Muted })).ToArray();

        foreach (var deck in decks)
        {
            var color = Theme.DeckColor(deck.Name);
            var name = Add(new Label(bold, deck.Name) { Color = color });

            var eq = new Slider[EqNames.Length];
            for (var row = 0; row < eq.Length; row++)
            {
                eq[row] = Add(
                    new Slider
                    {
                        Min = -1,
                        Max = 1,
                        DefaultValue = 0,
                        Length = EQ_LENGTH,
                        Thickness = 16,
                        TrackThickness = 4,
                        HandleLength = 8,
                        ShowFill = true,
                        FillFrom = 0,
                        FillColor = color,
                        FocusColor = Theme.Focus,
                    }
                );
                eq[row].Marks.Add(0);
                var band = row;
                eq[row].Changed += slider => SetEq(deck.Equalizer, band, slider.Value);
            }

            var meter = Add(new LevelMeter(2) { Length = 160, BarThickness = 4, BarGap = 2, Segments = 32 });

            // A channel fader: up is louder, and double-clicking puts it back to full.
            var fader = Add(
                new Slider
                {
                    Orientation = Orientation.Vertical,
                    Length = 160,
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
            _channels.Add(new Channel(deck, name, eq, meter, fader));
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

        _autoGain = Add(new Button(small, "AUTO GAIN") { FocusColor = Theme.Focus, MinSize = new float2(90, 24) });
        _autoGain.Clicked += _ => engine.AutoGain = !engine.AutoGain;

        Refresh();
    }

    static void SetEq(Equalizer equalizer, int row, float value)
    {
        switch (row)
        {
            case 0:
                equalizer.High = value;
                break;
            case 1:
                equalizer.Mid = value;
                break;
            case 2:
                equalizer.Low = value;
                break;
            default:
                equalizer.Filter = value;
                break;
        }
    }

    static float GetEq(Equalizer equalizer, int row) =>
        row switch
        {
            0 => equalizer.High,
            1 => equalizer.Mid,
            2 => equalizer.Low,
            _ => equalizer.Filter,
        };

    public void Refresh()
    {
        foreach (var channel in _channels)
        {
            var (left, right) = channel.Deck.Meter.TakePeaks();
            channel.Meter.Push(left, right);
            if (!channel.Fader.IsDragging)
                channel.Fader.Value = channel.Deck.Volume;
            for (var row = 0; row < channel.Eq.Length; row++)
            {
                if (!channel.Eq[row].IsDragging)
                    channel.Eq[row].Value = GetEq(channel.Deck.Equalizer, row);
            }
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

        Place(_heading, centre - _heading.Size.X / 2, top - 4);

        // The EQ's names down the middle, each deck's controls either side of them, its meter and fader out at the
        // edge with its letter over them; the left deck's on the left.
        var eqTop = top + 18;
        for (var row = 0; row < _eqLabels.Length; row++)
            Place(_eqLabels[row], centre - _eqLabels[row].Size.X / 2, eqTop + row * ROW + (16 - _eqLabels[row].Size.Y) / 2);

        var stripsY = top + 18;
        for (var i = 0; i < _channels.Count; i++)
        {
            var channel = _channels[i];
            var onLeft = i < (_channels.Count + 1) / 2;
            var eqX = onLeft ? centre - LABEL_COLUMN / 2 - 6 - EQ_LENGTH : centre + LABEL_COLUMN / 2 + 6;
            for (var row = 0; row < channel.Eq.Length; row++)
                Place(channel.Eq[row], eqX, eqTop + row * ROW);

            var faderX = onLeft ? position.X + PAD : position.X + WIDTH - PAD - channel.Fader.Size.X;
            var meterX = onLeft ? faderX + channel.Fader.Size.X + 6 : faderX - 6 - channel.Meter.Size.X;
            Place(channel.Fader, faderX, stripsY);
            Place(channel.Meter, meterX, stripsY);
            var columnLeft = MathF.Min(meterX, faderX);
            var columnRight = MathF.Max(meterX + channel.Meter.Size.X, faderX + channel.Fader.Size.X);
            Place(channel.Name, (columnLeft + columnRight - channel.Name.Size.X) / 2, top - 6);
        }

        Place(_autoGain, centre - _autoGain.Size.X / 2, eqTop + _eqLabels.Length * ROW + 4);

        var crossY = position.Y + DeckPanel.HEIGHT - PAD - _crossfader.Size.Y;
        Place(_crossfader, centre - _crossfader.Size.X / 2, crossY);
        Place(_crossLeft, centre - _crossfader.Size.X / 2 - 8 - _crossLeft.Size.X, crossY + (_crossfader.Size.Y - _crossLeft.Size.Y) / 2);
        Place(_crossRight, centre + _crossfader.Size.X / 2 + 8, crossY + (_crossfader.Size.Y - _crossRight.Size.Y) / 2);
    }
}
