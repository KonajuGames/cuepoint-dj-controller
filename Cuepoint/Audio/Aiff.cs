using System.Buffers.Binary;
using System.Text;

namespace Cuepoint.Audio;

/// <summary>
/// AIFF and AIFF-C, decoded and tagged here rather than by SDL_mixer, which reads on past the sound data into
/// whatever chunk follows it (the ID3 tag, as iTunes, Rekordbox and Serato put it, which plays as a burst of noise at
/// the end) and gets 32-bit float wrong; and Windows reads no tags or length from them at all.
/// <para>
/// An AIFF is a "FORM" chunk holding "AIFF" or "AIFC" and then chunks, each four letters, a big-endian length and its
/// bytes, padded to an even length: "COMM" for the format, "SSND" for the samples, and "ID3 " (or "NAME" and "AUTH")
/// for the tags, in any order.
/// </para>
/// </summary>
static class Aiff
{
    const int DECODE_CHUNK_BYTES = 64 * 1024;

    /// <summary>
    /// The biggest ID3 chunk read: room for cover art, but not for something that isn't really a tag.
    /// </summary>
    const int MAX_TAG_BYTES = 32 << 20;

    public static bool IsAiff(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".aif" or ".aiff" or ".aifc";

    /// <summary>
    /// Decodes all of <paramref name="path"/> into <paramref name="sink"/>, returning its title and artist if it's
    /// tagged with them. Throws <see cref="NotSupportedException"/>, before starting the sink, for a compressed AIFF-C
    /// (μ-law, IMA ADPCM and the like) this doesn't decode, and <see cref="InvalidDataException"/> for a file that
    /// isn't an AIFF.
    /// </summary>
    public static (string? Title, string? Artist) Stream(string path, IPcmSink sink)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var layout = Read(file);
        if (layout.Format is not { } format || layout.SoundStart < 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} has no {(layout.Format is null ? "COMM" : "SSND")} chunk");
        var convert = Converter(format);

        var frameBytes = format.Channels * format.BytesPerSample;
        var frames = Math.Min(format.Frames, layout.SoundLength / frameBytes);
        var tags = layout.Tags(file);

        sink.Start(format.Channels, format.SampleRate);
        file.Position = layout.SoundStart;
        var bytes = new byte[DECODE_CHUNK_BYTES - DECODE_CHUNK_BYTES % frameBytes];
        var samples = new float[bytes.Length / format.BytesPerSample];
        var left = frames * frameBytes;
        while (left > 0)
        {
            var wanted = (int)Math.Min(bytes.Length, left);
            var read = file.ReadAtLeast(bytes.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            var whole = read - read % frameBytes;
            if (whole == 0)
                break;
            var count = whole / format.BytesPerSample;
            convert(bytes.AsSpan(0, whole), samples.AsSpan(0, count));
            left -= whole;
            sink.Write(samples.AsSpan(0, count), 1 - (float)left / Math.Max(1, frames * frameBytes));
        }
        return (tags.Title, tags.Artist);
    }

    /// <summary>
    /// The file's tags and length, without decoding it; all null if it can't be read or isn't an AIFF.
    /// </summary>
    public static SongTags ReadTags(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var layout = Read(file);
            var tags = layout.Tags(file);
            TimeSpan? duration = layout.Format is { SampleRate: > 0 } format
                ? TimeSpan.FromSeconds((double)format.Frames / format.SampleRate)
                : null;
            return tags with { Duration = duration };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return default;
        }
    }

