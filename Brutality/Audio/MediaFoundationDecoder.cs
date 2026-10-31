using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;

namespace Brutality.Audio;

/// <summary>
/// Decodes audio with Windows' own Media Foundation codecs, for the formats SDL_mixer has no decoder for: chiefly
/// AAC in .m4a files, but also WMA and anything else Windows can play. NAudio's MediaFoundationReader does the COM
/// work; this collects its output as float samples.
/// </summary>
[SupportedOSPlatform("windows")]
static class MediaFoundationDecoder
{
    /// <summary>
    /// Decodes all of <paramref name="path"/> to 32-bit float samples.
    /// </summary>
    public static PcmAudio Decode(string path)
    {
        var collector = new PcmCollector();
        Stream(path, collector);
        return collector.ToPcm();
    }

    /// <summary>
    /// Decodes all of <paramref name="path"/> into <paramref name="sink"/> as 32-bit float samples.
    /// </summary>
    public static void Stream(string path, IPcmSink sink)
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

        sink.Start(format.Channels, format.SampleRate);

        // Length is only an estimate for compressed files, so progress is clamped rather than trusted.
        var length = Math.Max(1, reader.Length);
        var frameBytes = sizeof(float) * format.Channels;
        var bytes = new byte[64 * 1024];
        var held = 0;
        while (true)
        {
            var read = reader.Read(bytes, held, bytes.Length - held);
            if (read == 0)
                break;
            held += read;

            // Reads needn't end on a whole frame, so any part frame waits for the next read.
            var whole = held - held % frameBytes;
            var progress = Math.Clamp((float)reader.Position / length, 0, 1);
            sink.Write(MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole)), progress);
            bytes.AsSpan(whole, held - whole).CopyTo(bytes);
            held -= whole;
        }
    }
}
