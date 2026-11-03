using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// The mixer between the decks, laid out as a DJ mixer is. Down the middle, the names of each channel's trim, EQ and
/// filter, with each deck's knobs for them on its own side: the trim, from quieter turned left through unchanged at
/// the top to louder turned right; bass, mids and treble from killed (lit red) through unchanged to boosted; and the
/// filter, a low-pass to the left and a high-pass to the right. Double-click any of them to put it back to the top.
/// At the outer edges, each deck's level coming into its channel and its volume fader, with its PFL (send it to the
/// headphones) at the top beside the middle; and the crossfader across the bottom. All in each deck's colour. The
/// headphones' own settings are on the title bar, and automatic gain in the options. Call <see cref="Refresh"/> once
/// a frame.
/// </summary>
sealed class MixerPanel : Panel
{
    public const float WIDTH = 216;
    const float PAD = 10;
    const float KNOB = 26;
    const float ROW = 26;
    const float LABEL_COLUMN = 44;
    const float FADER_LENGTH = 128;

    /// <summary>
    /// A channel's controls: its trim, EQ and filter, top to bottom as they're labelled, then its meter and fader.
    /// </summary>
    sealed record Channel(Deck Deck, Label Name, Knob[] Eq, LevelMeter Meter, Slider Fader, Button Pfl);

    static readonly string[] EqNames = ["TRIM", "HIGH", "MID", "LOW", "FILTER"];

    static readonly string[] EqTooltips =
    [
        "Trim: the channel's level before its EQ and fader. Drag up for louder. Double-click for none",
        "Treble: drag up to boost, down to take it out. Double-click for flat",
        "Mids: drag up to boost, down to take them out. Double-click for flat",
        "Bass: drag up to boost, down to take it out. Double-click for flat",
        "Filter: turn left for low-pass, right for high-pass. Double-click for off",
    ];

    readonly AudioEngine _engine;
    readonly Label[] _eqLabels;
    readonly List<Channel> _channels = [];
    readonly Slider _crossfader;
    readonly Label _crossLeft;
    readonly Label _crossRight;

    /// <param name="pflKeys">The key for each deck's PFL button, in order, to label them with.</param>
    public MixerPanel(AudioEngine engine, IReadOnlyList<Deck> decks, Font small, Font bold, Images images, IReadOnlyList<string?>? pflKeys = null)
    {
        _engine = engine;
        Background = Theme.Panel;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;

        _eqLabels = EqNames.Select(name => Add(new Label(small, name) { Color = Theme.Muted })).ToArray();

        foreach (var deck in decks)
        {
            var color = Theme.DeckColor(deck.Name);
            var name = Add(new Label(bold, deck.Name) { Color = color });

            var eq = new Knob[EqNames.Length];
            for (var row = 0; row < eq.Length; row++)
            {
                eq[row] = Add(
                    new Knob(images.Knob, images.Dot)
                    {
                        Min = -1,
                        Max = 1,
                        DefaultValue = 0,
                        FillFrom = 0,
                        Diameter = KNOB,
                        FillColor = color,
                        // A band turned all the way down is killed, and says so.
                        MinColor = row is >= 1 and <= 3 ? Theme.Warning : null,
                        FocusColor = Theme.Focus,
                        Tooltip = EqTooltips[row],
                    }
                );
                var band = row;
                eq[row].Changed += knob => SetEq(deck, band, knob.Value);
            }

            var meter = Add(new LevelMeter(2) { Length = FADER_LENGTH, BarThickness = 4, BarGap = 2, Segments = 26 });

            // A channel fader: up is louder, and double-clicking puts it back to full.
            var fader = Add(
                new Slider
                {
                    Orientation = Orientation.Vertical,
                    Length = FADER_LENGTH,
                    Thickness = 22,
                    HandleLength = 12,
                    DefaultValue = 1,
                    Value = deck.Volume,
                    FillColor = color,
                    ShowFill = true,
                    FocusColor = Theme.Focus,
                    Tooltip = $"Deck {deck.Name}'s volume. Double-click for full",
                }
            );
            fader.Changed += slider => deck.Volume = slider.Value;

            var pfl = Add(
                new Button(small, "PFL")
                {
                    FocusColor = Theme.Focus,
                    MinSize = new float2(44, 20),
                    Padding = 3,
                    KeyHint = pflKeys?.ElementAtOrDefault(_channels.Count),
                    HintFont = small,
                    Tooltip = $"Hear deck {deck.Name} in the headphones",
                }
            );
            pfl.Clicked += _ => deck.Headphones = !deck.Headphones;
            _channels.Add(new Channel(deck, name, eq, meter, fader, pfl));
        }

        _crossLeft = Add(new Label(bold, decks.Count > 0 ? decks[0].Name : "") { Color = decks.Count > 0 ? Theme.DeckColor(decks[0].Name) : Theme.Muted });
        _crossRight = Add(new Label(bold, decks.Count > 1 ? decks[^1].Name : "") { Color = decks.Count > 1 ? Theme.DeckColor(decks[^1].Name) : Theme.Muted });
        _crossfader = Add(
            new Slider
            {
                Length = WIDTH - 2 * PAD - 44,
                Thickness = 22,
                HandleLength = 12,
                DefaultValue = 0.5f,
                Value = engine.Crossfader,
                FocusColor = Theme.Focus,
                Tooltip = "Crossfader: from one deck to the other. Double-click for the middle",
            }
        );
        _crossfader.Marks.Add(0.5f);
        _crossfader.Changed += slider => engine.Crossfader = slider.Value;

        Refresh();
    }

