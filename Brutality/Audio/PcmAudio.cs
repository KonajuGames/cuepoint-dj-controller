using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace Brutality.Audio;

/// <summary>
/// A whole song decoded to interleaved 32-bit float samples: what a deck plays, and what its waveform is drawn from.
/// </summary>
sealed record PcmAudio(float[] Samples, int Channels, int SampleRate)
{
    public long Frames => Samples.Length / Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>
    /// Hands the samples to SDL_mixer, which keeps its own copy.
    /// </summary>
    public unsafe MixAudio ToMixAudio(MixMixer mixer)
    {
        var spec = new SdlAudioSpec
        {
            format = SdlAudioFormat.F32,
            channels = Channels,
            freq = SampleRate,
        };
        fixed (float* data = Samples)
            return mixer.LoadRawAudio(data, (nuint)(Samples.Length * sizeof(float)), &spec);
    }
}

/// <summary>
/// Receives a song's samples a chunk at a time as it decodes, so they can be measured without holding them all.
/// </summary>
interface IPcmSink
{
    /// <summary>
    /// Called once, before any samples, with the format they'll come in.
    /// </summary>
    void Start(int channels, int sampleRate);

    /// <summary>
    /// The next interleaved samples, always whole frames. <paramref name="progress"/> is roughly how far through the
    /// file decoding has got, from 0 to 1.
    /// </summary>
    void Write(ReadOnlySpan<float> samples, float progress);
}

/// <summary>
/// Gathers a whole song's samples into a <see cref="PcmAudio"/>.
/// </summary>
sealed class PcmCollector : IPcmSink
{
    float[] _samples = [];
    int _filled;
    int _channels = 1;
    int _sampleRate = 1;

    public void Start(int channels, int sampleRate)
    {
        (_channels, _sampleRate) = (channels, sampleRate);
        // A minute of audio to start with, doubling as needed.
        _samples = new float[channels * sampleRate * 60];
        _filled = 0;
    }

    public void Write(ReadOnlySpan<float> samples, float progress)
    {
        if (_filled + samples.Length > _samples.Length)
            Array.Resize(ref _samples, Math.Max(_samples.Length * 2, _filled + samples.Length));
        samples.CopyTo(_samples.AsSpan(_filled));
        _filled += samples.Length;
    }

    public PcmAudio ToPcm()
    {
        Array.Resize(ref _samples, _filled);
        return new PcmAudio(_samples, _channels, _sampleRate);
    }
}

/// <summary>
/// Decodes with SDL_mixer's own decoders (MP3, WAV, FLAC, OGG and a few older formats) to float samples, rather than
/// letting it decode straight into a clip, so the samples are there for drawing a waveform and analysis.
/// </summary>
static unsafe class SdlPcmDecoder
{
    // SDL_mixer's metadata property names. The bindings only mention them in doc comments, so they're spelled out.
    const string TITLE_PROPERTY = "SDL_mixer.metadata.title";
    const string ARTIST_PROPERTY = "SDL_mixer.metadata.artist";

    // Asking for the whole song in one call is about 15 times slower (4.6 s against 0.27 s for a 3:43 MP3), and
    // chunks from 16 KB to 1 MB all run at about the same speed.
    const int DECODE_CHUNK_BYTES = 64 * 1024;

    /// <summary>
    /// Decodes all of <paramref name="path"/> at its own channel count and sample rate into <paramref name="sink"/>,
    /// returning any title and artist it's tagged with. Throws <see cref="SdlException"/> if SDL_mixer has no
    /// decoder for it.
    /// </summary>
    public static (string? Title, string? Artist) Stream(string path, IPcmSink sink)
    {
        // Through our own stream, because MIX_CreateAudioDecoder leaves the file open when it fails, which locks it
        // until the app quits. Once the decoder exists it owns the stream and closes it.
        var io = Sdl.PInvoke.IOFromFile(path, "rb");
        if (io == IntPtr.Zero)
            throw new SdlException($"Couldn't open {path}: {Sdl.PInvoke.GetError()}");
        MixAudioDecoder decoder;
        try
        {
            decoder = SdlMixer.CreateAudioDecoderIO(io, true, 0);
        }
        catch
        {
            Sdl.PInvoke.CloseIO(io);
            throw;
        }

        try
        {
            SdlAudioSpec native;
            decoder.GetFormat(&native);
            var spec = new SdlAudioSpec
            {
                format = SdlAudioFormat.F32,
                channels = native.channels,
                freq = native.freq,
            };

            var properties = decoder.GetProperties();
            var title = Sdl.PInvoke.GetStringProperty(properties, TITLE_PROPERTY, "");
            var artist = Sdl.PInvoke.GetStringProperty(properties, ARTIST_PROPERTY, "");

            // How far through the file the decoder has read is the best measure of progress there is.
            var fileSize = Math.Max(1, Sdl.PInvoke.GetIOSize(io));

            sink.Start(spec.channels, spec.freq);
            var buffer = new float[DECODE_CHUNK_BYTES / sizeof(float)];
            fixed (float* data = buffer)
            {
                while (true)
                {
                    var bytes = decoder.DecodeAudio(data, DECODE_CHUNK_BYTES, &spec);
                    if (bytes < 0)
                        throw new SdlException($"MIX_DecodeAudio failed: {Sdl.PInvoke.GetError()}");
                    if (bytes == 0)
                        break;
                    var samples = bytes / sizeof(float);
                    var progress = Math.Clamp((float)Sdl.PInvoke.TellIO(io) / fileSize, 0, 1);
                    sink.Write(buffer.AsSpan(0, samples - samples % spec.channels), progress);
                }
            }
            return (title, artist);
        }
        finally
        {
            decoder.Destroy();
        }
    }
}
