using System.Diagnostics;
using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.UI;

enum TitleIcon
{
    /// <summary>
    /// A picture, the button's <see cref="IconButton.Picture"/>: the options button's cog.
    /// </summary>
    Picture,
    Minimize,
    Maximize,
    Restore,
    Close,
}

/// <summary>
/// A flat button with an icon, for the title bar: the options' cog and the window's own buttons.
/// </summary>
sealed class IconButton(TitleIcon icon) : Element
{
    public TitleIcon Icon { get; set; } = icon;

    /// <summary>
    /// The picture for <see cref="TitleIcon.Picture"/>, white for tinting with <see cref="IconColor"/>.
    /// </summary>
    public Image? Picture { get; init; }
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
            case TitleIcon.Picture when Picture is { } picture:
                batch.AddImage(picture, ScreenPosition + (Size - new float2(picture.Size.X, picture.Size.Y)) / 2, IconColor);
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
    const float BOX = 21;
    const float GAP = 3;
    const float ROW = 9;
    const float ROW_GAP = 5;
    const float LETTER = 14;

    /// <summary>
    /// How far in from a box's edges the beat's block sits.
    /// </summary>
    const float INSET = 2;

    /// <summary>
    /// Where <paramref name="seconds"/> is: its beat in the bar, and its bar in four, both from 0, counting bars from
    /// the song's first (the one holding its first beat), as lighting software is told them.
    /// </summary>
    public static (int Beat, int Bar) Place(BeatGrid grid, double seconds)
    {
        var beat = (long)Math.Floor(grid.BeatNumberAt(seconds) + 1e-6);
        var first = (long)Math.Floor(grid.BeatNumberAt(grid.FirstBeat) + 1e-6);
        var firstBar = first - grid.BarPosition(first);
        // Rounded down, so the bars before the first count back 3, 2, 1, 0 as well.
        var sinceFirst = beat - firstBar;
        var bar = sinceFirst >= 0 ? sinceFirst / BeatGrid.BEATS_PER_BAR : (sinceFirst - BeatGrid.BEATS_PER_BAR + 1) / BeatGrid.BEATS_PER_BAR;
        return (grid.BarPosition(beat), (int)(((bar % 4) + 4) % 4));
    }

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

            var (beat, bar) = (-1, -1);
            if (deck.Analysis?.Grid is { } grid && deck.IsLoaded)
                (beat, bar) = Place(grid, deck.Position.TotalSeconds);

            // Four black boxes: the bar (of four) filled with the deck's colour, and the beat (of four) as a smaller
            // block of colour inside its box, which in the bar's own box is just the full colour.
            var lit = deck.IsPlaying ? color : Theme.Fade(color, 0.45f);
            for (var i = 0; i < 4; i++)
            {
                var at = new float2(ScreenPosition.X + LETTER + i * (BOX + GAP), top);
                batch.AddRect(at, new float2(BOX, ROW), i == bar ? lit : Colors.Hex(0x000000));
                if (i == beat && i != bar)
                    batch.AddRect(at + new float2(INSET, INSET), new float2(BOX - 2 * INSET, ROW - 2 * INSET), lit);
            }
        }
    }
}

/// <summary>
/// "OS2L" in a box, for whether lighting software is listening: black on green while it's connected, and orange on
/// black, pulsing, while it's still being looked for.
/// </summary>
sealed class Os2lIndicator(Font font) : Element
{
    const float PADDING = 6;
    const float HEIGHT = 20;

    /// <summary>
    /// How long one pulse takes, in seconds, while there's no connection.
    /// </summary>
    const double PULSE_SECONDS = 1.2;

    const string TEXT = "OS2L";

    public bool IsConnected { get; set; }

    public override float2 Measure() => Size = new float2(MathF.Ceiling(font.Measure(TEXT).X) + 2 * PADDING, HEIGHT);

