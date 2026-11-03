using System.Buffers.Binary;
using System.Text;

namespace Brutality.Music.Imports;

/// <summary>
/// Reads the hot cues Serato keeps inside a song's own file, its "Serato Markers2". In an MP3 they're in its ID3 tag,
/// in a GEOB frame of that description: two bytes (1, 1), then base64 text, often without its padding and with line
/// breaks. That decodes to two bytes (1, 1) again and a run of entries, each a name ending in a zero byte, a four-byte
/// length, big-endian, and that much data, up to an empty name. A "CUE" entry is a hot cue: a zero byte, its number
/// from 0, its place in milliseconds as four bytes, big-endian, then its colour and name.
///
/// An M4A keeps the same in an iTunes-style freeform tag, "----" with mean "com.serato.dj" and name "markersv2", and
/// a FLAC in a Vorbis comment, SERATO_MARKERS_V2; in both it's wrapped once more, in base64 again, after a header of
/// "application/octet-stream", two zero bytes, and "Serato Markers2" and a zero byte. (See the serato-tags notes,
/// github.com/Holzhaus/serato-tags, and Mixxx's reader, which agree.)
/// </summary>
static class SeratoMarkers
{
    const int HOTCUES = 8;

    /// <summary>
    /// The biggest ID3 tag read: room for cover art, but not for something that isn't really a tag.
    /// </summary>
    const int MAX_TAG_BYTES = 32 << 20;

    /// <summary>
    /// The header before the data, once the base64 wrapping an M4A's or FLAC's markers is undone.
    /// </summary>
    static readonly byte[] WrappedHeader = "application/octet-stream\0\0Serato Markers2\0"u8.ToArray();

    /// <summary>
    /// The hot cues Serato saved in the file at <paramref name="path"/>, an MP3, M4A or FLAC, by number; null if there
    /// are none, it's another kind of file, or it can't be read.
    /// </summary>
    public static IReadOnlyList<TimeSpan?>? ReadHotcues(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp3" => FromMp3(file),
                ".m4a" or ".mp4" => FromMp4(file),
                ".flac" => FromFlac(file),
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    static IReadOnlyList<TimeSpan?>? FromMp3(Stream file)
    {
        Span<byte> header = stackalloc byte[10];
        if (file.Read(header) < 10)
            return null;
        var tag = new byte[Math.Min(Id3Size(header), MAX_TAG_BYTES)];
        file.ReadExactly(tag);
        return FromId3(header, tag);
    }

    /// <summary>
    /// The hot cues in an M4A: its "moov" box read whole, wherever it is among the boxes at the top of the file (before
    /// the audio, or after it), and Serato's freeform tag found in its metadata.
    /// </summary>
    static IReadOnlyList<TimeSpan?>? FromMp4(Stream file)
    {
        Span<byte> header = stackalloc byte[16];
        while (file.Position + 8 <= file.Length)
        {
            var start = file.Position;
            file.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header.Slice(4, 4));
            var headerSize = 8;
            if (size == 1)
            {
                // A 64-bit size follows, for a box over 4 GB.
                file.ReadExactly(header.Slice(8, 8));
                size = BinaryPrimitives.ReadInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
                size = file.Length - start;
            if (size < headerSize || start + size > file.Length)
                return null;

            if (type == "moov")
            {
                if (size > MAX_TAG_BYTES)
                    return null;
                var moov = new byte[size - headerSize];
                file.ReadExactly(moov);
                return FromMoov(moov);
            }
            file.Position = start + size;
        }
        return null;
    }

    /// <summary>
    /// The hot cues in a "moov" box's contents: in moov › udta › meta › ilst, as iTunes keeps tags, or moov › meta ›
    /// ilst, as some taggers do.
    /// </summary>
    internal static IReadOnlyList<TimeSpan?>? FromMoov(ReadOnlySpan<byte> moov)
    {
        var meta = Box(moov, "udta") is { } udta ? Box(moov.Slice(udta.Start, udta.Length), "meta", udta.Start) : null;
        meta ??= Box(moov, "meta");
        // "meta" has four bytes of version and flags before its boxes.
        if (meta is not { Length: >= 4 } found || Box(moov.Slice(found.Start + 4, found.Length - 4), "ilst", found.Start + 4) is not { } ilst)
            return null;

        var items = moov.Slice(ilst.Start, ilst.Length);
        foreach (var item in Boxes(items))
        {
            if (item.Type != "----")
                continue;
            var entry = items.Slice(item.Start, item.Length);
            string? mean = null, name = null;
            ReadOnlySpan<byte> value = default;
            foreach (var part in Boxes(entry))
            {
                var content = entry.Slice(part.Start, part.Length);
                // "mean" and "name" have four bytes of version and flags; "data" four of type and four of locale.
                if (part.Type == "mean" && content.Length >= 4)
                    mean = Encoding.UTF8.GetString(content[4..]);
                else if (part.Type == "name" && content.Length >= 4)
                    name = Encoding.UTF8.GetString(content[4..]);
                else if (part.Type == "data" && content.Length >= 8)
                    value = content[8..];
            }
            if (mean == "com.serato.dj" && name == "markersv2")
                return FromWrapped(value);
        }
        return null;
    }

    /// <summary>
    /// The first box called <paramref name="type"/> among <paramref name="boxes"/>, where its contents start and how
    /// long they are, counting from <paramref name="offset"/>.
    /// </summary>
    static (int Start, int Length)? Box(ReadOnlySpan<byte> boxes, string type, int offset = 0)
    {
        foreach (var box in Boxes(boxes))
        {
            if (box.Type == type)
                return (offset + box.Start, box.Length);
        }
        return null;
    }

    /// <summary>
    /// The boxes in <paramref name="bytes"/>, each its type and where its contents start and how long they are, up to
    /// one that runs past the end.
    /// </summary>
    static List<(string Type, int Start, int Length)> Boxes(ReadOnlySpan<byte> bytes)
    {
        var boxes = new List<(string, int, int)>();
        var at = 0;
        while (at + 8 <= bytes.Length)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
            if (size < 8 || at + size > bytes.Length)
                break;
            boxes.Add((Encoding.ASCII.GetString(bytes.Slice(at + 4, 4)), at + 8, (int)size - 8));
            at += (int)size;
        }
        return boxes;
    }

