using Brutal.Numerics;
using Brutality.Audio;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// A deck's song as a waveform scrolling past a fixed line in the middle, which is where the deck is playing: what's
/// coming is to the right. Drawn in the deck's colour, darker where bass dominates and brighter where treble does,
/// so kicks, vocals and hi-hats look different, with a lighter core showing the average level inside the peaks, the
/// beat grid over it with a brighter line on each bar, and the deck's hotcues as numbered lines in their colours. Drag
/// it to move through the song. Sized by whoever lays it out, with <see cref="FixedSize"/>.
/// </summary>
sealed class ScrollingWaveform : Element
{
    readonly Deck _deck;
    readonly Font _font;
    double _shown;
    double _lastStep = double.NaN;
    (float X, double Position)? _drag;

    public ScrollingWaveform(Deck deck, float4 color, Font font)
    {
        _deck = deck;
        _font = font;
        Color = color;
        PressEnded += _ => _drag = null;
    }

    public float2 FixedSize { get; set; } = new(800, 64);

    /// <summary>
    /// How fast it scrolls, which is how much of the song shows: at 100, a 1,200 pixel wide strip shows 12 seconds.
    /// </summary>
    public float PixelsPerSecond { get; set; } = 100;

    public float4 Color { get; set; }
    public float4 Background { get; set; } = Theme.Well;

    public override bool IsInteractive => _deck.IsLoaded;
    public override Cursor Cursor => Cursor.Arrow;

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

        _shown += dt;
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
            var pps = PixelsPerSecond;

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
                var brightness = (0.5f * mid + high) / MathF.Max(1e-6f, bass + mid + high);
                var outer = brightness < 0.5f
                    ? Theme.Mix(dark, Color, brightness * 2)
                    : Theme.Mix(Color, bright, (brightness - 0.5f) * 2);
                var inner = Theme.Mix(outer, Colors.White, 0.45f);

                var height = MathF.Max(1, MathF.Round(peak * half));
                batch.AddRect(new float2(ScreenPosition.X + x, middle - height), new float2(1, height * 2), outer);

                var level = MathF.Sqrt(bass * bass + mids * mids + treble * treble) * 1.15f;
                var core = MathF.Min(height, MathF.Round(level * half));
                if (core >= 1)
                    batch.AddRect(new float2(ScreenPosition.X + x, middle - core), new float2(1, core * 2), inner);
            }

            DrawLoop(batch, position, centre);
            DrawBeats(batch, position, centre);
            DrawHotcues(batch, position, centre);
        }

        // Where the deck is.
        batch.AddRect(new float2(centre - 1, ScreenPosition.Y), new float2(2, Size.Y), Colors.White);
    }

    void DrawBeats(Batch batch, double position, float centre)
    {
        if (_deck.Analysis?.Grid is not { } grid)
            return;

        // Only over the song itself.
        var halfSeconds = Size.X / 2 / PixelsPerSecond;
        var first = Math.Ceiling(grid.BeatNumberAt(Math.Max(0, position - halfSeconds)));
        var last = Math.Floor(grid.BeatNumberAt(Math.Min(_deck.Duration.TotalSeconds, position + halfSeconds)));
        var tick = MathF.Round(Size.Y * 0.18f);
        for (var beat = first; beat <= last; beat++)
        {
            var x = MathF.Round(centre + (float)((grid.TimeOfBeat(beat) - position) * PixelsPerSecond));
            if (x < ScreenPosition.X || x >= ScreenPosition.X + Size.X)
                continue;

            var isBar = ((long)beat % 4 + 4) % 4 == 0;
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(1, Size.Y), Colors.Hex(0xFFFFFF, isBar ? 0.35f : 0.12f));
            if (isBar)
            {
                batch.AddRect(new float2(x - 1, ScreenPosition.Y), new float2(3, tick), Colors.Hex(0xFFFFFF, 0.8f));
                batch.AddRect(new float2(x - 1, ScreenPosition.Y + Size.Y - tick), new float2(3, tick), Colors.Hex(0xFFFFFF, 0.8f));
            }
        }
    }

    /// <summary>
    /// The loop, while there is one, shaded over the waveform with solid edges.
    /// </summary>
    void DrawLoop(Batch batch, double position, float centre)
    {
        if (_deck.Loop is not { } loop)
            return;
        float X(TimeSpan at) => MathF.Round(centre + (float)((at.TotalSeconds - position) * PixelsPerSecond));
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
            var x = MathF.Round(centre + (float)((at.TotalSeconds - position) * PixelsPerSecond));
            if (x < ScreenPosition.X - 20 || x >= ScreenPosition.X + Size.X)
                continue;

            var color = Theme.HotcueColor(i);
            batch.PushClip(ScreenPosition, Size);
            batch.AddRect(new float2(x, ScreenPosition.Y), new float2(2, Size.Y), color);
            WaveformBar.DrawFlag(batch, _font, new float2(x, ScreenPosition.Y), (i + 1).ToString(), color, Theme.TitleBar);
            batch.PopClip();
        }
    }

    public override void PointerDown(float2 point, Modifiers modifiers) =>
        _drag = (point.X, _deck.Position.TotalSeconds);

    /// <summary>
    /// Dragging moves the song with the pointer, as if pushing the waveform along.
    /// </summary>
    public override void PointerDrag(float2 point)
    {
        if (_drag is not { } drag)
            return;
        _deck.Seek(TimeSpan.FromSeconds(Math.Max(0, drag.Position - (point.X - drag.X) / PixelsPerSecond)));
    }
}