    public override void Draw(Batch batch)
    {
        // Brightening and dimming smoothly, never quite out, so it's always readable.
        var pulse = 0.5 + 0.5 * Math.Cos(2 * Math.PI * Clock.Seconds() / PULSE_SECONDS);
        var (background, text) = IsConnected
            ? (Theme.Connected, Colors.Hex(0x000000))
            : (Colors.Hex(0x000000), Theme.Fade(Theme.Accent, (float)(0.3 + 0.7 * pulse)));

        batch.AddRect(ScreenPosition, Size, background);
        // The capitals centred in the box, rather than the line box with room for descenders.
        var x = MathF.Round(ScreenPosition.X + PADDING);
        var y = MathF.Round(ScreenPosition.Y + HEIGHT / 2 + BeatTrackers.CapHeight(font) / 2 - font.Ascender);
        batch.AddText(font, TEXT, new float2(x, y), text);
    }
}

/// <summary>
/// The title bar's record button: a dot and REC, grey while the mix isn't being recorded, and red with how long it's
/// been going while it is.
/// </summary>
sealed class RecordButton(Font font) : Element
{
    const float DOT = 10;
    const float GAP = 6;
    const float PADDING = 8;
    const float HEIGHT = 26;

    /// <summary>
    /// How long the mix has been recorded for; null while it isn't.
    /// </summary>
    public TimeSpan? Recording { get; set; }

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;

    // Wide enough for hours, so the button doesn't grow and shuffle the title bar as the time ticks on.
    public override float2 Measure() => Size = new float2(MathF.Ceiling(PADDING + DOT + GAP + font.Measure("REC 0:00:00").X + PADDING), HEIGHT);

    static string Text(TimeSpan? recording) =>
        recording is not { } time ? "REC"
        : time.TotalHours >= 1 ? $"REC {(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"REC {time.Minutes}:{time.Seconds:00}";

    public override void Draw(Batch batch)
    {
        if (IsHovered || IsPressed)
            batch.AddRect(ScreenPosition, Size, Colors.Hex(0xFFFFFF, IsPressed && IsHovered ? 0.05f : 0.1f));

        var color = Recording is null ? Theme.Muted : Theme.Warning;
        var middle = ScreenPosition.Y + HEIGHT / 2;

        // A round dot, a row of pixels at a time.
        var left = MathF.Round(ScreenPosition.X + PADDING);
        var top = MathF.Round(middle - DOT / 2);
        const float radius = DOT / 2;
        for (var row = 0; row < DOT; row++)
        {
            var y = row + 0.5f - radius;
            var half = MathF.Round(MathF.Sqrt(MathF.Max(0, radius * radius - y * y)));
            batch.AddRect(new float2(left + radius - half, top + row), new float2(2 * half, 1), color);
        }

        // The text's capitals centred on the button, as the title bar's other text is.
        var x = left + DOT + GAP;
        var textY = MathF.Round(middle + BeatTrackers.CapHeight(font) / 2 - font.Ascender);
        batch.AddText(font, Text(Recording), new float2(x, textY), color);
    }
}

