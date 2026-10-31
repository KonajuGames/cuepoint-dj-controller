namespace Brutality.Audio.Analysis;

/// <summary>
/// What analysing a song found: its beat grid, how loud it is, and its key.
/// </summary>
/// <param name="Grid">Where the beats fall: one steady tempo, or several where the song changes tempo.</param>
/// <param name="Loudness">Integrated loudness, in LUFS.</param>
/// <param name="Peak">The largest sample, where 1 is full scale.</param>
sealed record TrackAnalysis(BeatGrid Grid, double Loudness, float Peak, MusicalKey Key)
{
    /// <summary>
    /// The loudness every song is brought to: the level streaming services play at. Most modern masters are louder
    /// and come down to it; older and quieter ones come up, as far as their peaks allow.
    /// </summary>
    public const double TARGET_LOUDNESS = -14;

    public const double MAX_BOOST_DB = 12;
    public const double MAX_CUT_DB = 24;

    /// <summary>
    /// The gain, in dB, that brings the song to <see cref="TARGET_LOUDNESS"/>. A boost stops where the song's peak
    /// would reach full scale, so it never clips.
    /// </summary>
    public double AutoGainDb
    {
        get
        {
            var gain = Math.Clamp(TARGET_LOUDNESS - Loudness, -MAX_CUT_DB, MAX_BOOST_DB);
            if (gain > 0 && Peak > 0)
                gain = Math.Max(0, Math.Min(gain, -20 * Math.Log10(Peak)));
            return gain;
        }
    }

    /// <summary>
    /// <see cref="AutoGainDb"/> as a factor to multiply samples by.
    /// </summary>
    public float AutoGain => (float)Math.Pow(10, AutoGainDb / 20);

    /// <summary>
    /// The song's tempo, in beats per minute, between <see cref="BeatDetector.MIN_BPM"/> and twice it: the one that
    /// covers most of it, if it varies.
    /// </summary>
    public double Bpm => Grid.MainBpm;
}

/// <summary>
/// Analyses a song for its <see cref="TrackAnalysis"/>. Decodes it once, measuring loudness on the full-quality
/// samples as they come, and keeping a mono copy at about 11 kHz (a few megabytes rather than the tens a full decode
/// takes) for finding the beats and key. Safe to run on several threads at once.
/// </summary>
static class TrackAnalyzer
{
    public const int ANALYSIS_RATE = 11025;

    // Roughly how the time goes: decoding first, then the beats, then the key.
    const float DECODED = 0.85f;
    const float BEATS_FOUND = 0.97f;

    /// <summary>
    /// Decodes and analyses <paramref name="path"/>, reporting progress from 0 to 1 as it goes. Throws if the file
    /// can't be decoded, and <see cref="OperationCanceledException"/> if <paramref name="cancel"/> fires.
    /// </summary>
    public static TrackAnalysis Analyse(string path, Action<float>? progress = null, CancellationToken cancel = default)
    {
        var sink = new AnalysisSink(progress, cancel);
        AudioDecoder.Stream(path, sink);
        return sink.Finish(progress, cancel);
    }

    /// <summary>
    /// Analyses samples already decoded.
    /// </summary>
    public static TrackAnalysis Analyse(PcmAudio audio, CancellationToken cancel = default)
    {
        var sink = new AnalysisSink(null, cancel);
        sink.Start(audio.Channels, audio.SampleRate);
        sink.Write(audio.Samples, 1);
        return sink.Finish(null, cancel);
    }

    sealed class AnalysisSink(Action<float>? progress, CancellationToken cancel) : IPcmSink
    {
        LoudnessMeter? _loudness;
        int _channels = 1;
        int _factor = 1;
        int _rate = ANALYSIS_RATE;

        // A 4th-order Butterworth low-pass ahead of dropping samples, so what's above the new Nyquist doesn't fold
        // back down.
        Biquad _lowPass1;
        Biquad _lowPass2;
        int _phase;
        float[] _mono = [];
        int _monoFilled;

        public void Start(int channels, int sampleRate)
        {
            _channels = channels;
            _loudness = new LoudnessMeter(channels, sampleRate);
            _factor = Math.Max(1, (int)Math.Round((double)sampleRate / ANALYSIS_RATE));
            _rate = sampleRate / _factor;
            var cutoff = 0.4 * _rate;
            _lowPass1 = Biquad.LowPass(cutoff, 0.5412, sampleRate);
            _lowPass2 = Biquad.LowPass(cutoff, 1.3066, sampleRate);
            _mono = new float[_rate * 60 * 5];
        }

        public void Write(ReadOnlySpan<float> samples, float fraction)
        {
            cancel.ThrowIfCancellationRequested();
            _loudness!.Write(samples);

            for (var i = 0; i + _channels <= samples.Length; i += _channels)
            {
                var sum = 0f;
                for (var c = 0; c < _channels; c++)
                    sum += samples[i + c];
                var filtered = _lowPass2.Process(_lowPass1.Process(sum / _channels));
                if (++_phase < _factor)
                    continue;
                _phase = 0;
                if (_monoFilled == _mono.Length)
                    Array.Resize(ref _mono, _mono.Length * 2);
                _mono[_monoFilled++] = (float)filtered;
            }
            progress?.Invoke(fraction * DECODED);
        }

        public TrackAnalysis Finish(Action<float>? progress, CancellationToken cancel)
        {
            if (_loudness is null)
                throw new InvalidOperationException("The song had no audio");

            var mono = _mono.AsSpan(0, _monoFilled);
            var beats = BeatDetector.Detect(mono, _rate, cancel);
            progress?.Invoke(BEATS_FOUND);
            var key = KeyDetector.Detect(mono, _rate, cancel);
            progress?.Invoke(1);

            return new TrackAnalysis(beats.Grid, _loudness.IntegratedLoudness, _loudness.Peak, key.Key);
        }
    }
}
