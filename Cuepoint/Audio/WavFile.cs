using System.Text;

namespace Cuepoint.Audio;

/// <summary>
/// Writes a song as a 24-bit WAV, with its title, artist, album and genre tagged twice over: in a RIFF INFO list, and
/// in an "id3 " chunk, as Rekordbox and Serato read. Windows reads neither from a WAV, so <see cref="ReadTags"/> does.
/// </summary>
static class WavFile
{
    const int BYTES_PER_SAMPLE = 3;

    const uint RIFF = 0x46464952;
    const uint WAVE = 0x45564157;
    const uint FMT = 0x20746D66;
    const uint DATA = 0x61746164;
    const uint LIST = 0x5453494C;
    const uint INFO = 0x4F464E49;
    const uint ID3_LOWER = 0x20336469;
    const uint ID3_UPPER = 0x20334449;

    public static bool IsWav(string path) => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tags of any song: read here for a WAV or an AIFF, which Windows has no tags for, and from Windows for the
    /// rest. All null if there are none, or the file can't be read.
    /// </summary>
    public static SongTags ReadAnyTags(string path) =>
        Aiff.IsAiff(path) ? Aiff.ReadTags(path)
        : IsWav(path) ? ReadTags(path)
        : OperatingSystem.IsWindows() ? WindowsTags.Read(path)
        : default;

    /// <summary>
    /// A WAV's tags, from its "id3 " chunk or failing that its INFO list, and its length; what Windows has, if it has
    /// neither. All null if it can't be read.
    /// </summary>
    public static SongTags ReadTags(string path)
    {
        SongTags own = default;
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(file);
            if (file.Length >= 12 && reader.ReadUInt32() == RIFF && reader.ReadUInt32() >= 0 && reader.ReadUInt32() == WAVE)
            {
                SongTags id3 = default, info = default;
                long frameBytes = 0, rate = 0, dataBytes = -1;
                while (file.Position + 8 <= file.Length)
                {
                    var id = reader.ReadUInt32();
                    var length = (long)reader.ReadUInt32();
                    var start = file.Position;
                    var body = Math.Min(length, file.Length - start);
                    if (id == FMT && body >= 16)
                    {
                        reader.ReadUInt16();
                        reader.ReadUInt16();
                        rate = reader.ReadUInt32();
                        reader.ReadUInt32();
                        frameBytes = reader.ReadUInt16();
                    }
                    else if (id == DATA)
                        dataBytes = body;
                    else if (id is ID3_LOWER or ID3_UPPER && body <= 16 << 20)
                        id3 = Id3Text.Read(reader.ReadBytes((int)body));
                    else if (id == LIST && body is >= 4 and <= 1 << 20 && reader.ReadUInt32() == INFO)
                        info = ReadInfo(reader.ReadBytes((int)body - 4));
                    file.Position = start + length + (length & 1);
                }
                TimeSpan? duration = rate > 0 && frameBytes > 0 && dataBytes >= 0 ? TimeSpan.FromSeconds((double)(dataBytes / frameBytes) / rate) : null;
                own = new SongTags(id3.Title ?? info.Title, id3.Artist ?? info.Artist, id3.Album ?? info.Album, duration, id3.Genre ?? info.Genre);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            // Whatever Windows makes of it, then.
        }
        if (own.Title is not null || !OperatingSystem.IsWindows())
            return own;
        var windows = WindowsTags.Read(path);
        return new SongTags(windows.Title, windows.Artist, windows.Album, own.Duration ?? windows.Duration, windows.Genre);
    }

    static SongTags ReadInfo(ReadOnlySpan<byte> list)
    {
        string? title = null, artist = null, album = null, genre = null;
        for (var at = 0; at + 8 <= list.Length;)
        {
            var id = Encoding.ASCII.GetString(list.Slice(at, 4));
            var length = (int)Math.Min(BitConverter.ToUInt32(list.Slice(at + 4, 4)), (uint)(list.Length - at - 8));
            var text = Encoding.UTF8.GetString(list.Slice(at + 8, length)).TrimEnd('\0').Trim();
            if (text.Length > 0)
            {
                switch (id)
                {
                    case "INAM":
                        title = text;
                        break;
                    case "IART":
                        artist = text;
                        break;
                    case "IPRD":
                        album = text;
                        break;
                    case "IGNR":
                        genre = text;
                        break;
                }
            }
            at += 8 + length + (length & 1);
        }
        return new SongTags(title, artist, album, null, genre);
    }

