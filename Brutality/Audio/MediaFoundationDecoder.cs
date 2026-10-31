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
        using var reader = new MediaFoundationReader(
            path,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true }
        );

        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            throw new NotSupportedException(
                $"Media Foundation gave {format.Encoding} at {format.BitsPerSample} bits, not 32-bit float"
            );

        // Length is only an estimate for compressed files, so it sizes the first buffer rather than limiting the read.
        var bytes = new byte[Math.Max(reader.Length, 1 << 20)];
        var filled = 0;
        while (true)
        {
            if (filled == bytes.Length)
                Array.Resize(ref bytes, bytes.Length * 2);

            var read = reader.Read(bytes.AsSpan(filled));
            if (read == 0)
                break;
            filled += read;
        }

        var frameBytes = sizeof(float) * format.Channels;
        var samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, filled - filled % frameBytes)).ToArray();
        return new PcmAudio(samples, format.Channels, format.SampleRate);
    }
}
