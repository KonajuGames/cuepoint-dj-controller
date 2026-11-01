namespace Brutality.Audio.Analysis;

/// <summary>
/// A second-order IIR filter section (transposed direct form II), for one channel.
/// </summary>
struct Biquad(double b0, double b1, double b2, double a1, double a2)
{
    double _z1;
    double _z2;

    public double Process(double x)
    {
        var y = b0 * x + _z1;
        _z1 = b1 * x - a1 * y + _z2;
        _z2 = b2 * x - a2 * y;
        return y;
    }

    /// <summary>
    /// This filter with <paramref name="tuning"/>'s coefficients but its own state, so it can be retuned while it
    /// runs, as a sweeping filter is, without a click.
    /// </summary>
    public Biquad Retuned(Biquad tuning)
    {
        var retuned = tuning;
        retuned._z1 = _z1;
        retuned._z2 = _z2;
        return retuned;
    }

    /// <summary>
    /// A Butterworth-style low-pass at <paramref name="cutoff"/> Hz with quality <paramref name="q"/> (0.7071 for a
    /// single section), from the Audio EQ Cookbook.
    /// </summary>
    public static Biquad LowPass(double cutoff, double q, int sampleRate)
    {
        var w = 2 * Math.PI * cutoff / sampleRate;
        var alpha = Math.Sin(w) / (2 * q);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        return new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    /// <summary>
    /// The matching high-pass, from the same cookbook.
    /// </summary>
    public static Biquad HighPass(double cutoff, double q, int sampleRate)
    {
        var w = 2 * Math.PI * cutoff / sampleRate;
        var alpha = Math.Sin(w) / (2 * q);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        return new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }
}
