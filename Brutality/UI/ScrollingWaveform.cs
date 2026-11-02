using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Input;
using Brutality.Music;

namespace Brutality.UI;

/// <summary>
/// How the scrolling waveforms are coloured.
/// </summary>
enum WaveformColours
{
    /// <summary>
    /// In the deck's colour, darker where bass dominates and brighter where treble does.
    /// </summary>
    Deck,

    /// <summary>
    /// In three colours mixed by how much of each band there is: bass red, mids green and treble blue.
    /// </summary>
    ThreeBand,
}

/// <summary>
/// A deck's song as a waveform scrolling past a fixed line in the middle, which is where the deck is playing: what's
/// coming is to the right. Drawn in the deck's colour, darker where bass dominates and brighter where treble does,
/// so kicks, vocals and hi-hats look different, with a lighter core showing the average level inside the peaks, the
/// beat grid over it with a brighter line on each bar, and the deck's hotcues as numbered lines in their colours. Drag
/// it to move through the song. While its <see cref="Editor"/> is fixing the grid, a beat's line can be grabbed and
/// dragged onto the music's beat instead, pinning it there, and a pin right-clicked to take it out. Sized by whoever
/// lays it out, with <see cref="FixedSize"/>.
/// </summary>
sealed class ScrollingWaveform : Element
{
    /// <summary>
    /// How close to a beat's line, in pixels either side, grabs it.
    /// </summary>
    const float GRAB = 4;

    readonly Deck _deck;
    readonly Font _font;
    double _shown;
    double _lastStep = double.NaN;
    (float X, double Position)? _drag;
    float2? _pointer;

    // A beat being dragged: how far its time was from the song's under the pointer as it was grabbed, so it doesn't
    // jump to the pointer.
    double? _beatGrabOffset;

    public ScrollingWaveform(Deck deck, float4 color, Font font)
    {
        _deck = deck;
        _font = font;
        Color = color;
        PressEnded += _ =>
        {
            _drag = null;
            if (_beatGrabOffset is not null)
            {
                _beatGrabOffset = null;
                Editor?.EndDrag();
            }
        };
    }

    public float2 FixedSize { get; set; } = new(800, 64);

    /// <summary>
    /// How fast it scrolls, in pixels a second as it plays, which is how much time shows: at 100, a 1,200 pixel wide
    /// strip shows 12 seconds' playing. It's the same for every deck whatever its tempo, so two decks in sync scroll
    /// by together with their beats the same distance apart.
    /// </summary>
    public float PixelsPerSecond { get; set; } = 100;

    /// <summary>
    /// How many pixels a second of the song takes: <see cref="PixelsPerSecond"/> squeezed by the tempo fader, since a
    /// deck playing at +5% gets through 1.05 seconds of song a second. (Not by a jog wheel's momentary push, which
    /// would make the grid shiver while nudging.)
    /// </summary>
    float SongPixelsPerSecond => PixelsPerSecond / (float)_deck.TempoRatio;

    public float4 Color { get; set; }

    /// <summary>
    /// What fixes the deck's beat grid by hand: its pins are shown as markers, and while it's active, beats can be
    /// dragged and pins taken out here. None if null.
    /// </summary>
    public GridEditor? Editor { get; set; }

    public float4 Background { get; set; } = Theme.Well;

    public WaveformColours Colours { get; set; }

    /// <summary>
    /// Whether the beat grid's lines are drawn over the waveform. They always are while the grid's being fixed.
    /// </summary>
    public bool ShowBeatGrid { get; set; } = true;

    /// <summary>
    /// The mouse wheel turned over the waveform, by this many notches, up to zoom in: whoever sets
    /// <see cref="PixelsPerSecond"/> zooms it.
    /// </summary>
    public event Action<float>? Zoomed;

    public override bool Wheel(float2 notches)
    {
        if (notches.Y == 0)
            return false;
        Zoomed?.Invoke(notches.Y);
        return true;
    }

    public override bool IsInteractive => _deck.IsLoaded;

    public override Cursor Cursor =>
        _beatGrabOffset is not null || (_pointer is { } at && BeatAt(at.X) is not null) ? Cursor.ResizeHorizontal : Cursor.Arrow;

    /// <summary>
    /// The song's time at <paramref name="x"/> on screen, as it's drawn.
    /// </summary>
    double TimeAt(float x) => _shown + (x - MathF.Round(ScreenPosition.X + Size.X / 2)) / SongPixelsPerSecond;

