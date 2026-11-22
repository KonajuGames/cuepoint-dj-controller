using Cuepoint.Audio.Analysis;
using Microsoft.ML.OnnxRuntime;

namespace Cuepoint.Audio.Stems;

/// <summary>
/// Meta's Demucs (htdemucs, the hybrid transformer model), run with ONNX Runtime: on the graphics card through
/// DirectML while nothing's playing, about 10 times as fast, and on the processor otherwise. It separates
/// stereo audio at 44.1 kHz into drums, bass, vocals and everything else, 7.8 seconds at a time with a quarter of
/// each overlapping the next, as Demucs's own apply_model does.
/// </summary>
/// <remarks>
/// The ONNX model (tools/export_demucs.py) is htdemucs without the STFT at its input and the inverse STFT at its
/// output, which ONNX can't do: they're done here, exactly as Demucs does them, so it gives the same stems as Demucs
/// itself (without its random shifts). On the processor it uses at most half its cores, so the rest of the PC, and
/// the decks, carry on. The graphics card isn't used while a deck's playing: each segment there holds it for a fifth
/// of a second, and the screen, waiting for it, stutters.
/// </remarks>
sealed class Demucs : IDisposable
{
    const int RATE = 44100;

    // A segment: 7.8 seconds, the length htdemucs was trained on.
    const int SEGMENT = 343980;
    const double OVERLAP = 0.25;

    const int NFFT = 4096;
    const int HOP = NFFT / 4;
    const int BINS = NFFT / 2;
    const int FRAMES = (SEGMENT + HOP - 1) / HOP;

    // The signal's padded by 3/2 of a hop at the start before its STFT, so the frames line up with the time branch.
    const int PAD = HOP / 2 * 3;

    // The STFT is normalised by √NFFT, and the inverse undoes it and its own 1/NFFT. A periodic Hann window's square,
    // overlapped at a quarter of its length, sums to 1.5 everywhere.
    const float STFT_SCALE = 1f / 64;
    const float ISTFT_SCALE = 1f / (64 * 1.5f);

    // The model's sources, in its order.
    const int SOURCES = 4;
    const int DRUMS = 0;
    const int BASS = 1;
    const int VOCALS = 3;

    /// <summary>
    /// How many of the processor's cores separating uses, when it's on the processor.
    /// </summary>
    public static readonly int Threads = Math.Max(1, Environment.ProcessorCount / 2);

    // Far louder than any stem can be, with the song normalised to a standard deviation of 1 (the graphics card going
    // wrong gives thousands): it's gone wrong.
    const float IMPOSSIBLE = 200;

    readonly string _modelPath;
    InferenceSession? _gpu;
    InferenceSession? _cpu;
    readonly float[] _window = new float[NFFT];

    // The STFT's done on as many threads as the processor would use for the model, each with its own transform.
    static readonly ParallelOptions Parallelism = new() { MaxDegreeOfParallelism = Threads };

    sealed class Transform
    {
        public readonly Fft Fft = new(NFFT);
        public readonly float[] Re = new float[NFFT];
        public readonly float[] Im = new float[NFFT];
    }

    readonly System.Collections.Concurrent.ConcurrentBag<Transform> _transforms = [];

    Transform TakeTransform() => _transforms.TryTake(out var transform) ? transform : new Transform();
    readonly float[] _spec = new float[4 * BINS * FRAMES];

    /// <summary>
    /// Whether the graphics card can be used: it started, and hasn't gone wrong.
    /// </summary>
    public bool HasGpu => _gpu is not null;

    /// <param name="gpu">Whether to use the graphics card when it can be.</param>
    public Demucs(string modelPath, bool gpu = true)
    {
        _modelPath = modelPath;
        for (var i = 0; i < NFFT; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFFT));

        if (gpu && OperatingSystem.IsWindows())
        {
            try
            {
                var options = new SessionOptions
                {
                    // DirectML needs these. Its graph fusion, compiling runs of the model into one, gets this model
                    // wrong (it gives wild values), so it's left out; the rest of DirectML is right.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };
                options.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
                options.AppendExecutionProvider_DML(0);
                _gpu = new InferenceSession(modelPath, options);
            }
            catch (OnnxRuntimeException e)
            {
                ErrorLog.Write("starting stem separation on the graphics card, so it's on the processor", e);
            }
        }

    }

    // The processor's, loaded the first time it's needed.
    InferenceSession Cpu =>
        _cpu ??= new InferenceSession(
            _modelPath,
            new SessionOptions
            {
                IntraOpNumThreads = Threads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            }
        );

