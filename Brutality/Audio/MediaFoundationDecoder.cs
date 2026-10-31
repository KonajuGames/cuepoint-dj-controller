using System.Runtime.Versioning;
using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;
using NAudio.Wave;

namespace Brutality.Audio;

/// <summary>
/// Decodes audio with Windows' own Media Foundation codecs, for the formats SDL_mixer has no decoder for: chiefly
/// AAC in .m4a files, but also WMA and anything else Windows can play. NAudio's MediaFoundationReader does the COM
/// work; this turns its output into an SDL_mixer audio clip.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class MediaFoundationDecoder
{
    /// <summary>
    /// Decodes all of <paramref name="path"/> to 32-bit float samples and hands them to <paramref name="mixer"/>,
    /// which keeps its own copy.
    /// </summary>
    public static MixAudio Load(MixMixer mixer, string path)
    {
        using var reader = new MediaFoundationReader(
            path,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true }
        );

        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            throw new NotSupportedException(
                $"Media Foundation gave {format.Encoding} at {format.BitsPerSample} bits, not 32-bit float"
            );

        var samples = ReadAll(reader);
        var spec = new SdlAudioSpec
        {
            format = SdlAudioFormat.F32,
            channels = format.Channels,
            freq = format.SampleRate,
        };

        fixed (byte* data = samples.AsSpan())
            return mixer.LoadRawAudio(data, (nuint)samples.Count, &spec);
    }

    /// <summary>
    /// Reads to the end. Length is only an estimate for compressed files, so it sizes the first buffer rather than
    /// limiting the read.
    /// </summary>
    static ArraySegment<byte> ReadAll(MediaFoundationReader reader)
    {
        var buffer = new byte[Math.Max(reader.Length, 1 << 20)];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);

            var read = reader.Read(buffer.AsSpan(filled));
            if (read == 0)
                break;
            filled += read;
        }
        return new ArraySegment<byte>(buffer, 0, filled);
    }
}