    /// <summary>
    /// The beat whose line is within <see cref="GRAB"/> pixels of <paramref name="x"/>, while the grid's being fixed;
    /// null if none, or it isn't.
    /// </summary>
    long? BeatAt(float x)
    {
        if (Editor is not { Active: true } || _deck.Analysis?.Grid is not { } grid)
            return null;
        var beat = (long)Math.Round(grid.BeatNumberAt(TimeAt(x)));
        var lineX = MathF.Round(ScreenPosition.X + Size.X / 2) + (float)((grid.TimeOfBeat(beat) - _shown) * SongPixelsPerSecond);
        return MathF.Abs(lineX - x) <= GRAB ? beat : null;
    }

    /// <summary>
    /// The pin within <see cref="GRAB"/> pixels of <paramref name="x"/>, while the grid's being fixed; null if none.
    /// </summary>
    double? PinAt(float x)
    {
        if (Editor is not { Active: true } editor)
            return null;
        var centre = MathF.Round(ScreenPosition.X + Size.X / 2);
        foreach (var pin in editor.Pins)
        {
            if (MathF.Abs(centre + (float)((pin - _shown) * SongPixelsPerSecond) - x) <= GRAB + 1)
                return pin;
        }
        return null;
    }

    public override float2 Measure() => Size = FixedSize;

    /// <summary>
    /// The deck's position, smoothed: SDL_mixer only moves it on a buffer at a time, which would make the waveform
    /// stutter, so while playing it runs on by the clock and eases toward the deck's figure.
    /// </summary>
    double SmoothedPosition()
    {
        var reported = _deck.Position.TotalSeconds;
        var dt = Clock.Delta(ref _lastStep);
        if (!_deck.IsPlaying || _drag is not null)
            return _shown = reported;

        // The song moves on at the deck's tempo, not the clock's.
        _shown += dt * _deck.TempoRatio;
        var behind = reported - _shown;
        if (Math.Abs(behind) > 0.15)
            _shown = reported;
        else
            _shown += behind * Math.Min(1, dt * 8);
        return _shown;
    }

    public override void Draw(Batch batch)
    {
        batch.AddRect(ScreenPosition, Size, Background);
        var middle = MathF.Round(ScreenPosition.Y + Size.Y / 2);
        var half = MathF.Floor(Size.Y / 2) - 2;
        var centre = MathF.Round(ScreenPosition.X + Size.X / 2);
        var width = (int)Size.X;

        if (_deck.Detail is { } detail)
        {
            var position = SmoothedPosition();
            var pps = SongPixelsPerSecond;

            // Columns stay on a fixed grid in song time and the whole lot moves by whole pixels, so the waveform
            // scrolls without the shimmer of re-dividing it every frame.
            var origin = Math.Round(position * pps);
            var dark = Color * new float4(0.35f, 0.35f, 0.35f, 1);
            var bright = Theme.Mix(Color, Colors.White, 0.35f);
            for (var x = 0; x < width; x++)
            {
                var column = origin + x - (centre - ScreenPosition.X);
                var (peak, bass, mids, treble) = detail.Between(column / pps, (column + 1) / pps);
                if (peak <= 0)
                    continue;

                // Treble is far quieter than bass in energy, so it's weighted up to count about as much as it sounds.
                var mid = mids * 1.5f;
                var high = treble * 2.5f;
                var outer = Colours == WaveformColours.ThreeBand ? ThreeBand(bass, mid, high) : DeckShade(bass, mid, high, dark, bright);
                // The average level inside the peaks, lighter; less so in three bands, so as not to wash them out.
                var inner = Theme.Mix(outer, Colors.White, Colours == WaveformColours.ThreeBand ? 0.2f : 0.45f);

                var height = MathF.Max(1, MathF.Round(peak * half));
                batch.AddRect(new float2(ScreenPosition.X + x, middle - height), new float2(1, height * 2), outer);

                var level = MathF.Sqrt(bass * bass + mids * mids + treble * treble) * 1.15f;
                var core = MathF.Min(height, MathF.Round(level * half));
                if (core >= 1)
                    batch.AddRect(new float2(ScreenPosition.X + x, middle - core), new float2(1, core * 2), inner);
            }

            DrawLoop(batch, position, centre);
            DrawBeats(batch, position, centre);
            DrawPins(batch, position, centre);
            DrawHotcues(batch, position, centre);
        }

        // Where the deck is.
        batch.AddRect(new float2(centre - 1, ScreenPosition.Y), new float2(2, Size.Y), Colors.White);
        DrawLabel(batch);
    }

