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
/// Decodes with SDL_mixer's own decoders (MP3, WAV, FLAC, OGG and a few older formats) to float samples, rather than
/// letting it decode straight into a clip, so the samples are there for drawing a waveform.
/// </summary>
static unsafe class SdlPcmDecoder
{
    // SDL_mixer's metadata property names. The bindings only mention them in doc comments, so they're spelled out.
    const string TITLE_PROPERTY = "SDL_mixer.metadata.title";
    const string ARTIST_PROPERTY = "SDL_mixer.metadata.artist";

    const int DECODE_CHUNK_BYTES = 64 * 1024;

    /// <summary>
    /// Decodes all of <paramref name="path"/> at its own channel count and sample rate, with any title and artist
    /// it's tagged with. Throws <see cref="SdlException"/> if SDL_mixer has no decoder for it.
    /// </summary>
    public static (PcmAudio Audio, string? Title, string? Artist) Decode(string path)
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

            // A minute of audio to start with, doubling as needed.
            var samples = new float[spec.channels * spec.freq * 60];
            var filled = 0;
            while (true)
            {
                if (filled == samples.Length)
                    Array.Resize(ref samples, samples.Length * 2);

                fixed (float* buffer = &samples[filled])
                {
                    // In modest chunks: asking for the whole song in one call is about 15 times slower (4.6 s against
                    // 0.27 s for a 3:43 MP3), and chunks from 16 KB to 1 MB all run at about the same speed.
                    var room = Math.Min(DECODE_CHUNK_BYTES, (samples.Length - filled) * sizeof(float));
                    var bytes = decoder.DecodeAudio(buffer, room, &spec);
                    if (bytes < 0)
                        throw new SdlException($"MIX_DecodeAudio failed: {Sdl.PInvoke.GetError()}");
                    if (bytes == 0)
                        break;
                    filled += bytes / sizeof(float);
                }
            }

            Array.Resize(ref samples, filled - filled % spec.channels);
            return (new PcmAudio(samples, spec.channels, spec.freq), title, artist);
        }
        finally
        {
            decoder.Destroy();
        }
    }
}