    /// <summary>
    /// Separates <paramref name="audio"/> into its vocals, drums and bass, in its own format, reporting how far it's
    /// got from 0 to 1 as it goes. Each segment's on the graphics card unless <paramref name="busy"/> says something's
    /// playing. If the graphics card gives values no song could have, it's never used again, and this throws
    /// <see cref="GpuWentWrongException"/> for the song to be separated afresh.
    /// </summary>
    public SongStems Separate(PcmAudio audio, Action<float>? progress = null, Func<bool>? busy = null)
    {
        // Stereo at 44.1 kHz, normalised by the mono mix's level as Demucs's separate does.
        var mix = ToModel(audio);
        var length = mix[0].Length;
        var (mean, std) = Level(mix);
        foreach (var channel in mix)
            for (var i = 0; i < length; i++)
                channel[i] = (channel[i] - mean) / std;

        var sources = new float[SOURCES][][];
        for (var s = 0; s < SOURCES; s++)
            sources[s] = [new float[length], new float[length]];
        var weights = new float[length];

        // Each segment's weighed most in its middle and least at its ends, where it overlaps its neighbours.
        var weight = new float[SEGMENT];
        for (var i = 0; i < SEGMENT; i++)
            weight[i] = (float)Math.Min(i + 1, SEGMENT - i) / (SEGMENT / 2);

        var stride = (int)((1 - OVERLAP) * SEGMENT);
        var segments = Math.Max(1, (length + stride - 1) / stride);
        var input = new float[2 * SEGMENT];
        for (var n = 0; n < segments; n++)
        {
            var offset = n * stride;
            var chunk = Math.Min(SEGMENT, length - offset);

            // A short last segment is centred in a whole one, with the song either side of it where there is some.
            var delta = SEGMENT - chunk;
            var start = offset - delta / 2;
            for (var c = 0; c < 2; c++)
                for (var i = 0; i < SEGMENT; i++)
                {
                    var at = start + i;
                    input[c * SEGMENT + i] = at >= 0 && at < length ? mix[c][at] : 0;
                }

            var output = Run(input, _gpu is { } gpu && busy?.Invoke() != true ? gpu : Cpu);
            for (var s = 0; s < SOURCES; s++)
                for (var c = 0; c < 2; c++)
                {
                    var from = (s * 2 + c) * SEGMENT + delta / 2;
                    var to = sources[s][c];
                    for (var i = 0; i < chunk; i++)
                        to[offset + i] += weight[i] * output[from + i];
                }
            for (var i = 0; i < chunk; i++)
                weights[offset + i] += weight[i];
            progress?.Invoke((float)(n + 1) / segments);
        }

        foreach (var source in sources)
            foreach (var channel in source)
                for (var i = 0; i < length; i++)
                    channel[i] = channel[i] / weights[i] * std + mean;

        return new SongStems(ToSong(sources[VOCALS], audio), ToSong(sources[DRUMS], audio), ToSong(sources[BASS], audio), audio.Channels, audio.SampleRate);
    }

    /// <summary>
    /// One segment through the model: its two channels one after the other, to each source's two channels one after
    /// the other.
    /// </summary>
    float[] Run(float[] mix, InferenceSession session)
    {
        Spectrogram(mix, _spec);

        using var mixValue = OrtValue.CreateTensorValueFromMemory(mix, [1, 2, SEGMENT]);
        using var specValue = OrtValue.CreateTensorValueFromMemory(_spec, [1, 4, BINS, FRAMES]);
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, ["mix", "spec"], [mixValue, specValue], ["spec_out", "time_out"]);
        var specOut = outputs[0];
        var result = outputs[1].GetTensorDataAsSpan<float>().ToArray();