    /// <summary>
    /// A column in the deck's colour, from <paramref name="dark"/> where it's all bass to <paramref name="bright"/>
    /// where it's all treble.
    /// </summary>
    float4 DeckShade(float bass, float mid, float high, float4 dark, float4 bright)
    {
        var brightness = (0.5f * mid + high) / MathF.Max(1e-6f, bass + mid + high);
        return brightness < 0.5f ? Theme.Mix(dark, Color, brightness * 2) : Theme.Mix(Color, bright, (brightness - 0.5f) * 2);
    }

    /// <summary>
    /// How sharply the weaker bands fade beside the strongest in <see cref="ThreeBand"/>: at 1, each would show as much
    /// as it is, and music, which nearly always has some of everything, would come out pale.
    /// </summary>
    const float THREE_BAND_CONTRAST = 2.5f;

    /// <summary>
    /// A column mixed from red for bass, green for mids and blue for treble: the strongest at full, the others
    /// falling away sharply as they're weaker, so a kick shows a strong red, a vocal green and hi-hats blue, and only
    /// everything at once, evenly, shows white.
    /// </summary>
    internal static float4 ThreeBand(float bass, float mid, float high)
    {
        var most = MathF.Max(1e-6f, MathF.Max(bass, MathF.Max(mid, high)));
        float Share(float band) => MathF.Pow(band / most, THREE_BAND_CONTRAST);
        return new float4(Share(bass), Share(mid), Share(high), 1);
    }

    /// <summary>
    /// Which deck's lane this is, as its letter in a badge of its colour at the left, and what's missing when it's
    /// empty.
    /// </summary>
    void DrawLabel(Batch batch)
    {
        // In the top corner, framed in the background's colour so it stands out over the waveform.
        var letter = _font.Measure(_deck.Name);
        var badge = new float2(MathF.Max(letter.X, letter.Y) + 8, letter.Y + 4);
        var at = new float2(ScreenPosition.X + 5, ScreenPosition.Y + 5);
        batch.AddRect(at - new float2(2, 2), badge + new float2(4, 4), Background);
        batch.AddRect(at, badge, _deck.IsLoaded ? Color : Theme.Fade(Color, 0.4f));
        batch.AddText(_font, _deck.Name, new float2(MathF.Round(at.X + (badge.X - letter.X) / 2), at.Y + 2), Theme.TitleBar);
        if (!_deck.IsLoaded)
        {
            var text = _deck.LoadingName is { } loading ? $"Loading {loading}…" : "No track loaded";
            batch.AddText(_font, text, new float2(at.X + badge.X + 8, MathF.Round(at.Y + (badge.Y - _font.Measure(text).Y) / 2)), Theme.Faint);
        }
    }

