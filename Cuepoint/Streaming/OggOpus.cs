using System.Buffers.Binary;
using System.Text;

namespace Cuepoint.Streaming;

/// <summary>
/// Opus audio in Ogg pages, as Icecast takes it and players play it (RFC 7845): a logical stream begins with an
/// OpusHead page and an OpusTags page, whose title players show, then one page for each packet of audio. A new title
/// is a new logical stream chained on after the last, ended with its last page marked, as Icecast passes on.
/// </summary>
sealed class OggOpusStream
{
    /// <summary>
    /// Opus's clock: granule positions count 48 kHz samples whatever the rate put in.
    /// </summary>
    public const int RATE = 48000;

    readonly int _channels;
    readonly int _preSkip;
    uint _serial;
    uint _sequence;
    long _granule;
    bool _begun;

    /// <summary>
    /// A stream of <paramref name="channels"/>-channel audio, whose decoder drops the first
    /// <paramref name="preSkip"/> samples (the encoder's lookahead); <paramref name="serial"/> is the first logical
    /// stream's, each one after that the next.
    /// </summary>
    public OggOpusStream(int channels, int preSkip, uint serial)
    {
        _channels = channels;
        _preSkip = preSkip;
        _serial = serial;
    }

    /// <summary>
    /// The logical stream's serial number, as its pages carry it.
    /// </summary>
    public uint Serial => _serial;

    /// <summary>
    /// The two header pages beginning a logical stream: OpusHead, then OpusTags with <paramref name="title"/> as its
    /// TITLE (and the encoder's name).
    /// </summary>
    public byte[] Begin(string title)
    {
        (_sequence, _granule, _begun) = (0, 0, true);
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)_channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)_preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), RATE);
        // Output gain 0, mapping family 0 (mono or stereo).

        var vendor = "Cuepoint"u8;
        var comments = new List<byte[]>();
        if (title.Length > 0)
            comments.Add(Encoding.UTF8.GetBytes($"TITLE={title}"));
        var tags = new MemoryStream();
        tags.Write("OpusTags"u8);
        WriteLength(tags, vendor.Length);
        tags.Write(vendor);
        WriteLength(tags, comments.Count);
        foreach (var comment in comments)
        {
            WriteLength(tags, comment.Length);
            tags.Write(comment);
        }

        var pages = new MemoryStream();
        pages.Write(Page(head, granule: 0, first: true, last: false));
        pages.Write(Page(tags.ToArray(), granule: 0, first: false, last: false));
        return pages.ToArray();
    }

    static void WriteLength(Stream stream, int length)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)length);
        stream.Write(bytes);
    }

    /// <summary>
    /// One packet of audio, <paramref name="samples"/> 48 kHz samples long, on a page of its own; with
    /// <paramref name="last"/>, the logical stream's last page, after which <see cref="Begin"/> starts the next.
    /// </summary>
    public byte[] Audio(ReadOnlySpan<byte> packet, int samples, bool last = false)
    {
        if (!_begun)
            throw new InvalidOperationException("Begin the stream before its audio");
        _granule += samples;
        var page = Page(packet, _preSkip + _granule, first: false, last);
        if (last)
        {
            _begun = false;
            _serial++;
        }
        return page;
    }

    byte[] Page(ReadOnlySpan<byte> packet, long granule, bool first, bool last)
    {
        // A packet's laced in 255-byte segments, with one shorter (perhaps empty) to say where it ends.
        var segments = packet.Length / 255 + 1;
        if (segments > 255)
            throw new ArgumentException("A packet that big needs more than one page", nameof(packet));
        var page = new byte[27 + segments + packet.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = (byte)((first ? 0x02 : 0) | (last ? 0x04 : 0));
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), _serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
        page[26] = (byte)segments;
        for (var i = 0; i < segments - 1; i++)
            page[27 + i] = 255;
        page[27 + segments - 1] = (byte)(packet.Length % 255);
        packet.CopyTo(page.AsSpan(27 + segments));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
        return page;
    }

    static readonly uint[] CrcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var bit = 0; bit < 8; bit++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }

    /// <summary>
    /// Ogg's checksum of a page, its own checksum field taken as zero: CRC-32 with polynomial 0x04C11DB7, unreflected.
    /// </summary>
    internal static uint Crc(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        for (var i = 0; i < page.Length; i++)
        {
            var b = i is >= 22 and < 26 ? (byte)0 : page[i];
            crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b];
        }
        return crc;
    }
}
