using System.Diagnostics;
using Brutal.Numerics;
using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.UI;

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
        // The bars before the first count back 3, 2, 1, 0 as well.
        var (bar, beat) = Quantizing.BarAndBeat(grid, seconds);
        return (beat - 1, (int)((((bar - 1) % 4) + 4) % 4));
    }

    public override float2 Measure() =>
        Size = new float2(LETTER + 4 * BOX + 3 * GAP, decks.Count * ROW + Math.Max(0, decks.Count - 1) * ROW_GAP);

    /// <summary>
    /// How tall <paramref name="font"/>'s capitals stand above the baseline, from its M.
    /// </summary>
    public static float CapHeight(Font font) => font.GetGlyph('M').Bearing.Y;

    public override void Draw(Batch batch)
    {
        // The letters share a column as wide as the widest, each centred in it, so they line up down their middles.
        var column = decks.Max(deck => font.Advance(deck.Name));
        for (var row = 0; row < decks.Count; row++)
        {
            var deck = decks[row];
            var color = AppTheme.DeckColor(deck.Name);
            var top = ScreenPosition.Y + row * (ROW + ROW_GAP);

            // The letter's capitals centred on the row, rather than its line box.
            var x = MathF.Round(ScreenPosition.X + (column - font.Advance(deck.Name)) / 2);
            batch.AddText(font, deck.Name, new float2(x, MathF.Round(top + ROW / 2 + CapHeight(font) / 2 - font.Ascender)), color);

            var (beat, bar) = (-1, -1);
            if (deck.Analysis?.Grid is { } grid && deck.IsLoaded)
                (beat, bar) = Place(grid, deck.Position.TotalSeconds);

            // Four black boxes: the bar (of four) filled with the deck's colour, and the beat (of four) as a smaller
            // block of colour inside its box, which in the bar's own box is just the full colour.
            var lit = deck.IsPlaying ? color : Theme.Fade(color, 0.45f);
            for (var i = 0; i < 4; i++)
            {
                var at = new float2(ScreenPosition.X + LETTER + i * (BOX + GAP), top);
                batch.AddRect(at, new float2(BOX, ROW), i == bar ? lit : AppTheme.Unlit);
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
            batch.AddRect(ScreenPosition, Size, Theme.Tint(IsPressed && IsHovered ? 0.05f : 0.1f));

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
/// Cuepoint's widgets in the window's title bar (the framework's <see cref="TitleBar"/>): each deck's place in the bar
/// stacked beside the master level and the limiter's light in the middle; a missing sound card, the record button,
/// whether lighting software is connected and the CPU before the clock; and the headphones button. Call
/// <see cref="Update"/> once a frame, with the bar's.
/// </summary>
sealed class TitleBarItems
{
    readonly AudioEngine _engine;
    readonly LevelMeter _master;
    readonly Knob _masterVolume;
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

    // The master meter's length and segments, and the least it's shortened to on a crowded title bar.
    const float MASTER_LENGTH = 160;
    const int MASTER_SEGMENTS = 16;
    const float MIN_MASTER_LENGTH = 60;

    double _limitedAt = double.NegativeInfinity;
    readonly Os2lIndicator _os2l;
    readonly RecordButton _record;
    readonly Button _live;
    readonly Label _audioWarning;
    readonly Spacer _audioWarningGap;
    readonly Spacer _os2lGap;
    readonly Label _cpu;
    readonly IconButton _headphones;
    readonly Button _masterFx;
    readonly Button _mic;
    readonly Process _process = Process.GetCurrentProcess();
    readonly Stopwatch _sinceCpu = Stopwatch.StartNew();
    TimeSpan _cpuBefore;

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

    /// <summary>
    /// The LIVE button was clicked, to show or put away live streaming below it.
    /// </summary>
    public event Action? LiveClicked;

    /// <summary>
    /// The live stream going, if there is one, to light LIVE and say how long it's been.
    /// </summary>
    public Func<Streaming.LiveStream?> LiveStream { get; set; } = () => null;

    /// <summary>
    /// The bottom right corner of the LIVE button, where live streaming drops down from.
    /// </summary>
    public float2 LiveAnchor => _live.ScreenPosition + _live.Size;

    /// <summary>
    /// The master FX button was clicked, to show or put away the effect on the whole mix below it.
    /// </summary>
    public event Action? MasterFxClicked;

    /// <summary>
    /// The bottom left corner of the master FX button, where the master effect drops down from.
    /// </summary>
    public float2 MasterFxAnchor => _masterFx.ScreenPosition + new float2(0, _masterFx.Size.Y);

    /// <summary>
    /// The MIC button was clicked, to show or put away the microphone's settings below it.
    /// </summary>
    public event Action? MicClicked;

    /// <summary>
    /// The bottom left corner of the MIC button, where the microphone's settings drop down from.
    /// </summary>
    public float2 MicAnchor => _mic.ScreenPosition + new float2(0, _mic.Size.Y);

    /// <summary>
    /// Puts Cuepoint's widgets into <paramref name="bar"/>.
    /// </summary>
    public TitleBarItems(TitleBar bar, AudioEngine engine, IReadOnlyList<Deck> decks, Font small, Font body, Font bold, Images images)
    {
        _engine = engine;

        // The decks' beat trackers, A over B, then a gap, then the master level and its name.
        bar.Centre.Add(new BeatTrackers(decks, small));
        bar.Centre.Add(new Spacer(20));
        _masterLabel = bar.Centre.Add(new Label(small, "MASTER") { Color = Theme.Muted });
        bar.Centre.Add(new Spacer(6));
        _masterVolume = bar.Centre.Add(
            new Knob(images.Knob, images.Dot)
            {
                Min = 0,
                Max = 1,
                DefaultValue = 1,
                FillFrom = 0,
                Diameter = 18,
                Value = engine.MasterVolume,
                FocusColor = Theme.Focus,
                Tooltip = "Master volume: the whole mix, as the speakers, the recording and the headphones' main mix hear it. Double-click for full",
            }
        );
        _masterVolume.Changed += knob => engine.MasterVolume = knob.Value;
        bar.Centre.Add(new Spacer(6));
        _master = bar.Centre.Add(new LevelMeter(2) { Orientation = Orientation.Horizontal, Length = MASTER_LENGTH, BarThickness = 4, BarGap = 2, Segments = MASTER_SEGMENTS });
        // Lit while the master limiter is turning the mix down: a sign the decks are too loud together.
        _limit = bar.Centre.Add(new Label(small, "LIMIT") { Color = Theme.Faint });
        bar.Centre.Add(new Spacer(10));
        // The effect on the whole mix, lit while it's on.
        _masterFx = bar.Centre.Add(
            new Button(small, "FX")
            {
                Padding = 3,
                MinSize = new float2(26, 18),
                Tooltip = "Master FX: an effect on the whole mix, timed by the deck leading it",
            }
        );
        _masterFx.Clicked += _ => MasterFxClicked?.Invoke();
        bar.Centre.Add(new Spacer(6));
        // The microphone, lit while it's in the mix, and brighter while talkover has the music down.
        _mic = bar.Centre.Add(
            new Button(small, "MIC")
            {
                Padding = 3,
                MinSize = new float2(32, 18),
                Tooltip = "Microphone: put it in the mix, its level, and talkover",
            }
        );
        _mic.Clicked += _ => MicClicked?.Invoke();

        _audioWarning = bar.Status.Add(new Label(bold) { Color = Theme.Warning, Visible = false });
        _audioWarningGap = bar.Status.Add(new Spacer(16) { Visible = false });
        // Live streaming, before the record button: lit while it's live, amber while it's trying to get back on.
        _live = bar.Status.Add(new Button(small, "LIVE") { Padding = 3, MinSize = new float2(38, 18), Tooltip = "Stream the mix live to an Icecast server" });
        _live.Clicked += _ => LiveClicked?.Invoke();
        bar.Status.Add(new Spacer(10));
        _record = bar.Status.Add(new RecordButton(body));
        _record.Clicked += _ => RecordClicked?.Invoke();
        bar.Status.Add(new Spacer(12));
        _os2l = bar.Status.Add(new Os2lIndicator(bold) { Visible = false });
        _os2lGap = bar.Status.Add(new Spacer(16) { Visible = false });
        _cpu = bar.Status.Add(new Label(body) { Color = Theme.Muted });

        _headphones = bar.Tools.Add(new IconButton(TitleIcon.Picture) { Picture = images.Headphones, Tooltip = "Headphones: what they hear, and how loud" });
        _headphones.Clicked += _ => HeadphonesClicked?.Invoke();

        // The master meter at its full length, or shorter if that's what it takes for the middle to fit between the
        // sides once it's moved left, down to a short one.
        bar.FitCentre = room =>
        {
            (_master.Length, _master.Segments) = (MASTER_LENGTH, MASTER_SEGMENTS);
            bar.Centre.Measure();
            if (bar.Centre.Size.X > room)
            {
                _master.Length = MathF.Max(MIN_MASTER_LENGTH, MASTER_LENGTH - (bar.Centre.Size.X - room));
                // Segments as wide as at full length.
                _master.Segments = (int)MathF.Round(_master.Length / MASTER_LENGTH * MASTER_SEGMENTS);
            }
        };
        // "MASTER" and "LIMIT" centred by their capitals on the meter beside them.
        bar.Arranged += () =>
        {
            var middle = _master.ScreenPosition.Y + _master.Size.Y / 2;
            TitleBar.CentreCapitals(_masterLabel, middle);
            TitleBar.CentreCapitals(_limit, middle);
        };

        _cpuBefore = _process.TotalProcessorTime;
        _cpu.Text = "CPU –";
        Update();
    }

    public void Update()
    {
        _record.Recording = Recording();
        var live = LiveStream();
        var (lit, text) = live switch
        {
            { State: Streaming.StreamState.Live, LiveSince: { } since } => (
                Theme.Warning,
                $"LIVE {(int)(DateTime.Now - since).TotalMinutes}:{(DateTime.Now - since).Seconds:00}{(live.Listeners is { } listening ? $" · {listening}" : "")}"
            ),
            { State: Streaming.StreamState.Live or Streaming.StreamState.Connecting } => (Theme.Warning, "LIVE"),
            { State: Streaming.StreamState.Retrying } => (Theme.Accent, "LIVE ..."),
            _ => (Theme.Control, "LIVE"),
        };
        _live.Text = text;
        _live.Background = live is { State: not Streaming.StreamState.Stopped } ? Theme.Mix(Theme.PanelRaised, lit, 0.6f) : Theme.Control;
        _live.TextColor = live is { State: not Streaming.StreamState.Stopped } ? Theme.Text : Theme.Muted;
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
        var fxOn = _engine.MasterEffects.On;
        _masterFx.Background = fxOn ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.6f) : Theme.Control;
        _masterFx.HoverBackground = fxOn ? Theme.Mix(Theme.PanelRaised, Theme.Accent, 0.75f) : Theme.ControlHover;
        _masterFx.TextColor = fxOn ? Theme.Text : Theme.Muted;
        var micOn = _engine.MicrophoneOn && _engine.MicrophoneDevice is not null;
        var micLit = !micOn ? 0f : _engine.TalkingOver ? 0.8f : 0.5f;
        _mic.Background = micLit > 0 ? Theme.Mix(Theme.PanelRaised, Theme.Warning, micLit) : Theme.Control;
        _mic.HoverBackground = micLit > 0 ? Theme.Mix(Theme.PanelRaised, Theme.Warning, micLit + 0.1f) : Theme.ControlHover;
        _mic.TextColor = micOn ? Theme.Text : Theme.Muted;
        _limit.Color = Clock.Seconds() - _limitedAt < LIMIT_LIGHT_HOLD ? Theme.Accent : Theme.Faint;
        if (!_masterVolume.IsDragging)
            _masterVolume.Value = _engine.MasterVolume;
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
}
