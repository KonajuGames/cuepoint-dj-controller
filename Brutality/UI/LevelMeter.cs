using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// A level meter: one or more bars side by side (left and right, say) that light up in segments from
/// <see cref="FLOOR_DB"/> to full scale, green then yellow from -12 dB then red from -3 dB. Each bar jumps up to a new
/// peak and falls back slowly, with a line holding the highest recent peak a moment longer. Feed it with
/// <see cref="Push"/> once a frame.
/// </summary>
sealed class LevelMeter : Element
{
    public const float FLOOR_DB = -48;

    const float FALL_DB_PER_SECOND = 24;
    const double HOLD_SECONDS = 1.2;
    const float YELLOW_DB = -12;
    const float RED_DB = -3;

    public Orientation Orientation { get; set; } = Orientation.Vertical;

    /// <summary>
    /// How long the meter is along its bars, and how thick each bar is.
    /// </summary>
    public float Length { get; set; } = 120;

    public float BarThickness { get; set; } = 5;
    public float BarGap { get; set; } = 2;

    /// <summary>
    /// How many segments each bar is divided into.
    /// </summary>
    public int Segments { get; set; } = 24;

    public float4 Green { get; set; } = Colors.Hex(0x3DDC6A);
    public float4 Yellow { get; set; } = Colors.Hex(0xF2C53D);
    public float4 Red { get; set; } = Colors.Hex(0xF04848);
    public float4 Unlit { get; set; } = Colors.Hex(0xFFFFFF, 0.07f);

    readonly float[] _shown;
    readonly float[] _held;
    readonly double[] _heldAt;
    double _lastStep = double.NaN;

    public LevelMeter(int bars = 2)
    {
        _shown = Enumerable.Repeat(FLOOR_DB, bars).ToArray();
        _held = Enumerable.Repeat(FLOOR_DB, bars).ToArray();
        _heldAt = new double[bars];
    }

    public int Bars => _shown.Length;

    /// <summary>
    /// What each bar shows, in dB, as it stands.
    /// </summary>
    public float Shown(int bar) => _shown[bar];

    /// <summary>
    /// The latest peak for each bar, where 1 is full scale.
    /// </summary>
    public void Push(params ReadOnlySpan<float> peaks)
    {
        var dt = (float)Clock.Delta(ref _lastStep);
        var now = Clock.Seconds();
        for (var bar = 0; bar < _shown.Length; bar++)
        {
            var peak = bar < peaks.Length ? peaks[bar] : peaks[^1];
            var db = peak > 0 ? MathF.Max(FLOOR_DB, 20 * MathF.Log10(peak)) : FLOOR_DB;
            _shown[bar] = MathF.Max(db, _shown[bar] - FALL_DB_PER_SECOND * dt);
            if (db >= _held[bar] || now - _heldAt[bar] > HOLD_SECONDS)
                (_held[bar], _heldAt[bar]) = (MathF.Max(db, _shown[bar]), now);
        }
    }

    bool IsVertical => Orientation == Orientation.Vertical;

    float Thickness => Bars * BarThickness + (Bars - 1) * BarGap;

    public override float2 Measure() => Size = IsVertical ? new float2(Thickness, Length) : new float2(Length, Thickness);

    public override void Draw(Batch batch)
    {
        var segment = Length / Segments;
        for (var bar = 0; bar < Bars; bar++)
        {
            var across = bar * (BarThickness + BarGap);
            var lit = (int)MathF.Round(Fraction(_shown[bar]) * Segments);
            for (var i = 0; i < Segments; i++)
            {
                var db = FLOOR_DB + (i + 0.5f) / Segments * -FLOOR_DB;
                var color = i >= lit ? Unlit : db >= RED_DB ? Red : db >= YELLOW_DB ? Yellow : Green;
                DrawAlong(batch, across, i * segment + 1, segment - 1, color);
            }

            if (_held[bar] > FLOOR_DB)
            {
                var at = MathF.Min(Fraction(_held[bar]) * Length, Length - 2);
                var color = _held[bar] >= RED_DB ? Red : _held[bar] >= YELLOW_DB ? Yellow : Green;
                DrawAlong(batch, across, at, 2, color);
            }
        }
    }

    static float Fraction(float db) => Math.Clamp((db - FLOOR_DB) / -FLOOR_DB, 0, 1);

    /// <summary>
    /// A piece of bar from <paramref name="from"/> along it (from the bottom, or the left), <paramref name="length"/>
    /// long.
    /// </summary>
    void DrawAlong(Batch batch, float across, float from, float length, float4 color)
    {
        if (IsVertical)
            batch.AddRect(new float2(ScreenPosition.X + across, ScreenPosition.Y + Length - from - length), new float2(BarThickness, length), color);
        else
            batch.AddRect(new float2(ScreenPosition.X + from, ScreenPosition.Y + across), new float2(length, BarThickness), color);
    }
}