    /// <summary>
    /// The hot cues in a FLAC's Vorbis comment SERATO_MARKERS_V2. A FLAC begins "fLaC" (after an ID3 tag, if a tagger
    /// put one first), then its metadata blocks, each a byte of type (the top bit set on the last) and three of
    /// length, big-endian; the Vorbis comment, type 4, is a vendor string and then the comments, "NAME=value", each
    /// after its length, little-endian.
    /// </summary>
    static IReadOnlyList<TimeSpan?>? FromFlac(Stream file)
    {
        Span<byte> header = stackalloc byte[10];
        if (file.Read(header) < 10)
            return null;
        // Past an ID3 tag put first.
        file.Position = Id3Size(header) is > 0 and var id3 ? 10 + id3 : 0;
        Span<byte> magic = stackalloc byte[4];
        file.ReadExactly(magic);
        if (!magic.SequenceEqual("fLaC"u8))
            return null;

        Span<byte> block = stackalloc byte[4];
        while (true)
        {
            file.ReadExactly(block);
            var last = (block[0] & 0x80) != 0;
            var type = block[0] & 0x7f;
            var length = block[1] << 16 | block[2] << 8 | block[3];
            if (type == 4)
            {
                var comments = new byte[length];
                file.ReadExactly(comments);
                return FromVorbisComment(comments);
            }
            if (last)
                return null;
            file.Position += length;
        }
    }

    /// <summary>
    /// The hot cues in a Vorbis comment block's bytes, from its SERATO_MARKERS_V2 comment.
    /// </summary>
    internal static IReadOnlyList<TimeSpan?>? FromVorbisComment(ReadOnlySpan<byte> comments)
    {
        if (comments.Length < 8)
            return null;
        var vendor = BinaryPrimitives.ReadInt32LittleEndian(comments);
        var at = 4 + vendor;
        if (vendor < 0 || at + 4 > comments.Length)
            return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(comments[at..]);
        at += 4;
        var key = "SERATO_MARKERS_V2="u8;
        for (var i = 0; i < count && at + 4 <= comments.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(comments[at..]);
            at += 4;
            if (length < 0 || at + length > comments.Length)
                return null;
            var comment = comments.Slice(at, length);
            // Names are matched ignoring case, as Vorbis comments' are.
            if (comment.Length > key.Length && Encoding.ASCII.GetString(comment[..key.Length]).Equals("SERATO_MARKERS_V2=", StringComparison.OrdinalIgnoreCase))
                return FromWrapped(comment[key.Length..]);
            at += length;
        }
        return null;
    }

    /// <summary>
    /// The hot cues in Serato's markers as an M4A or FLAC keeps them: base64 of the header, then the markers as an
    /// MP3 keeps them.
    /// </summary>
    internal static IReadOnlyList<TimeSpan?>? FromWrapped(ReadOnlySpan<byte> base64)
    {
        if (Base64(base64) is not { } unwrapped || !unwrapped.AsSpan().StartsWith(WrappedHeader))
            return null;
        return FromMarkers2(unwrapped.AsSpan(WrappedHeader.Length));
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
