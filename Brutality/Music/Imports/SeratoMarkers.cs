using System.Buffers.Binary;
using System.Text;
using Brutality.Audio.Analysis;

namespace Brutality.Music.Imports;

/// <summary>
/// Reads the hot cues and beat grid Serato keeps inside a song's own file. Its hot cues are its "Serato Markers2". In an MP3 they're in its ID3 tag,
/// in a GEOB frame of that description: two bytes (1, 1), then base64 text, often without its padding and with line
/// breaks. That decodes to two bytes (1, 1) again and a run of entries, each a name ending in a zero byte, a four-byte
/// length, big-endian, and that much data, up to an empty name. A "CUE" entry is a hot cue: a zero byte, its number
/// from 0, its place in milliseconds as four bytes, big-endian, then its colour and name.
///
/// An M4A keeps the same in an iTunes-style freeform tag, "----" with mean "com.serato.dj" and name "markersv2", and
/// a FLAC in a Vorbis comment, SERATO_MARKERS_V2; in both it's wrapped once more, in base64 again, after a header of
/// "application/octet-stream", two zero bytes, and "Serato Markers2" and a zero byte. (See the serato-tags notes,
/// github.com/Holzhaus/serato-tags, and Mixxx's reader, which agree.)
///
/// Its beat grid, "Serato BeatGrid", is kept the same ways: a GEOB frame of that description in an MP3, freeform tag
/// "beatgrid" in an M4A and comment SERATO_BEATGRID in a FLAC, wrapped the same way in those. It's two bytes (1, 0),
/// the number of markers as four bytes, then the markers, eight bytes each and big-endian: each but the last its place
/// in seconds as a float and how many beats it is to the next as four bytes, the last its place and the tempo from
/// there on as floats; then a byte of no meaning. Serato analyses every song's grid, often a little off the beat, so
/// only one the DJ has locked in Serato (its markers' "BPMLOCK" entry set) is taken as checked by hand and read.
/// </summary>
static class SeratoMarkers
{
    const int HOTCUES = 8;

    /// <summary>
    /// The biggest ID3 tag read: room for cover art, but not for something that isn't really a tag.
    /// </summary>
    const int MAX_TAG_BYTES = 32 << 20;

    /// <summary>
    /// One of Serato's tags, by what it's called in an MP3's GEOB frame (and the header before it once the base64
    /// wrapping an M4A's or FLAC's is undone), an M4A's freeform tag and a FLAC's Vorbis comment.
    /// </summary>
    sealed record Tag(string Geob, string Mp4, string Vorbis)
    {
        public byte[] WrappedHeader { get; } = Encoding.ASCII.GetBytes($"application/octet-stream\0\0{Geob}\0");
    }

    static readonly Tag Markers2 = new("Serato Markers2", "markersv2", "SERATO_MARKERS_V2");
    static readonly Tag BeatGridTag = new("Serato BeatGrid", "beatgrid", "SERATO_BEATGRID");

    /// <summary>
    /// The hot cues Serato saved in the file at <paramref name="path"/>, an MP3, M4A or FLAC, by number; null if there
    /// are none, it's another kind of file, or it can't be read.
    /// </summary>
    public static IReadOnlyList<TimeSpan?>? ReadHotcues(string path) => ReadAll(path).Hotcues;

    /// <summary>
    /// The beat grid Serato saved in the file at <paramref name="path"/>, an MP3, M4A or FLAC, its bars counted from
    /// its first marker, if it's locked there; null if it isn't locked, it has none, it's another kind of file, or it
    /// can't be read.
    /// </summary>
    public static BeatGrid? ReadGrid(string path) => ReadAll(path).Grid;

    /// <summary>
    /// The hot cues and locked beat grid Serato saved in the file at <paramref name="path"/>, as
    /// <see cref="ReadHotcues"/> and <see cref="ReadGrid"/> give them, from one reading of the file.
    /// </summary>
    public static (IReadOnlyList<TimeSpan?>? Hotcues, BeatGrid? Grid) ReadAll(string path)
    {
        if (Open(path) is not { } tags || tags(Markers2) is not { } markers)
            return (null, null);
        var grid = IsBpmLocked(markers) && tags(BeatGridTag) is { } data ? FromBeatGrid(data) : null;
        return (FromMarkers2(markers), grid);
    }

