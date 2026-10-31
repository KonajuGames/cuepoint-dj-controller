using Brutal.Numerics;
using Brutality.Audio;

namespace Brutality.UI;

/// <summary>
/// A song's progress bar drawn as its waveform: loud passages stand tall and breaks and drops show as gaps, so you can
/// see what's coming. The part already played is coloured; seeking works as on any <see cref="Slider"/>. With no
/// <see cref="Waveform"/> it draws a plain track.
/// </summary>
sealed class WaveformBar : Slider
{
    public Waveform? Waveform { get; set; }

    public float4 PlayedColor { get; set; } = Colors.Hex(0xEB7828);
    public float4 UnplayedColor { get; set; } = Colors.Hex(0xFFFFFF, 0.35f);
    public float4 BackgroundColor { get; set; } = Colors.Hex(0x000000, 0.25f);

    protected override void DrawTrack(Batch batch)
    {
        if (Waveform is not { } waveform || IsVertical)
        {
            base.DrawTrack(batch);
            return;
        }

        batch.AddRect(ScreenPosition, Size, Faded(BackgroundColor));

        // One column per pixel of the handle's travel, centred on the handle's middle at each point, mirrored about
        // the bar's centre line. Quiet stretches keep a pixel of height so the song's extent still shows.
        var left = ScreenPosition.X + HandleLength / 2;
        var middle = ScreenPosition.Y + Size.Y / 2;
        var halfHeight = Size.Y / 2 - 1;
        var columns = (int)MathF.Ceiling(Travel);
        var playhead = OffsetOf(Fraction);
        var played = Faded(PlayedColor);
        var unplayed = Faded(UnplayedColor);
        for (var column = 0; column < columns; column++)
        {
            var peak = waveform.PeakBetween(column / Travel, (column + 1) / Travel);
            var height = MathF.Max(1, MathF.Round(peak * halfHeight));
            batch.AddRect(
                new float2(left + column, middle - height),
                new float2(1, height * 2),
                column + 0.5f < playhead ? played : unplayed
            );
        }
    }
}