/// <summary>
/// The window's own title bar: the app's name, each deck's place in the bar stacked beside the master level, whether
/// lighting software is connected, the CPU and the time, the options button and the window's buttons. Its empty space is where the window can be
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
    readonly Label _limit;

    /// <summary>
    /// How much the limiter has to turn the mix down, as a gain, before its light comes on: about half a decibel,
    /// so it only shows working that can be heard.
    /// </summary>
    const float LIMIT_LIGHT_GAIN = 0.944f;

    /// <summary>
    /// How long the light stays on after the limiter last worked, in seconds, so a moment's limiting is seen.
    /// </summary>
    const double LIMIT_LIGHT_HOLD = 0.5;

    double _limitedAt = double.NegativeInfinity;
    readonly Os2lIndicator _os2l;
    readonly RecordButton _record;
    readonly Label _audioWarning;
    readonly Spacer _audioWarningGap;
    readonly Spacer _os2lGap;
    readonly Label _cpu;
    readonly Label _clock;
    readonly IconButton _maximize;
    readonly IconButton _headphones;
    readonly Process _process = Process.GetCurrentProcess();
    readonly Stopwatch _sinceCpu = Stopwatch.StartNew();
    TimeSpan _cpuBefore;

    public float Width { get; set; } = 1280;

    /// <summary>
    /// Whether the window is maximised, which the middle button shows and toggles.
    /// </summary>
    public Func<bool> IsMaximized { get; set; } = () => false;

    /// <summary>
    /// Whether lighting software is connected by OS2L; null while OS2L is off, which hides its indicator.
    /// </summary>
    public Func<bool?> Os2lConnected { get; set; } = () => null;

    /// <summary>
    /// How long the mix has been recorded for, which the record button shows; null while it isn't being recorded.
    /// </summary>
    public Func<TimeSpan?> Recording { get; set; } = () => null;

    /// <summary>
    /// The record button was clicked, to start recording the mix or to stop.
    /// </summary>
    public event Action? RecordClicked;

    /// <summary>
    /// A sound card chosen that's gone missing, as "DDJ-SB3 missing", to warn of in red; null while all's well.
    /// </summary>
    public Func<string?> AudioWarning { get; set; } = () => null;

    public event Action? OptionsClicked;

    /// <summary>
    /// The headphones button was clicked, to show or put away their settings below it.
    /// </summary>
    public event Action? HeadphonesClicked;

    /// <summary>
    /// Whether any deck's being heard in the headphones, which lights their button.
    /// </summary>
    public Func<bool> Cueing { get; set; } = () => false;

    /// <summary>
    /// The bottom right corner of the headphones button, where their settings drop down from.
    /// </summary>
    public float2 HeadphonesAnchor => _headphones.ScreenPosition + _headphones.Size;
    public event Action? MinimizeClicked;
    public event Action? MaximizeClicked;
    public event Action? CloseClicked;

    public TitleBar(AudioEngine engine, IReadOnlyList<Deck> decks, Font small, Font body, Font bold, Images images)
    {
        _engine = engine;
        Background = Theme.Panel;

        _left = Add(new Panel { Direction = Direction.Horizontal, Spacing = 10 });
        _left.Add(new Picture(images.Logo));
        _left.Add(new Label(bold, "Cuepoint") { Color = Theme.Text });

        // The decks' beat trackers, A over B, then a gap, then the master level and its name.
        _centre = Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        _centre.Add(new BeatTrackers(decks, small));
        _centre.Add(new Spacer(20));
        _masterLabel = _centre.Add(new Label(small, "MASTER") { Color = Theme.Muted });
        _master = _centre.Add(new LevelMeter(2) { Orientation = Orientation.Horizontal, Length = 160, BarThickness = 4, BarGap = 2, Segments = 32 });
        // Lit while the master limiter is turning the mix down: a sign the decks are too loud together.
        _limit = _centre.Add(new Label(small, "LIMIT") { Color = Theme.Faint });

        _right = Add(new Panel { Direction = Direction.Horizontal, Spacing = 0 });
        _audioWarning = _right.Add(new Label(bold) { Color = Theme.Warning, Visible = false });
        _audioWarningGap = _right.Add(new Spacer(16) { Visible = false });
        _record = _right.Add(new RecordButton(body));
        _record.Clicked += _ => RecordClicked?.Invoke();
        _right.Add(new Spacer(12));
        _os2l = _right.Add(new Os2lIndicator(bold) { Visible = false });
        _os2lGap = _right.Add(new Spacer(16) { Visible = false });
        _cpu = _right.Add(new Label(body) { Color = Theme.Muted });
        _right.Add(new Spacer(16));
        _clock = _right.Add(new Label(body) { Color = Theme.Text });
        _right.Add(new Spacer(12));
        _headphones = _right.Add(new IconButton(TitleIcon.Picture) { Picture = images.Headphones, Tooltip = "Headphones: what they hear, and how loud" });
        _headphones.Clicked += _ => HeadphonesClicked?.Invoke();
        var options = _right.Add(new IconButton(TitleIcon.Picture) { Picture = images.Cog, Tooltip = "Options" });
        options.Clicked += _ => OptionsClicked?.Invoke();
        _right.Add(new Spacer(8));
        var minimize = _right.Add(new IconButton(TitleIcon.Minimize) { Tooltip = "Minimise" });
        minimize.Clicked += _ => MinimizeClicked?.Invoke();
        _maximize = _right.Add(new IconButton(TitleIcon.Maximize));
        _maximize.Clicked += _ => MaximizeClicked?.Invoke();
        var close = _right.Add(
            new IconButton(TitleIcon.Close) { HoverBackground = Colors.Hex(0xE81123), PressedBackground = Colors.Hex(0xF1707A), Tooltip = "Close Cuepoint" }
        );
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
        _maximize.Tooltip = IsMaximized() ? "Restore" : "Maximise";
        _record.Recording = Recording();
        _headphones.IconColor = Cueing() ? Theme.Accent : Theme.Text;
        var warning = AudioWarning();
        _audioWarning.Visible = _audioWarningGap.Visible = warning is not null;
        _audioWarning.Text = warning?.ToUpperInvariant() ?? "";
        _record.Tooltip = _record.Recording is null ? "Record the mix" : "Stop recording the mix";
        var os2l = Os2lConnected();
        _os2l.Visible = _os2lGap.Visible = os2l is not null;
        _os2l.IsConnected = os2l == true;
        var limiter = _engine.Limiter;
        if (limiter.TakeReduction() < LIMIT_LIGHT_GAIN)
            _limitedAt = Clock.Seconds();
        _limit.Visible = limiter.Enabled;
        _limit.Color = Clock.Seconds() - _limitedAt < LIMIT_LIGHT_HOLD ? Theme.Accent : Theme.Faint;
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
        // Measured even while it's hidden, to know whether it fits again.
        _centre.Measure();
        return Size = new float2(Width, HEIGHT);
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        float Middle(Element child) => MathF.Round(position.Y + (HEIGHT - child.Size.Y) / 2);
        _left.Arrange(new float2(position.X + 10, Middle(_left)));
        // The beat trackers and master level in the middle, or as near it as the sides leave room for; on a window
        // too narrow for them at all, they're left out.
        var centreMin = position.X + 10 + _left.Size.X + 16;
        var centreMax = position.X + Width - _right.Size.X - 16 - _centre.Size.X;
        _centre.Visible = centreMax >= centreMin;
        var centreX = Math.Clamp(position.X + (Width - _centre.Size.X) / 2, centreMin, MathF.Max(centreMin, centreMax));
        _centre.Arrange(new float2(MathF.Round(centreX), Middle(_centre)));
        _right.Arrange(new float2(position.X + Width - _right.Size.X, Middle(_right)));
        AlignMiddle(_left);
        AlignMiddle(_centre);
        AlignMiddle(_right);

        // "MASTER" centred by its capitals rather than its line box, which has room for descenders below, so it
        // lines up with the meter beside it.
        CentreCapitals(_masterLabel, _master.ScreenPosition.Y + _master.Size.Y / 2);
        CentreCapitals(_limit, _master.ScreenPosition.Y + _master.Size.Y / 2);

        // The CPU and the time likewise, so they line up with the OS2L indicator's text.
        var rightMiddle = _right.ScreenPosition.Y + _right.Size.Y / 2;
        CentreCapitals(_cpu, rightMiddle);
        CentreCapitals(_clock, rightMiddle);
    }

    /// <summary>
    /// Puts <paramref name="label"/>'s capitals, rather than its line box, centred on <paramref name="middle"/>.
    /// </summary>
    static void CentreCapitals(Label label, float middle) =>
        label.Arrange(new float2(label.ScreenPosition.X, MathF.Round(middle + BeatTrackers.CapHeight(label.Font) / 2 - label.Font.Ascender)));

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