    /// <summary>
    /// The file's "ID3 " chunk, its ID3 tag's ten-byte header and then the tag; null if it has none or can't be read.
    /// Serato keeps its cues there, as it does in an MP3's ID3 tag.
    /// </summary>
    public static byte[]? ReadId3(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var layout = Read(file);
            return layout.Id3Start < 0 ? null : ReadAt(file, layout.Id3Start, layout.Id3Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    readonly record struct Format(int Channels, long Frames, int Bits, int SampleRate, string Compression)
    {
        public int BytesPerSample => (Bits + 7) / 8;
    }

    /// <summary>
    /// Where things are in the file: its format, its samples and its tag chunks; a start of -1 for any it hasn't got.
    /// </summary>
    sealed record Layout(Format? Format, long SoundStart, long SoundLength, long Id3Start, int Id3Length, string? Name, string? Author)
    {
        public SongTags Tags(Stream file)
        {
            var fromId3 = Id3Start >= 0 && ReadAt(file, Id3Start, Id3Length) is { } id3 ? Id3Text.Read(id3) : default;
            return new SongTags(fromId3.Title ?? Name, fromId3.Artist ?? Author, fromId3.Album, null);
        }
    }

    static byte[] ReadAt(Stream file, long start, int length)
    {
        file.Position = start;
        var bytes = new byte[length];
        file.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// Walks the chunks. A chunk running past the end of the file (a file cut short, or a length a streaming
    /// recorder never went back to fill in) is taken to end with the file.
    /// </summary>
    static Layout Read(Stream file)
    {
        Span<byte> header = stackalloc byte[12];
        if (file.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12 || !header[..4].SequenceEqual("FORM"u8))
            throw new InvalidDataException("Not an AIFF: no FORM chunk");
        var isAifc = header.Slice(8, 4).SequenceEqual("AIFC"u8);
        if (!isAifc && !header.Slice(8, 4).SequenceEqual("AIFF"u8))
            throw new InvalidDataException("Not an AIFF: the FORM isn't AIFF or AIFC");

        Format? format = null;
        long soundStart = -1, soundLength = 0, id3Start = -1;
        int id3Length = 0;
        string? name = null, author = null;
        var length = file.Length;
        while (file.Position + 8 <= length)
        {
            file.ReadExactly(header[..8]);
            var id = Encoding.ASCII.GetString(header[..4]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var start = file.Position;
            size = Math.Min(size, length - start);

            switch (id)
            {
                case "COMM" when size >= 18:
                    format = ReadComm(ReadAt(file, start, (int)Math.Min(size, 256)), isAifc);
                    break;
                case "SSND" when size >= 8:
                    // The offset to the first sample (four bytes), then a block size nothing uses.
                    file.ReadExactly(header[..8]);
                    var offset = BinaryPrimitives.ReadUInt32BigEndian(header);
                    soundStart = start + 8 + offset;
                    soundLength = Math.Max(0, size - 8 - offset);
                    break;
                case "ID3 " or "id3 " when size is >= 10 and <= MAX_TAG_BYTES:
                    (id3Start, id3Length) = (start, (int)size);
                    break;
                case "NAME" when size <= 1024:
                    name = Text(ReadAt(file, start, (int)size));
                    break;
                case "AUTH" when size <= 1024:
                    author = Text(ReadAt(file, start, (int)size));
                    break;
            }
            file.Position = start + size + (size & 1);
        }
        return new Layout(format, soundStart, soundLength, id3Start, id3Length, name, author);
    }

    static string? Text(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes).TrimEnd('\0', ' ');
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// The format: channels (two bytes), frames (four), bits per sample (two), the sample rate as an 80-bit extended
    /// float, and for AIFF-C, the compression's four letters.
    /// </summary>
    static Format ReadComm(ReadOnlySpan<byte> comm, bool isAifc)
    {
        var channels = BinaryPrimitives.ReadInt16BigEndian(comm);
        var frames = BinaryPrimitives.ReadUInt32BigEndian(comm[2..]);
        var bits = BinaryPrimitives.ReadInt16BigEndian(comm[6..]);
        var rate = Extended(comm.Slice(8, 10));
        var compression = isAifc && comm.Length >= 22 ? Encoding.ASCII.GetString(comm.Slice(18, 4)) : "NONE";
        if (channels <= 0 || bits is <= 0 or > 64 || rate is not (>= 1 and <= 1_000_000))
            throw new InvalidDataException($"An AIFF with {channels} channels of {bits} bits at {rate} Hz");
        return new Format(channels, frames, bits, (int)Math.Round(rate), compression);
    }

    /// <summary>
    /// An 80-bit IEEE extended float: a sign bit, a 15-bit exponent and a 64-bit mantissa with its whole-number bit.
    /// </summary>
    internal static double Extended(ReadOnlySpan<byte> bytes)
    {
        var exponent = BinaryPrimitives.ReadUInt16BigEndian(bytes) & 0x7fff;
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        if (exponent == 0 && mantissa == 0)
            return 0;
        var value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (bytes[0] & 0x80) != 0 ? -value : value;
    }

    delegate void Convert(ReadOnlySpan<byte> bytes, Span<float> samples);

    /// <summary>
    /// How to turn the file's samples into floats. Integer samples sit in the top bits of whole bytes (a 20-bit
    /// sample in three bytes), so dividing by the whole bytes' range does for any bit depth.
    /// </summary>
    static Convert Converter(Format format)
    {
        var bytes = format.BytesPerSample;
        switch (format.Compression)
        {
            case "NONE" or "twos" when bytes <= 4:
                return (from, to) => Integers(from, to, bytes, bigEndian: true, signed: true);
            case "sowt" when bytes <= 4:
                return (from, to) => Integers(from, to, bytes, bigEndian: false, signed: true);
            case "raw " when bytes == 1:
                return (from, to) => Integers(from, to, 1, bigEndian: true, signed: false);
            case "fl32" or "FL32" when bytes == 4:
                return (from, to) =>
                {
                    for (var i = 0; i < to.Length; i++)
                        to[i] = BinaryPrimitives.ReadSingleBigEndian(from[(i * 4)..]);
                };
            case "fl64" or "FL64" when bytes == 8:
                return (from, to) =>
                {
                    for (var i = 0; i < to.Length; i++)
                        to[i] = (float)BinaryPrimitives.ReadDoubleBigEndian(from[(i * 8)..]);
                };
            default:
                throw new NotSupportedException($"AIFF-C compression '{format.Compression}' at {format.Bits} bits");
        }
    }

    static void Integers(ReadOnlySpan<byte> from, Span<float> to, int bytes, bool bigEndian, bool signed)
    {
        var scale = 1f / (1L << (bytes * 8 - 1));
        var shift = 32 - bytes * 8;
        for (var i = 0; i < to.Length; i++)
        {
            var sample = from.Slice(i * bytes, bytes);
            var value = 0;
            for (var b = 0; b < bytes; b++)
                value = value << 8 | sample[bigEndian ? b : bytes - 1 - b];
            // Into the top of an int, so its sign comes along, then back down.
            value = signed ? value << shift >> shift : value - (1 << (bytes * 8 - 1));
            to[i] = value * scale;
        }
    }
}

/// <summary>
/// The title, artist, album and genre text frames of an ID3v2 tag (versions 2.2 to 2.4), header and all.
/// </summary>
static class Id3Text
{
    public static SongTags Read(ReadOnlySpan<byte> id3)
    {
        if (id3.Length < 10 || !id3[..3].SequenceEqual("ID3"u8))
            return default;
        var version = id3[3];
        var flags = id3[5];
        if (version is not (2 or 3 or 4))
            return default;
        var size = Math.Min(Syncsafe(id3[6..10]), id3.Length - 10);
        ReadOnlySpan<byte> tag = id3.Slice(10, size);
        // Version 2.3 and before unsynchronise the whole tag, a zero after every 0xFF; 2.4 does it frame by frame.
        if ((flags & 0x80) != 0 && version < 4)
            tag = Resynchronise(tag);

        var at = 0;
        if ((flags & 0x40) != 0 && version >= 3 && tag.Length >= 4)
            at = version == 4 ? Syncsafe(tag[..4]) : BinaryPrimitives.ReadInt32BigEndian(tag) + 4;

        var idLength = version == 2 ? 3 : 4;
        var headerLength = version == 2 ? 6 : 10;
        string? title = null, artist = null, albumArtist = null, album = null, genre = null;
        while (at >= 0 && at + headerLength <= tag.Length && tag[at] != 0)
        {
            var id = Encoding.ASCII.GetString(tag.Slice(at, idLength));
            var length = version switch
            {
                2 => tag[at + 3] << 16 | tag[at + 4] << 8 | tag[at + 5],
                3 => BinaryPrimitives.ReadInt32BigEndian(tag.Slice(at + 4, 4)),
                _ => Syncsafe(tag.Slice(at + 4, 4)),
            };
            if (length <= 0 || at + headerLength + length > tag.Length)
                break;
            var frame = tag.Slice(at + headerLength, length);
            switch (id)
            {
                case "TIT2" or "TT2":
                    title ??= Decode(frame);
                    break;
                case "TPE1" or "TP1":
                    artist ??= Decode(frame);
                    break;
                case "TPE2" or "TP2":
                    albumArtist ??= Decode(frame);
                    break;
                case "TALB" or "TAL":
                    album ??= Decode(frame);
                    break;
                case "TCON" or "TCO":
                    genre ??= Decode(frame);
                    break;
            }
            at += headerLength + length;
        }
        return new SongTags(title, artist ?? albumArtist, album, null, genre);
    }

    /// <summary>
    /// A text frame: a byte saying how it's encoded, then the text. Version 2.4 parts several values with zeros,
    /// which come back joined with "; ", as Windows joins them.
    /// </summary>
    static string? Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
            return null;
        var text = frame[1..];
        var decoded = frame[0] switch
        {
            0 => Encoding.Latin1.GetString(text),
            1 => text.Length >= 2 && text[0] == 0xFE && text[1] == 0xFF
                ? Encoding.BigEndianUnicode.GetString(text[2..])
                : Encoding.Unicode.GetString(text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE ? text[2..] : text),
            2 => Encoding.BigEndianUnicode.GetString(text),
            3 => Encoding.UTF8.GetString(text),
            _ => null,
        };
        if (decoded is null)
            return null;
        var parts = decoded.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.TrimStart('﻿'))
            .Where(part => part.Length > 0);
        var joined = string.Join("; ", parts);
        return joined.Length > 0 ? joined : null;
    }

    static int Syncsafe(ReadOnlySpan<byte> bytes) => bytes[0] << 21 | bytes[1] << 14 | bytes[2] << 7 | bytes[3];

    static byte[] Resynchronise(ReadOnlySpan<byte> tag)
    {
        var bytes = new List<byte>(tag.Length);
        for (var i = 0; i < tag.Length; i++)
        {
            bytes.Add(tag[i]);
            if (tag[i] == 0xFF && i + 1 < tag.Length && tag[i + 1] == 0)
                i++;
        }
        return [.. bytes];
    }
}
