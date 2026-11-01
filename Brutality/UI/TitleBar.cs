using System.Diagnostics;
using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

enum TitleIcon
{
    Menu,
    Minimize,
    Maximize,
    Restore,
    Close,
}

/// <summary>
/// A flat button with a drawn icon, for the title bar: the options menu and the window's own buttons.
/// </summary>
sealed class IconButton(TitleIcon icon) : Element
{
    public TitleIcon Icon { get; set; } = icon;
    public float2 FixedSize { get; set; } = new(46, 34);
    public float4 IconColor { get; set; } = Theme.Text;
    public float4 HoverBackground { get; set; } = Colors.Hex(0xFFFFFF, 0.1f);
    public float4 PressedBackground { get; set; } = Colors.Hex(0xFFFFFF, 0.05f);

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    public override float2 Measure() => Size = FixedSize;

    public override void Draw(Batch batch)
    {
        if (IsHovered || IsPressed)
            batch.AddRect(ScreenPosition, Size, IsPressed && IsHovered ? PressedBackground : HoverBackground);

        // A 10 pixel icon, on whole pixels so its lines stay crisp.
        var x = MathF.Round(ScreenPosition.X + Size.X / 2 - 5);
        var y = MathF.Round(ScreenPosition.Y + Size.Y / 2 - 5);
        switch (Icon)
        {
            case TitleIcon.Menu:
                for (var i = 0; i < 3; i++)
                    batch.AddRect(new float2(x - 1, y + 1 + i * 4), new float2(12, 1), IconColor);
                break;
            case TitleIcon.Minimize:
                batch.AddRect(new float2(x, y + 5), new float2(10, 1), IconColor);
                break;
            case TitleIcon.Maximize:
                batch.AddBorder(new float2(x, y), new float2(10, 10), 1, IconColor);
                break;
            case TitleIcon.Restore:
                batch.AddBorder(new float2(x, y + 2), new float2(8, 8), 1, IconColor);
                batch.AddRect(new float2(x + 2, y), new float2(8, 1), IconColor);
                batch.AddRect(new float2(x + 9, y), new float2(1, 8), IconColor);
                break;
            case TitleIcon.Close:
                for (var i = 0; i < 10; i++)
                {
                    batch.AddRect(new float2(x + i, y + i), new float2(1, 1), IconColor);
                    batch.AddRect(new float2(x + 9 - i, y + i), new float2(1, 1), IconColor);
                }
                break;
        }
    }
}

/// <summary>
/// Where each deck is in the bar, one deck to a row, stacked: its letter, then four boxes, one per beat, with the
/// current one lit in the deck's colour. Dim while paused, and empty until the deck's song has a beat grid.
/// </summary>
sealed class BeatTrackers(IReadOnlyList<Deck> decks, Font font) : Element
{
    const float BOX = 14;
    const float GAP = 3;
    const float ROW = 9;
    const float ROW_GAP = 5;
    const float LETTER = 14;

    public override float2 Measure() =>
        Size = new float2(LETTER + 4 * BOX + 3 * GAP, decks.Count * ROW + Math.Max(0, decks.Count - 1) * ROW_GAP);

    /// <summary>
    /// How tall <paramref name="font"/>'s capitals stand above the baseline, from its M.
    /// </summary>
    public static float CapHeight(Font font) => font.GetGlyph('M').Bearing.Y;

    public override void Draw(Batch batch)
    {
        for (var row = 0; row < decks.Count; row++)
        {
            var deck = decks[row];
            var color = Theme.DeckColor(deck.Name);
            var top = ScreenPosition.Y + row * (ROW + ROW_GAP);

            // The letter's capitals centred on the row, rather than its line box.
            batch.AddText(font, deck.Name, new float2(ScreenPosition.X, MathF.Round(top + ROW / 2 + CapHeight(font) / 2 - font.Ascender)), color);

            var lit = -1;
            if (deck.Analysis?.Grid is { } grid && deck.IsLoaded)
                lit = (int)(((long)Math.Floor(grid.BeatNumberAt(deck.Position.TotalSeconds)) % 4 + 4) % 4);

            for (var i = 0; i < 4; i++)
            {
                var on = i == lit ? (deck.IsPlaying ? color : Theme.Fade(color, 0.45f)) : Theme.Fade(color, 0.14f);
                batch.AddRect(new float2(ScreenPosition.X + LETTER + i * (BOX + GAP), top), new float2(BOX, ROW), on);
            }
        }
    }
}

/// <summary>
/// The window's own title bar: the app's name, each deck's place in the bar stacked beside the master level, the
/// CPU and the time, the options button and the window's buttons. Its empty space is where the window can be
/// dragged from (see <see cref="IsCaption"/>). Call <see cref="Update"/> once a frame.
/// </summary>
sealed class TitleBar : Panel
{
    public const float HEIGHT = 34;

    readonly AudioEngine _engine;
    readonly Panel _left;
    readonly Panel _centre;
    readonly Panel _right;
    readonly LevelMeter _master;
    readonly Label _masterLabel;
    readonly Label _cpu;
    readonly Label _clock;
    readonly IconButton _maximize;
    readonly Process _process = Process.GetCurrentProcess();
    readonly Stopwatch _sinceCpu = Stopwatch.StartNew();
    TimeSpan _cpuBefore;

    public float Width { get; set; } = 1280;

