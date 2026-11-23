using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cuepoint.Audio;

/// <summary>
/// Writes a song as a 16-bit FLAC: losslessly compressed, about half the size of a WAV or less, with its title,
/// artist, album and genre as Vorbis comments, and its cover as a picture, which Windows and DJ software read. Each block of samples is predicted
/// by the best of FLAC's fixed polynomial predictors, its stereo as left and right or as mid and side, whichever is
/// smallest, and what the prediction misses is Rice-coded in partitions, each with its own parameter.
/// </summary>
static class FlacFile
{
    const int BLOCK_SIZE = 4096;
    const int BITS = 16;
    const int MAX_FIXED_ORDER = 4;
    const int MAX_PARTITION_ORDER = 8;
    const int MAX_RICE_PARAMETER = 14;

    /// <summary>
    /// Writes <paramref name="audio"/> to <paramref name="path"/>, rounded to 16 bits and clipped at full scale, by
    /// way of a file beside it, so a half-written one is never left under its name; a file there already is only
    /// replaced with <paramref name="replace"/>, and then only once the new one's whole. <paramref name="cover"/> is
    /// its front cover, if it has one. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// if it can't.
    /// </summary>
    public static void Write(string path, PcmAudio audio, SongTags tags, bool replace = false, CoverImage? cover = null)
    {
        if (!replace && File.Exists(path))
            throw new IOException($"There's a file at {path} already");
        if (audio.Channels is < 1 or > 8)
            throw new IOException($"FLAC can't hold {audio.Channels} channels");
        if (audio.SampleRate is <= 0 or >= 1 << 20)
            throw new IOException($"FLAC can't hold a sample rate of {audio.SampleRate}");

        var samples = Quantise(audio.Samples);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new BufferedStream(File.Create(temporary), 1 << 20))
            {
                stream.Write("fLaC"u8);
                WriteStreamInfo(stream, audio.Channels, audio.SampleRate, samples);
                // A picture's length has to fit in a block's 24 bits; one bigger is left out.
                if (cover is not null && 32 + cover.MimeType.Length + cover.Data.Length >= 1 << 24)
                    cover = null;
                WriteComments(stream, tags, last: cover is null);
                if (cover is not null)
                    WritePicture(stream, cover);

                var writer = new BitWriter();
                var channels = audio.Channels;
                var frames = samples.Length / channels;
                var planes = new int[channels][];
                for (var c = 0; c < channels; c++)
                    planes[c] = new int[BLOCK_SIZE];
                var number = 0L;
                for (var start = 0; start < frames; start += BLOCK_SIZE, number++)
                {
                    var size = Math.Min(BLOCK_SIZE, frames - start);
                    for (var i = 0; i < size; i++)
                    {
                        for (var c = 0; c < channels; c++)
                            planes[c][i] = samples[(start + i) * channels + c];
                    }
                    writer.Reset();
                    WriteFrame(writer, planes, channels, size, number);
                    stream.Write(writer.Bytes);
                }
            }
            File.Move(temporary, path, overwrite: replace);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Floats from -1 to 1 into 16-bit samples, as a decoder dividing by 32768 gets them back exactly.
    static int[] Quantise(float[] samples)
    {
        var quantised = new int[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            quantised[i] = (int)Math.Clamp(MathF.Round(samples[i] * 32768f), short.MinValue, short.MaxValue);
        return quantised;
    }

    // A metadata block's header: whether it's the last, its type, and its length.
    static void WriteBlockHeader(Stream stream, bool last, int type, int length)
    {
        stream.WriteByte((byte)((last ? 0x80 : 0) | type));
        stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
    }

    // STREAMINFO: the block sizes, the format, how many samples there are, and the MD5 of them all as 16-bit
    // little-endian, so a decoder can check it got them back. Frame sizes are left unknown, as they're allowed to be.
    static void WriteStreamInfo(Stream stream, int channels, int sampleRate, int[] samples)
    {
        var info = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(info, BLOCK_SIZE);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2), BLOCK_SIZE);
        var total = (ulong)(samples.Length / channels);
        var packed = (ulong)sampleRate << 44 | (ulong)(channels - 1) << 41 | (ulong)(BITS - 1) << 36 | total & 0xFFFFFFFFFUL;
        BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10), packed);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var bytes = new byte[64 * 1024];
        for (var start = 0; start < samples.Length; start += bytes.Length / 2)
        {
            var count = Math.Min(bytes.Length / 2, samples.Length - start);
            for (var i = 0; i < count; i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)samples[start + i]);
            md5.AppendData(bytes, 0, count * 2);
        }
        md5.GetHashAndReset().CopyTo(info, 18);

        WriteBlockHeader(stream, last: false, type: 0, info.Length);
        stream.Write(info);
    }

    // VORBIS_COMMENT, the last metadata block: who made it, then TITLE, ARTIST, ALBUM and GENRE, all little-endian.
    static void WriteComments(Stream stream, SongTags tags, bool last)
    {
        using var body = new MemoryStream();
        void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            body.Write(length);
            body.Write(bytes);
        }

        var comments = new[] { ("TITLE", tags.Title), ("ARTIST", tags.Artist), ("ALBUM", tags.Album), ("GENRE", tags.Genre) }
            .Where(comment => !string.IsNullOrWhiteSpace(comment.Item2))
            .Select(comment => $"{comment.Item1}={comment.Item2!.Trim()}")
            .ToList();
        Text("Cuepoint");
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, comments.Count);
        body.Write(count);
        foreach (var comment in comments)
            Text(comment);

        WriteBlockHeader(stream, last, type: 4, (int)body.Length);
        body.WriteTo(stream);
    }

    // PICTURE, the last metadata block when there's a cover: its type (3, the front cover), its MIME type, no
    // description, its size and bits per pixel, no palette, and the picture file, all big-endian.
    static void WritePicture(Stream stream, CoverImage cover)
    {
        var mime = Encoding.ASCII.GetBytes(cover.MimeType);
        var block = new byte[32 + mime.Length + cover.Data.Length];
        var span = block.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span, 3);
        BinaryPrimitives.WriteInt32BigEndian(span[4..], mime.Length);
        mime.CopyTo(span[8..]);
        var at = 8 + mime.Length;
        BinaryPrimitives.WriteInt32BigEndian(span[at..], 0);
        BinaryPrimitives.WriteInt32BigEndian(span[(at + 4)..], cover.Width);
        BinaryPrimitives.WriteInt32BigEndian(span[(at + 8)..], cover.Height);
        BinaryPrimitives.WriteInt32BigEndian(span[(at + 12)..], cover.Depth);
        BinaryPrimitives.WriteInt32BigEndian(span[(at + 16)..], 0);
        BinaryPrimitives.WriteInt32BigEndian(span[(at + 20)..], cover.Data.Length);
        cover.Data.CopyTo(span[(at + 24)..]);
        WriteBlockHeader(stream, last: true, type: 6, block.Length);
        stream.Write(block);
    }

    // One frame: its header, a subframe for each channel (stereo as whichever of left and right, left and side, side
    // and right, or mid and side is smallest), and a CRC-16 of it all.
    static void WriteFrame(BitWriter writer, int[][] planes, int channels, int size, long number)
    {
        Subframe[] subframes;
        int assignment;
        if (channels == 2)
        {
            var (left, right) = (planes[0], planes[1]);
            var mid = new int[size];
            var side = new int[size];
            for (var i = 0; i < size; i++)
            {
                mid[i] = (left[i] + right[i]) >> 1;
                side[i] = left[i] - right[i];
            }
            var l = Subframe.Best(left, size, BITS);
            var r = Subframe.Best(right, size, BITS);
            var m = Subframe.Best(mid, size, BITS);
            var s = Subframe.Best(side, size, BITS + 1);
            // Left and right as they are, or with either one, or the mid, alongside the side.
            (long Bits, int Assignment, Subframe[] Subframes)[] choices =
            [
                (l.Bits + r.Bits, 1, [l, r]),
                (l.Bits + s.Bits, 8, [l, s]),
                (s.Bits + r.Bits, 9, [s, r]),
                (m.Bits + s.Bits, 10, [m, s]),
            ];
            var smallest = choices.MinBy(choice => choice.Bits);
            (assignment, subframes) = (smallest.Assignment, smallest.Subframes);
        }
        else
        {
            assignment = channels - 1;
            subframes = new Subframe[channels];
            for (var c = 0; c < channels; c++)
                subframes[c] = Subframe.Best(planes[c], size, BITS);
        }

        // The header: sync code and a fixed block size, the block size (4096, or one given after the frame number),
        // the sample rate from STREAMINFO, the channels, 16 bits, the frame number, and a CRC-8 of the header.
        writer.Write(0xFFF8, 16);
        writer.Write(size == BLOCK_SIZE ? 12u : 7u, 4);
        writer.Write(0, 4);
        writer.Write((uint)assignment, 4);
        writer.Write(0b100, 3);
        writer.Write(0, 1);
        WriteUtf8(writer, number);
        if (size != BLOCK_SIZE)
            writer.Write((uint)(size - 1), 16);
        writer.Write(Crc8(writer.Bytes), 8);

        foreach (var subframe in subframes)
            subframe.Write(writer);
        writer.Align();
        writer.Write(Crc16(writer.Bytes), 16);
    }

    // A frame number, coded as UTF-8 is, extended to 36 bits.
    static void WriteUtf8(BitWriter writer, long value)
    {
        if (value < 0x80)
        {
            writer.Write((uint)value, 8);
            return;
        }
        var bytes = value < 0x800 ? 2 : value < 0x10000 ? 3 : value < 0x200000 ? 4 : value < 0x4000000 ? 5 : value < 0x80000000 ? 6 : 7;
        writer.Write((uint)((0xFF00 >> bytes) & 0xFF | (int)(value >> (6 * (bytes - 1)))), 8);
        for (var i = bytes - 2; i >= 0; i--)
            writer.Write((uint)(0x80 | (value >> (6 * i)) & 0x3F), 8);
    }

    static byte Crc8(ReadOnlySpan<byte> bytes)
    {
        var crc = 0;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1;
        }
        return (byte)crc;
    }

    static readonly ushort[] Crc16Table = Enumerable
        .Range(0, 256)
        .Select(n =>
        {
            var crc = n << 8;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
            return (ushort)crc;
        })
        .ToArray();

    static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes)
            crc = (ushort)((crc << 8) ^ Crc16Table[(crc >> 8) ^ b]);
        return crc;
    }

    /// <summary>
    /// One channel of a frame, coded the smallest way found: as one value repeated, as a fixed predictor of some order
    /// and its Rice-coded residual, or failing those, the samples as they are.
    /// </summary>
    sealed class Subframe
    {
        int[] _samples = [];
        int _size;
        int _bits;
        int _order = -1;
        int[] _residual = [];
        int _partitionOrder;
        int[] _parameters = [];
        bool _constant;

        /// <summary>
        /// How many bits it takes.
        /// </summary>
        public long Bits { get; private set; }

        public static Subframe Best(int[] samples, int size, int bits)
        {
            var best = new Subframe { _samples = samples, _size = size, _bits = bits, Bits = 8 + (long)bits * size };
            if (samples.AsSpan(0, size).IndexOfAnyExcept(samples[0]) < 0)
            {
                best._constant = true;
                best.Bits = 8 + bits;
                return best;
            }

            var residual = new int[size];
            for (var order = 0; order <= Math.Min(MAX_FIXED_ORDER, size - 1); order++)
            {
                Predict(samples, size, order, residual);
                var (partitionOrder, parameters, riceBits) = Partition(residual, size, order);
                var total = 8 + (long)order * bits + riceBits;
                if (total < best.Bits)
                {
                    best.Bits = total;
                    best._order = order;
                    best._residual = residual.ToArray();
                    best._partitionOrder = partitionOrder;
                    best._parameters = parameters;
                }
            }
            return best;
        }

        // What the fixed predictor of an order misses, from the sample after its warm-up on.
        static void Predict(int[] x, int size, int order, int[] residual)
        {
            for (var i = order; i < size; i++)
            {
                residual[i] = order switch
                {
                    0 => x[i],
                    1 => x[i] - x[i - 1],
                    2 => x[i] - 2 * x[i - 1] + x[i - 2],
                    3 => x[i] - 3 * x[i - 1] + 3 * x[i - 2] - x[i - 3],
                    _ => x[i] - 4 * x[i - 1] + 6 * x[i - 2] - 4 * x[i - 3] + x[i - 4],
                };
            }
        }

        static uint Fold(int value) => (uint)((value << 1) ^ (value >> 31));

        // The partitioning and Rice parameters that take fewest bits, and how many: estimated from each partition's
        // total, as count × (parameter + 1) + total >> parameter.
        static (int Order, int[] Parameters, long Bits) Partition(int[] residual, int size, int order)
        {
            var finest = 0;
            while (finest < MAX_PARTITION_ORDER && size % (1 << (finest + 1)) == 0 && (size >> (finest + 1)) > order)
                finest++;
            var sums = new long[1 << finest];
            var count = size >> finest;
            for (var p = 0; p < sums.Length; p++)
            {
                long sum = 0;
                for (var i = Math.Max(p * count, order); i < (p + 1) * count; i++)
                    sum += Fold(residual[i]);
                sums[p] = sum;
            }

            (int Order, int[] Parameters, long Bits) best = (0, [], long.MaxValue);
            for (var partitionOrder = finest; partitionOrder >= 0; partitionOrder--)
            {
                var partitions = 1 << partitionOrder;
                var parameters = new int[partitions];
                long bits = 2 + 4;
                for (var p = 0; p < partitions; p++)
                {
                    var samples = (size >> partitionOrder) - (p == 0 ? order : 0);
                    var (k, cost) = BestParameter(sums[p], samples);
                    parameters[p] = k;
                    bits += 4 + cost;
                }
                if (bits < best.Bits)
                    best = (partitionOrder, parameters, bits);
                // The next order down has partitions twice the size.
                if (partitionOrder > 0)
                {
                    var merged = new long[partitions / 2];
                    for (var p = 0; p < merged.Length; p++)
                        merged[p] = sums[2 * p] + sums[2 * p + 1];
                    sums = merged;
                }
            }
            return best;
        }

        static (int Parameter, long Bits) BestParameter(long sum, int count)
        {
            if (count <= 0)
                return (0, 0);
            var guess = sum <= count ? 0 : Math.Min(MAX_RICE_PARAMETER, 63 - (int)ulong.LeadingZeroCount((ulong)(sum / count)));
            (int, long) best = (0, long.MaxValue);
            for (var k = Math.Max(0, guess - 1); k <= Math.Min(MAX_RICE_PARAMETER, guess + 1); k++)
            {
                var bits = (long)count * (k + 1) + (sum >> k);
                if (bits < best.Item2)
                    best = (k, bits);
            }
            return best;
        }

        public void Write(BitWriter writer)
        {
            if (_constant)
            {
                writer.Write(0, 8);
                writer.WriteSigned(_samples[0], _bits);
                return;
            }
            if (_order < 0)
            {
                writer.Write(0b000001 << 1, 8);
                for (var i = 0; i < _size; i++)
                    writer.WriteSigned(_samples[i], _bits);
                return;
            }

            writer.Write((uint)((0b001000 | _order) << 1), 8);
            for (var i = 0; i < _order; i++)
                writer.WriteSigned(_samples[i], _bits);
            writer.Write(0, 2);
            writer.Write((uint)_partitionOrder, 4);
            var count = _size >> _partitionOrder;
            for (var p = 0; p < _parameters.Length; p++)
            {
                var k = _parameters[p];
                writer.Write((uint)k, 4);
                for (var i = Math.Max(p * count, _order); i < (p + 1) * count; i++)
                {
                    var folded = Fold(_residual[i]);
                    writer.WriteZeros((int)(folded >> k));
                    writer.Write(1, 1);
                    if (k > 0)
                        writer.Write(folded & ((1u << k) - 1), k);
                }
            }
        }
    }

    /// <summary>
    /// Writes bits most significant first into a buffer that grows as it needs to.
    /// </summary>
    sealed class BitWriter
    {
        byte[] _buffer = new byte[64 * 1024];
        int _length;
        ulong _pending;
        int _pendingBits;

        public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _length);

        public void Reset() => (_length, _pending, _pendingBits) = (0, 0, 0);

        public void Write(uint value, int bits)
        {
            if (bits == 0)
                return;
            _pending = (_pending << bits) | (value & (bits == 32 ? uint.MaxValue : (1u << bits) - 1));
            _pendingBits += bits;
            while (_pendingBits >= 8)
            {
                _pendingBits -= 8;
                if (_length == _buffer.Length)
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                _buffer[_length++] = (byte)(_pending >> _pendingBits);
            }
            _pending &= (1UL << _pendingBits) - 1;
        }

        public void WriteSigned(int value, int bits) => Write((uint)value, bits);

        public void WriteZeros(int count)
        {
            for (; count >= 32; count -= 32)
                Write(0, 32);
            Write(0, count);
        }

        // Pads with zeros to a whole byte.
        public void Align()
        {
            if (_pendingBits > 0)
                Write(0, 8 - _pendingBits);
        }
    }
}