    /// <summary>
    /// The file at <paramref name="path"/>'s tags, read into memory and closed again, as a way of asking for what's
    /// in each, as an MP3 keeps it (unwrapped, from an M4A or FLAC), null if it isn't there; null if it can't be read.
    /// </summary>
    static Func<Tag, byte[]?>? Open(string path)
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

    static Func<Tag, byte[]?>? FromMp3(Stream file)
    {
        var header = new byte[10];
        if (file.Read(header) < 10)
            return null;
        var tag = new byte[Math.Min(Id3Size(header), MAX_TAG_BYTES)];
        file.ReadExactly(tag);
        return wanted => FromId3(header, tag, wanted);
    }

    /// <summary>
    /// A tag in an M4A: its "moov" box read whole, wherever it is among the boxes at the top of the file (before the
    /// audio, or after it), and Serato's freeform tag found in its metadata.
    /// </summary>
    static Func<Tag, byte[]?>? FromMp4(Stream file)
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
                return wanted => FromMoov(moov, wanted);
            }
            file.Position = start + size;
        }
        return null;
    }

    /// <summary>
    /// A tag in a "moov" box's contents: in moov › udta › meta › ilst, as iTunes keeps tags, or moov › meta › ilst, as
    /// some taggers do.
    /// </summary>
    static byte[]? FromMoov(ReadOnlySpan<byte> moov, Tag wanted)
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
            if (mean == "com.serato.dj" && name == wanted.Mp4)
                return FromWrapped(value, wanted);
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
    /// A tag in a FLAC's Vorbis comment, SERATO_MARKERS_V2 or SERATO_BEATGRID. A FLAC begins "fLaC" (after an ID3 tag,
    /// if a tagger put one first), then its metadata blocks, each a byte of type (the top bit set on the last) and three of
    /// length, big-endian; the Vorbis comment, type 4, is a vendor string and then the comments, "NAME=value", each
    /// after its length, little-endian.
    /// </summary>
    static Func<Tag, byte[]?>? FromFlac(Stream file)
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
                return wanted => FromVorbisComment(comments, wanted);
            }
            if (last)
                return null;
            file.Position += length;
        }
    }

    /// <summary>
    /// A tag in a Vorbis comment block's bytes, from its comment of that name.
    /// </summary>
    static byte[]? FromVorbisComment(ReadOnlySpan<byte> comments, Tag wanted)
    {
        if (comments.Length < 8)
            return null;
        var vendor = BinaryPrimitives.ReadInt32LittleEndian(comments);
        var at = 4 + vendor;
        if (vendor < 0 || at + 4 > comments.Length)
            return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(comments[at..]);
        at += 4;
        var key = Encoding.ASCII.GetBytes(wanted.Vorbis + "=");
        for (var i = 0; i < count && at + 4 <= comments.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(comments[at..]);
            at += 4;
            if (length < 0 || at + length > comments.Length)
                return null;
            var comment = comments.Slice(at, length);
            // Names are matched ignoring case, as Vorbis comments' are.
            if (comment.Length > key.Length && Encoding.ASCII.GetString(comment[..key.Length]).Equals(wanted.Vorbis + "=", StringComparison.OrdinalIgnoreCase))
                return FromWrapped(comment[key.Length..], wanted);
            at += length;
        }
        return null;
    }

    /// <summary>
    /// A tag as an M4A or FLAC keeps it: base64 of its header, then what an MP3 keeps.
    /// </summary>
    static byte[]? FromWrapped(ReadOnlySpan<byte> base64, Tag wanted)
    {
        if (Base64(base64) is not { } unwrapped || !unwrapped.AsSpan().StartsWith(wanted.WrappedHeader))
            return null;
        return unwrapped[wanted.WrappedHeader.Length..];
    }

    /// <summary>
    /// The size of the ID3 tag whose ten-byte header is <paramref name="header"/>, after the header; 0 if it isn't one.
    /// </summary>
    static int Id3Size(ReadOnlySpan<byte> header) =>
        header[0] == 'I' && header[1] == 'D' && header[2] == '3' ? Syncsafe(header[6..10]) : 0;

    // Seven bits a byte, so a tag's sizes never look like the start of an MPEG frame.
    static int Syncsafe(ReadOnlySpan<byte> bytes) => bytes[0] << 21 | bytes[1] << 14 | bytes[2] << 7 | bytes[3];

    /// <summary>
    /// A Serato tag in an ID3 tag's frames, <paramref name="tag"/>, after its <paramref name="header"/>.
    /// </summary>
    static byte[]? FromId3(ReadOnlySpan<byte> header, ReadOnlySpan<byte> tag, Tag wanted)
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
            if (id == "GEOB" && FromGeob(tag.Slice(at + 10, size), wanted) is { } data)
                return data;
            at += 10 + size;
        }
        return null;
    }

    /// <summary>
    /// A GEOB frame's data, if it's the tag wanted: a text encoding byte, then its MIME type, file name and
    /// description, each ending in a zero byte, then the data.
    /// </summary>
    static byte[]? FromGeob(ReadOnlySpan<byte> frame, Tag wanted)
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
        return fields[2] == wanted.Geob ? rest.ToArray() : null;
    }

    /// <summary>
    /// The beat grid in Serato BeatGrid's data, each marker but the last given the tempo its beats to the next take;
    /// null if it isn't one, or its markers don't make a grid (out of order, or no beats or tempo).
    /// </summary>
    internal static BeatGrid? FromBeatGrid(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6 || data[0] != 1 || data[1] != 0)
            return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
        if (count == 0 || count > 10_000 || data.Length < 6 + count * 8)
            return null;

        var places = new double[count];
        var beats = new uint[count];
        double bpm = 0;
        for (var i = 0; i < count; i++)
        {
            var marker = data.Slice(6 + i * 8, 8);
            places[i] = BinaryPrimitives.ReadSingleBigEndian(marker);
            if (i + 1 < count)
                beats[i] = BinaryPrimitives.ReadUInt32BigEndian(marker[4..]);
            else
                bpm = BinaryPrimitives.ReadSingleBigEndian(marker[4..]);
            if (!double.IsFinite(places[i]) || places[i] < 0 || (i > 0 && places[i] <= places[i - 1]) || (i + 1 < count && beats[i] == 0))
                return null;
        }
        if (!double.IsFinite(bpm) || bpm <= 0)
            return null;

        var markers = new List<GridMarker>((int)count);
        for (var i = 0; i + 1 < count; i++)
            markers.Add(new GridMarker(places[i], beats[i] * 60 / (places[i + 1] - places[i])));
        markers.Add(new GridMarker(places[^1], bpm));
        // As long again as a beat past the last marker, for want of the song's length; a grid's length is the song's
        // wherever it's used.
        return new BeatGrid(markers, places[^1] + 60 / bpm);
    }

    /// <summary>
    /// The hot cues in Serato Markers2's data, from its CUE entries.
    /// </summary>
    internal static IReadOnlyList<TimeSpan?>? FromMarkers2(ReadOnlySpan<byte> data)
    {
        var cues = new TimeSpan?[HOTCUES];
        var any = false;
        foreach (var (name, entry) in Entries(data))
        {
            if (name != "CUE" || entry.Length < 6)
                continue;
            var number = entry[1];
            if (number < HOTCUES)
            {
                cues[number] = TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(2, 4)));
                any = true;
            }
        }
        return any ? cues : null;
    }

    /// <summary>
    /// Whether Serato Markers2's data says the beat grid's locked: its BPMLOCK entry, one byte, not zero.
    /// </summary>
    internal static bool IsBpmLocked(ReadOnlySpan<byte> data) => Entries(data).Any(entry => entry.Name == "BPMLOCK" && entry.Data is [not 0, ..]);

    /// <summary>
    /// Serato Markers2's entries, each its name and data: its two bytes, then the base64 of the entries, up to an empty
    /// name or one that runs past the end. None if it isn't Markers2.
    /// </summary>
    static List<(string Name, byte[] Data)> Entries(ReadOnlySpan<byte> data)
    {
        var found = new List<(string, byte[])>();
        if (data.Length < 2 || data[0] != 1 || data[1] != 1)
            return found;
        if (Base64(data[2..]) is not { } entries || entries.Length < 2 || entries[0] != 1 || entries[1] != 1)
            return found;

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
            found.Add((name, entries[start..(start + length)]));
            at = start + length;
        }
        return found;
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