        // The time branch, plus the frequency branch back from its spectrogram.
        Parallel.For(0, SOURCES * 2, Parallelism, sc => InverseSpectrogram(specOut.GetTensorDataAsSpan<float>(), sc / 2, sc % 2, result.AsSpan(sc * SEGMENT, SEGMENT)));
        if (session == _gpu && Wild(result))
        {
            _gpu.Dispose();
            _gpu = null;
            throw new GpuWentWrongException();
        }
        return result;
    }

    /// <summary>
    /// Demucs's STFT of a segment: each channel's real and imaginary parts as channels of their own, of every bin but
    /// the last, at each frame. The segment's reflected at its ends for the frames that run past them.
    /// </summary>
    void Spectrogram(float[] mix, float[] spec)
    {
        // A block of frames of one channel at a time.
        const int BLOCK = 32;
        var blocks = (FRAMES + BLOCK - 1) / BLOCK;
        Parallel.For(
            0,
            2 * blocks,
            Parallelism,
            cb =>
            {
                var transform = TakeTransform();
                var (re, im) = (transform.Re, transform.Im);
                var c = cb / blocks;
                var real = c * 2 * BINS * FRAMES;
                var imaginary = real + BINS * FRAMES;
                for (var frame = cb % blocks * BLOCK; frame < Math.Min(FRAMES, (cb % blocks + 1) * BLOCK); frame++)
                {
                    var first = frame * HOP - PAD;
                    for (var n = 0; n < NFFT; n++)
                    {
                        var at = first + n;
                        if (at < 0)
                            at = -at;
                        else if (at >= SEGMENT)
                            at = 2 * (SEGMENT - 1) - at;
                        re[n] = mix[c * SEGMENT + at] * _window[n];
                        im[n] = 0;
                    }
                    transform.Fft.Transform(re, im);
                    for (var k = 0; k < BINS; k++)
                    {
                        spec[real + k * FRAMES + frame] = re[k] * STFT_SCALE;
                        spec[imaginary + k * FRAMES + frame] = im[k] * STFT_SCALE;
                    }
                }
                _transforms.Add(transform);
            }
        );
    }

    /// <summary>
    /// Adds one source's channel, back from its spectrogram, to <paramref name="into"/>.
    /// </summary>
    void InverseSpectrogram(ReadOnlySpan<float> spec, int source, int channel, Span<float> into)
    {
        var transform = TakeTransform();
        var (re, im) = (transform.Re, transform.Im);
        var real = (source * 4 + channel * 2) * BINS * FRAMES;
        var imaginary = real + BINS * FRAMES;
        for (var frame = 0; frame < FRAMES; frame++)
        {
            // The whole spectrum from its positive half (the top bin's zero), conjugated, so a forward transform
            // inverts it.
            for (var k = 0; k < BINS; k++)
            {
                re[k] = spec[real + k * FRAMES + frame];
                im[k] = -spec[imaginary + k * FRAMES + frame];
            }
            re[BINS] = im[BINS] = 0;
            for (var k = 1; k < BINS; k++)
            {
                re[NFFT - k] = re[k];
                im[NFFT - k] = -im[k];
            }
            transform.Fft.Transform(re, im);

            var first = frame * HOP - PAD;
            for (var n = Math.Max(0, -first); n < NFFT && first + n < SEGMENT; n++)
                into[first + n] += re[n] * _window[n] * ISTFT_SCALE;
        }
        _transforms.Add(transform);
    }

    // Whether any value is impossible for a separated song: not a number, or far too loud.
    static bool Wild(ReadOnlySpan<float> values)
    {
        foreach (var value in values)
            if (!(MathF.Abs(value) < IMPOSSIBLE))
                return true;
        return false;
    }

    // The mean and standard deviation of the mono mix.
    static (float Mean, float Std) Level(float[][] mix)
    {
        var length = mix[0].Length;
        double sum = 0;
        for (var i = 0; i < length; i++)
            sum += (mix[0][i] + mix[1][i]) / 2;
        var mean = sum / Math.Max(1, length);
        double squares = 0;
        for (var i = 0; i < length; i++)
        {
            var d = (mix[0][i] + mix[1][i]) / 2 - mean;
            squares += d * d;
        }
        var std = Math.Sqrt(squares / Math.Max(1, length - 1));
        return ((float)mean, (float)Math.Max(std, 1e-8));
    }

    // The song as the model wants it: two channels (mono doubled, beyond two dropped) at 44.1 kHz.
    static float[][] ToModel(PcmAudio audio)
    {
        var (frames, channels) = (audio.Frames, audio.Channels);
        var stereo = new float[2][];
        for (var c = 0; c < 2; c++)
        {
            var from = Math.Min(c, channels - 1);
            var channel = new float[frames];
            for (long i = 0; i < frames; i++)
                channel[i] = audio.Samples[i * channels + from];
            stereo[c] = audio.SampleRate == RATE ? channel : Resample(channel, audio.SampleRate, RATE, (long)Math.Round((double)frames * RATE / audio.SampleRate));
        }
        return stereo;
    }

    // A stem back in the song's own format, interleaved: mono the two channels' average, beyond two silent.
    static float[] ToSong(float[][] stem, PcmAudio audio)
    {
        var (frames, channels) = (audio.Frames, audio.Channels);
        var (left, right) = audio.SampleRate == RATE && stem[0].Length == frames
            ? (stem[0], stem[1])
            : (Resample(stem[0], RATE, audio.SampleRate, frames), Resample(stem[1], RATE, audio.SampleRate, frames));
        var samples = new float[frames * channels];
        for (long i = 0; i < frames; i++)
        {
            if (channels == 1)
                samples[i] = (left[i] + right[i]) / 2;
            else
                (samples[i * channels], samples[i * channels + 1]) = (left[i], right[i]);
        }
        return samples;
    }

    // Straight-line resampling: rough, but whatever it gets wrong goes into the instruments, which are the song less
    // the other stems, so the song as a whole is untouched.
    static float[] Resample(float[] from, int fromRate, int toRate, long length)
    {
        var to = new float[length];
        var step = (double)fromRate / toRate;
        for (long i = 0; i < length; i++)
        {
            var at = i * step;
            var j = (long)at;
            if (j >= from.Length - 1)
            {
                to[i] = from.Length > 0 ? from[^1] : 0;
                continue;
            }
            to[i] = from[j] + (from[j + 1] - from[j]) * (float)(at - j);
        }
        return to;
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _cpu?.Dispose();
    }
}

/// <summary>
/// The graphics card gave values no song's stems could have, so the song's to be separated on the processor instead.
/// </summary>
sealed class GpuWentWrongException() : Exception("The graphics card's stem separation went wrong");