    static void SetEq(Deck deck, int row, float value)
    {
        switch (row)
        {
            case 0:
                deck.Trim = value;
                break;
            case 1:
                deck.Equalizer.High = value;
                break;
            case 2:
                deck.Equalizer.Mid = value;
                break;
            case 3:
                deck.Equalizer.Low = value;
                break;
            default:
                deck.Equalizer.Filter = value;
                break;
        }
    }

    static float GetEq(Deck deck, int row) =>
        row switch
        {
            0 => deck.Trim,
            1 => deck.Equalizer.High,
            2 => deck.Equalizer.Mid,
            3 => deck.Equalizer.Low,
            _ => deck.Equalizer.Filter,
        };

    /// <summary>
    /// The knob for <paramref name="row"/> (0 the trim, down to 4 the filter) on the channel for deck number
    /// <paramref name="deck"/>, for tests.
    /// </summary>
    internal Knob KnobFor(int deck, int row) => _channels[deck].Eq[row];

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
                    channel.Eq[row].Value = GetEq(channel.Deck, row);
            }

            var color = Theme.DeckColor(channel.Deck.Name);
            var cued = channel.Deck.Headphones;
            channel.Pfl.Background = cued ? Theme.Mix(Theme.PanelRaised, color, 0.6f) : Colors.Hex(0x2A2A35);
            channel.Pfl.HoverBackground = cued ? Theme.Mix(Theme.PanelRaised, color, 0.75f) : Colors.Hex(0x3A3A48);
            channel.Pfl.TextColor = cued ? Colors.White : Theme.Muted;
        }
        if (!_crossfader.IsDragging)
            _crossfader.Value = _engine.Crossfader;
    }

    /// <summary>
    /// As tall as the decks either side, which grow when they're too narrow for their buttons in one row.
    /// </summary>
    public float Height { get; set; } = DeckPanel.HEIGHT;

    public override float2 Measure()
    {
        foreach (var child in Children)
            child.Measure();
        return Size = new float2(WIDTH, Height);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        void Place(Element child, float x, float y) => child.Arrange(new float2(MathF.Round(x), MathF.Round(y)));
        var centre = position.X + WIDTH / 2;
        var top = position.Y + PAD;

        // The EQ's names down the middle, each deck's knobs either side of them, its meter and fader out at the edge
        // with its letter over them, and its PFL at the top beside the middle; the left deck's on the left.
        var eqTop = top + 18;
        for (var row = 0; row < _eqLabels.Length; row++)
            Place(_eqLabels[row], centre - _eqLabels[row].Size.X / 2, eqTop + row * ROW + (KNOB - _eqLabels[row].Size.Y) / 2);

        var stripsY = top + 16;
        for (var i = 0; i < _channels.Count; i++)
        {
            var channel = _channels[i];
            var onLeft = i < (_channels.Count + 1) / 2;
            var knobX = onLeft ? centre - LABEL_COLUMN / 2 - 2 - KNOB : centre + LABEL_COLUMN / 2 + 2;
            for (var row = 0; row < channel.Eq.Length; row++)
                Place(channel.Eq[row], knobX, eqTop + row * ROW);

            var faderX = onLeft ? position.X + PAD : position.X + WIDTH - PAD - channel.Fader.Size.X;
            var meterX = onLeft ? faderX + channel.Fader.Size.X + 6 : faderX - 6 - channel.Meter.Size.X;
            Place(channel.Fader, faderX, stripsY);
            Place(channel.Meter, meterX, stripsY);
            var columnLeft = MathF.Min(meterX, faderX);
            var columnRight = MathF.Max(meterX + channel.Meter.Size.X, faderX + channel.Fader.Size.X);
            Place(channel.Name, (columnLeft + columnRight - channel.Name.Size.X) / 2, top - 6);
            Place(channel.Pfl, onLeft ? centre - 3 - channel.Pfl.Size.X : centre + 3, top - 6);
        }

        var crossY = position.Y + Height - PAD - _crossfader.Size.Y;
        Place(_crossfader, centre - _crossfader.Size.X / 2, crossY);
        Place(_crossLeft, centre - _crossfader.Size.X / 2 - 8 - _crossLeft.Size.X, crossY + (_crossfader.Size.Y - _crossLeft.Size.Y) / 2);
        Place(_crossRight, centre + _crossfader.Size.X / 2 + 8, crossY + (_crossfader.Size.Y - _crossRight.Size.Y) / 2);
    }
}