    /// <summary>
    /// Whether the window is maximised, which the middle button shows and toggles.
    /// </summary>
    public Func<bool> IsMaximized { get; set; } = () => false;

    public event Action? OptionsClicked;
    public event Action? MinimizeClicked;
    public event Action? MaximizeClicked;
    public event Action? CloseClicked;

    public TitleBar(AudioEngine engine, IReadOnlyList<Deck> decks, Font small, Font body, Font bold)
    {
        _engine = engine;
        Background = Theme.TitleBar;

        _left = Add(new Panel { Direction = Direction.Horizontal, Spacing = 10 });
        _left.Add(new Badge(bold, "C", Theme.Accent, Theme.TitleBar, 22));
        _left.Add(new Label(bold, "Cuepoint") { Color = Theme.Text });

        // The decks' beat trackers, A over B, then a gap, then the master level and its name.
        _centre = Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        _centre.Add(new BeatTrackers(decks, small));
        _centre.Add(new Spacer(20));
        _masterLabel = _centre.Add(new Label(small, "MASTER") { Color = Theme.Muted });
        _master = _centre.Add(new LevelMeter(2) { Orientation = Orientation.Horizontal, Length = 160, BarThickness = 4, BarGap = 2, Segments = 32 });

        _right = Add(new Panel { Direction = Direction.Horizontal, Spacing = 0 });
        _cpu = _right.Add(new Label(body) { Color = Theme.Muted });
        _right.Add(new Spacer(16));
        _clock = _right.Add(new Label(body) { Color = Theme.Text });
        _right.Add(new Spacer(12));
        var options = _right.Add(new IconButton(TitleIcon.Menu));
        options.Clicked += _ => OptionsClicked?.Invoke();
        _right.Add(new Spacer(8));
        var minimize = _right.Add(new IconButton(TitleIcon.Minimize));
        minimize.Clicked += _ => MinimizeClicked?.Invoke();
        _maximize = _right.Add(new IconButton(TitleIcon.Maximize));
        _maximize.Clicked += _ => MaximizeClicked?.Invoke();
        var close = _right.Add(new IconButton(TitleIcon.Close) { HoverBackground = Colors.Hex(0xE81123), PressedBackground = Colors.Hex(0xF1707A) });
        close.Clicked += _ => CloseClicked?.Invoke();

        _cpuBefore = _process.TotalProcessorTime;
        _cpu.Text = "CPU –";
        Update();
    }

    /// <summary>
    /// Whether the window can be dragged from <paramref name="point"/>: inside the bar, but not on a button.
    /// </summary>
    public bool IsCaption(float2 point) => Contains(point) && HitTest(point) is null;

    public void Update()
    {
        _clock.Text = DateTime.Now.ToString("HH:mm:ss");
        _maximize.Icon = IsMaximized() ? TitleIcon.Restore : TitleIcon.Maximize;
        var (left, right) = _engine.Meter.TakePeaks();
        _master.Push(left, right);

        // The process's share of the whole machine, over the last second.
        if (_sinceCpu.Elapsed >= TimeSpan.FromSeconds(1))
        {
            _process.Refresh();
            var used = _process.TotalProcessorTime - _cpuBefore;
            var share = used.TotalSeconds / (_sinceCpu.Elapsed.TotalSeconds * Environment.ProcessorCount);
            _cpu.Text = $"CPU {share * 100:F0}%";
            _cpuBefore = _process.TotalProcessorTime;
            _sinceCpu.Restart();
        }
    }

    public override float2 Measure()
    {
        MeasureChildren();
        return Size = new float2(Width, HEIGHT);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        float Middle(Element child) => MathF.Round(position.Y + (HEIGHT - child.Size.Y) / 2);
        _left.Arrange(new float2(position.X + 10, Middle(_left)));
        _centre.Arrange(new float2(MathF.Round(position.X + (Width - _centre.Size.X) / 2), Middle(_centre)));
        _right.Arrange(new float2(position.X + Width - _right.Size.X, Middle(_right)));
        AlignMiddle(_left);
        AlignMiddle(_centre);
        AlignMiddle(_right);

        // "MASTER" centred by its capitals rather than its line box, which has room for descenders below, so it
        // lines up with the meter beside it.
        var meterMiddle = _master.ScreenPosition.Y + _master.Size.Y / 2;
        var font = _masterLabel.Font;
        _masterLabel.Arrange(new float2(_masterLabel.ScreenPosition.X, MathF.Round(meterMiddle + BeatTrackers.CapHeight(font) / 2 - font.Ascender)));
    }

    /// <summary>
    /// Centres a row's children on its middle, since a row lines them up along the top.
    /// </summary>
    static void AlignMiddle(Panel row)
    {
        foreach (var child in row.Children)
            child.Arrange(new float2(child.ScreenPosition.X, MathF.Round(row.ScreenPosition.Y + (row.Size.Y - child.Size.Y) / 2)));
    }

    public override void Draw(Batch batch)
    {
        DrawFrame(batch);
        batch.AddRect(ScreenPosition + new float2(0, HEIGHT - 1), new float2(Width, 1), Theme.PanelBorder);
        DrawChildren(batch);
    }
}

/// <summary>
/// Empty space of a set width (or height, in a column), to push things apart in a row.
/// </summary>
sealed class Spacer(float length) : Element
{
    public override float2 Measure() => Size = new float2(length, length);

    public override void Draw(Batch batch) { }
}
