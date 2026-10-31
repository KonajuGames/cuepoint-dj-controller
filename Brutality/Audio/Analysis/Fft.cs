namespace Brutality.Audio.Analysis;

/// <summary>
/// A radix-2 fast Fourier transform of one fixed size, with a Hann window, for turning frames of a song into
/// spectra. Not thread-safe: each thread needs its own.
/// </summary>
sealed class Fft
{
    public int Size { get; }

    readonly float[] _cos;
    readonly float[] _sin;
    readonly int[] _reversed;
    readonly float[] _window;
    readonly float[] _re;
    readonly float[] _im;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException($"FFT size must be a power of two, not {size}", nameof(size));
        Size = size;

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / size);
        }

        var bits = int.Log2(size);
        _reversed = new int[size];
        for (var i = 0; i < size; i++)
        {
            var reversed = 0;
            for (var bit = 0; bit < bits; bit++)
                reversed |= ((i >> bit) & 1) << (bits - 1 - bit);
            _reversed[i] = reversed;
        }

        _window = new float[size];
        for (var i = 0; i < size; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));

        _re = new float[size];
        _im = new float[size];
    }

    /// <summary>
    /// Transforms <paramref name="re"/> and <paramref name="im"/> in place.
    /// </summary>
    public void Transform(Span<float> re, Span<float> im)
    {
        var n = Size;
        for (var i = 0; i < n; i++)
        {
            var j = _reversed[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length *= 2)
        {
            var half = length / 2;
            var step = n / length;
            for (var start = 0; start < n; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    /// <summary>
    /// The magnitudes of bins 0 to Size/2 of <paramref name="frame"/> (Size samples) under a Hann window, scaled so
    /// a full-scale sine centred on a bin measures about 1.
    /// </summary>
    public void Magnitudes(ReadOnlySpan<float> frame, Span<float> magnitudes)
    {
        for (var i = 0; i < Size; i++)
        {
            _re[i] = frame[i] * _window[i];
            _im[i] = 0;
        }
        Transform(_re, _im);

        // A Hann window sums to Size/2, and a sine splits between positive and negative frequencies.
        var scale = 4f / Size;
        for (var i = 0; i <= Size / 2; i++)
            magnitudes[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) * scale;
    }
}
