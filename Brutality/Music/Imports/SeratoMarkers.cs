using System.Buffers.Binary;
using System.Text;

namespace Brutality.Music.Imports;

/// <summary>
/// Reads the hot cues Serato keeps inside a song's own file. In an MP3 they're in its ID3 tag, in a GEOB frame
/// described "Serato Markers2": two bytes (1, 1), then base64 text, often without its padding and with line breaks.
/// That decodes to two bytes (1, 1) again and a run of entries, each a name ending in a zero byte, a four-byte
/// length, big-endian, and that much data, up to an empty name. A "CUE" entry is a hot cue: a zero byte, its number
/// from 0, its place in milliseconds as four bytes, big-endian, then its colour and name.
/// </summary>
static class SeratoMarkers
{
    const int HOTCUES = 8;

    /// <summary>
    /// The biggest ID3 tag read: room for cover art, but not for something that isn't really a tag.
    /// </summary>
    const int MAX_TAG_BYTES = 32 << 20;

    /// <summary>
    /// The hot cues Serato saved in the file at <paramref name="path"/>, by number; null if there are none, it isn't
    /// an MP3, or it can't be read.
    /// </summary>
    public static IReadOnlyList<TimeSpan?>? ReadHotcues(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[10];
            if (file.Read(header) < 10)
                return null;
            var tag = new byte[Math.Min(Id3Size(header), MAX_TAG_BYTES)];
            file.ReadExactly(tag);
            return FromId3(header, tag);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// The size of the ID3 tag whose ten-byte header is <paramref name="header"/>, after the header; 0 if it isn't one.
    /// </summary>
    static int Id3Size(ReadOnlySpan<byte> header) =>
        header[0] == 'I' && header[1] == 'D' && header[2] == '3' ? Syncsafe(header[6..10]) : 0;

    // Seven bits a byte, so a tag's sizes never look like the start of an MPEG frame.
    static int Syncsafe(ReadOnlySpan<byte> bytes) => bytes[0] << 21 | bytes[1] << 14 | bytes[2] << 7 | bytes[3];

    /// <summary>
    /// The hot cues in an ID3 tag's frames, <paramref name="tag"/>, after its <paramref name="header"/>.
    /// </summary>
    internal static IReadOnlyList<TimeSpan?>? FromId3(ReadOnlySpan<byte> header, ReadOnlySpan<byte> tag)
    {
        var version = header[3];
        // Unsynchronised tags (rare, and never Serato's) would need undoing first.
        if (Id3Size(header) == 0 || version is not (3 or 4) || (header[5] & 0x80) != 0)
            return null;

        var at = 0;
        if ((header[5] & 0x40) != 0 && tag.Length >= 4)
            at = version == 4 ? Syncsafe(tag[..4]) : BinaryPrimitives.ReadInt32BigEndian(tag) + 4;
        while (at + 10 <= tag.Length && tag[at] != 0)
        {
            var id = Encoding.ASCII.GetString(tag.Slice(at, 4));
            var size = version == 4 ? Syncsafe(tag.Slice(at + 4, 4)) : BinaryPrimitives.ReadInt32BigEndian(tag.Slice(at + 4, 4));
            if (size <= 0 || at + 10 + size > tag.Length)
                break;
            if (id == "GEOB" && FromGeob(tag.Slice(at + 10, size)) is { } cues)
                return cues;
            at += 10 + size;
        }
        return null;
    }

    /// <summary>
    /// The hot cues in a GEOB frame's contents, if it's Serato's markers: a text encoding byte, then its MIME type,
    /// file name and description, each ending in a zero byte, then the data.
    /// </summary>
    static IReadOnlyList<TimeSpan?>? FromGeob(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1 || frame[0] != 0)
            return null;
        var rest = frame[1..];
        var fields = new string[3];
        for (var i = 0; i < 3; i++)
        {
            var end = rest.IndexOf((byte)0);
            if (end < 0)
                return null;
            fields[i] = Encoding.Latin1.GetString(rest[..end]);
            rest = rest[(end + 1)..];
        }
        return fields[2] == "Serato Markers2" ? FromMarkers2(rest) : null;
    }

    /// <summary>
    /// The hot cues in Serato Markers2's data: its two bytes, then the base64 of its entries.
    /// </summary>
    internal static IReadOnlyList<TimeSpan?>? FromMarkers2(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != 1 || data[1] != 1)
            return null;
        if (Base64(data[2..]) is not { } entries || entries.Length < 2 || entries[0] != 1 || entries[1] != 1)
            return null;

        var cues = new TimeSpan?[HOTCUES];
        var any = false;
        var at = 2;
        while (at < entries.Length && entries[at] != 0)
        {
            var nameEnd = Array.IndexOf(entries, (byte)0, at);
            if (nameEnd < 0 || nameEnd + 5 > entries.Length)
                break;
            var name = Encoding.ASCII.GetString(entries, at, nameEnd - at);
            var length = BinaryPrimitives.ReadInt32BigEndian(entries.AsSpan(nameEnd + 1, 4));
            var start = nameEnd + 5;
            if (length < 0 || start + length > entries.Length)
                break;
            if (name == "CUE" && length >= 6)
            {
                var number = entries[start + 1];
                var milliseconds = BinaryPrimitives.ReadUInt32BigEndian(entries.AsSpan(start + 2, 4));
                if (number < HOTCUES)
                {
                    cues[number] = TimeSpan.FromMilliseconds(milliseconds);
                    any = true;
                }
            }
            at = start + length;
        }
        return any ? cues : null;
    }

    /// <summary>
    /// Serato's base64: line breaks every 72 characters to drop, zero bytes padding it out, its own padding left off,
    /// and sometimes a character too many to be base64, which an "A" put after makes right.
    /// </summary>
    static byte[]? Base64(ReadOnlySpan<byte> text)
    {
        var characters = new StringBuilder(text.Length);
        foreach (var b in text)
        {
            if (b == 0)
                break;
            if (b is (byte)'\n' or (byte)'\r' or (byte)' ')
                continue;
            characters.Append((char)b);
        }
        if (characters.Length % 4 == 1)
            characters.Append('A');
        while (characters.Length % 4 != 0)
            characters.Append('=');
        try
        {
            return Convert.FromBase64String(characters.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