    /// <summary>
    /// Writes <paramref name="audio"/> to <paramref name="path"/>, by way of a file beside it, so a half-written one is
    /// never left under its name; a file there already is only replaced with <paramref name="replace"/>, and then
    /// only once the new one's whole. Samples past full scale are clipped. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if it can't.
    /// </summary>
    public static void Write(string path, PcmAudio audio, SongTags tags, bool replace = false)
    {
        if (!replace && File.Exists(path))
            throw new IOException($"There's a file at {path} already");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                var info = Info(tags);
                var id3 = Id3(tags);
                var dataBytes = (long)audio.Samples.Length * BYTES_PER_SAMPLE;
                if (dataBytes + info.Length + id3.Length + 64 > uint.MaxValue)
                    throw new IOException("The song's too long for a WAV file");
                var frameBytes = audio.Channels * BYTES_PER_SAMPLE;

                writer.Write("RIFF"u8);
                writer.Write((uint)(4 + 8 + 16 + info.Length + id3.Length + 8 + dataBytes + (dataBytes & 1)));
                writer.Write("WAVE"u8);
                writer.Write("fmt "u8);
                writer.Write(16u);
                writer.Write((ushort)1); // PCM
                writer.Write((ushort)audio.Channels);
                writer.Write((uint)audio.SampleRate);
                writer.Write((uint)(audio.SampleRate * frameBytes));
                writer.Write((ushort)frameBytes);
                writer.Write((ushort)(8 * BYTES_PER_SAMPLE));
                writer.Write(info);
                writer.Write(id3);
                writer.Write("data"u8);
                writer.Write((uint)dataBytes);

                var bytes = new byte[64 * 1024 * BYTES_PER_SAMPLE];
                for (var start = 0; start < audio.Samples.Length; start += bytes.Length / BYTES_PER_SAMPLE)
                {
                    var samples = audio.Samples.AsSpan(start, Math.Min(bytes.Length / BYTES_PER_SAMPLE, audio.Samples.Length - start));
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var value = (int)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607f);
                        bytes[i * 3] = (byte)value;
                        bytes[i * 3 + 1] = (byte)(value >> 8);
                        bytes[i * 3 + 2] = (byte)(value >> 16);
                    }
                    writer.Write(bytes, 0, samples.Length * BYTES_PER_SAMPLE);
                }
                if ((dataBytes & 1) != 0)
                    writer.Write((byte)0);
            }
            File.Move(temporary, path, overwrite: replace);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// The LIST chunk of INFO, whole: INAM for the title, IART the artist, IPRD the album, IGNR the genre, and ISFT
    /// what made it.
    /// </summary>
    static byte[] Info(SongTags tags)
    {
        using var body = new MemoryStream();
        body.Write("INFO"u8);
        foreach (var (id, text) in new[] { ("INAM", tags.Title), ("IART", tags.Artist), ("IPRD", tags.Album), ("IGNR", tags.Genre), ("ISFT", "Cuepoint") })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var bytes = Encoding.UTF8.GetBytes(text.Trim() + "\0");
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write(BitConverter.GetBytes((uint)bytes.Length));
            body.Write(bytes);
            if ((bytes.Length & 1) != 0)
                body.WriteByte(0);
        }
        return Chunk("LIST"u8, body.ToArray());
    }

    /// <summary>
    /// The "id3 " chunk, whole: an ID3v2.3 tag of the title (TIT2), artist (TPE1), album (TALB) and genre (TCON), in
    /// UTF-16 as version 2.3 has it.
    /// </summary>
    static byte[] Id3(SongTags tags)
    {
        using var frames = new MemoryStream();
        foreach (var (id, text) in new[] { ("TIT2", tags.Title), ("TPE1", tags.Artist), ("TALB", tags.Album), ("TCON", tags.Genre) })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            byte[] bytes = [1, .. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text.Trim())];
            frames.Write(Encoding.ASCII.GetBytes(id));
            frames.Write([(byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length, 0, 0]);
            frames.Write(bytes);
        }
        var size = (int)frames.Length;
        var tag = new byte[10 + size];
        "ID3"u8.CopyTo(tag);
        tag[3] = 3;
        // The size is syncsafe: seven bits to a byte.
        (tag[6], tag[7], tag[8], tag[9]) = ((byte)(size >> 21 & 0x7F), (byte)(size >> 14 & 0x7F), (byte)(size >> 7 & 0x7F), (byte)(size & 0x7F));
        frames.ToArray().CopyTo(tag, 10);
        return Chunk("id3 "u8, tag);
    }

    // A RIFF chunk: its id, its length, and its body, padded to an even length.
    static byte[] Chunk(ReadOnlySpan<byte> id, byte[] body)
    {
        var chunk = new byte[8 + body.Length + (body.Length & 1)];
        id.CopyTo(chunk);
        BitConverter.TryWriteBytes(chunk.AsSpan(4), (uint)body.Length);
        body.CopyTo(chunk, 8);
        return chunk;
    }
}