    void DrawBeats(Batch batch, double position, float centre)
    {
        if (_deck.Analysis?.Grid is not { } grid || !ShowBeatGrid && Editor is not { Active: true })
            return;

        // Across the whole view, before the song and after it too: the grid carries on at its first tempo and
        // phase before the first beat, and at its last after the end, so the beats can still be lined up there.
        var pps = SongPixelsPerSecond;
        var halfSeconds = Size.X / 2 / pps;
        var first = Math.Ceiling(grid.BeatNumberAt(position - halfSeconds));
        var last = Math.Floor(grid.BeatNumberAt(position + halfSeconds));
        var tick = MathF.Round(Size.Y * 0.18f);
        for (var beat = first; beat <= last; beat++)
        {
            var x = MathF.Round(centre + (float)((grid.TimeOfBeat(beat) - position) * pps));
            if (x < ScreenPosition.X || x >= ScreenPosition.X + Size.X)
                continue;

            var isBar = grid.IsBarStart(beat);
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(1, Size.Y), Colors.Hex(0xFFFFFF, isBar ? 0.35f : 0.12f));
            if (isBar)
            {
                batch.AddRect(new float2(x - 1, ScreenPosition.Y), new float2(3, tick), Colors.Hex(0xFFFFFF, 0.8f));
                batch.AddRect(new float2(x - 1, ScreenPosition.Y + Size.Y - tick), new float2(3, tick), Colors.Hex(0xFFFFFF, 0.8f));
            }
        }
    }

    /// <summary>
    /// Beats pinned by hand, as lines in the accent colour with a block at each end.
    /// </summary>
    void DrawPins(Batch batch, double position, float centre)
    {
        if (Editor?.Pins is not { Count: > 0 } pins)
            return;
        var pps = SongPixelsPerSecond;
        foreach (var pin in pins)
        {
            var x = MathF.Round(centre + (float)((pin - position) * pps));
            if (x < ScreenPosition.X || x >= ScreenPosition.X + Size.X)
                continue;
            batch.AddRect(new float2(x - 1, ScreenPosition.Y), new float2(2, Size.Y), Theme.Fade(Theme.Accent, 0.8f));
            batch.AddRect(new float2(x - 4, ScreenPosition.Y), new float2(8, 5), Theme.Accent);
            batch.AddRect(new float2(x - 4, ScreenPosition.Y + Size.Y - 5), new float2(8, 5), Theme.Accent);
        }
    }

    /// <summary>
    /// The loop, while there is one, shaded over the waveform with solid edges.
    /// </summary>
    void DrawLoop(Batch batch, double position, float centre)
    {
        if (_deck.Loop is not { } loop)
            return;
        var pps = SongPixelsPerSecond;
        float X(TimeSpan at) => MathF.Round(centre + (float)((at.TotalSeconds - position) * pps));
        var start = MathF.Max(ScreenPosition.X, X(loop.Start));
        var end = MathF.Min(ScreenPosition.X + Size.X, X(loop.End));
        if (end <= start)
            return;
        batch.AddRect(new float2(start, ScreenPosition.Y), new float2(end - start, Size.Y), Theme.Fade(Theme.Loop, 0.22f));
        if (X(loop.Start) >= ScreenPosition.X)
            batch.AddRect(new float2(X(loop.Start), ScreenPosition.Y), new float2(2, Size.Y), Theme.Loop);
        if (X(loop.End) < ScreenPosition.X + Size.X)
            batch.AddRect(new float2(X(loop.End) - 2, ScreenPosition.Y), new float2(2, Size.Y), Theme.Loop);
    }

    void DrawHotcues(Batch batch, double position, float centre)
    {
        for (var i = 0; i < _deck.Hotcues.Count; i++)
        {
            if (_deck.Hotcues[i] is not { } at)
                continue;
            var x = MathF.Round(centre + (float)((at.TotalSeconds - position) * SongPixelsPerSecond));
            if (x < ScreenPosition.X - 20 || x >= ScreenPosition.X + Size.X)
                continue;

            var color = Theme.HotcueColor(i);
            batch.PushClip(ScreenPosition, Size);
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(2, Size.Y), color);
            WaveformBar.DrawFlag(batch, _font, new float2(x, ScreenPosition.Y), (i + 1).ToString(), color, Theme.TitleBar);
            batch.PopClip();
        }
    }

    public override void PointerMove(float2 point) => _pointer = point;

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        _drag = null;
        _beatGrabOffset = null;
        if (BeatAt(point.X) is { } beat && Editor!.BeginDrag(beat))
            _beatGrabOffset = _deck.Analysis!.Grid.TimeOfBeat(beat) - TimeAt(point.X);
        else
            _drag = (point.X, _deck.Position.TotalSeconds);
    }

    /// <summary>
    /// Dragging moves the song with the pointer, as if pushing the waveform along; or, holding a beat's line, moves
    /// the beat.
    /// </summary>
    public override void PointerDrag(float2 point)
    {
        _pointer = point;
        if (_beatGrabOffset is { } offset)
        {
            Editor?.DragTo(TimeAt(point.X) + offset);
            return;
        }
        if (_drag is not { } drag)
            return;
        _deck.Seek(TimeSpan.FromSeconds(Math.Max(0, drag.Position - (point.X - drag.X) / SongPixelsPerSecond)));
    }

    /// <summary>
    /// Right-clicking a pin, while the grid's being fixed, takes it out.
    /// </summary>
    public override void SecondaryClick(float2 point)
    {
        if (PinAt(point.X) is { } pin)
            Editor!.RemovePin(pin);
    }
}
