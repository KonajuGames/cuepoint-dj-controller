using System.Buffers.Binary;
using System.Text;
using Cuepoint.Audio;
using Cuepoint.Music;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// AIFF and AIFF-C, decoded and tagged by <see cref="Aiff"/>, from files written here.
/// </summary>
public sealed class AiffTests : IDisposable
{
    const int RATE = 44100;
    const int FRAMES = 4410;

    readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    static float Expected(int sample) => (float)(Math.Sin(sample / 2 * 2 * Math.PI * 440 / RATE) * 0.5);

    [Theory]
    [InlineData(8, null)]
    [InlineData(16, null)]
    [InlineData(24, null)]
    [InlineData(32, null)]
    [InlineData(16, "NONE")]
    [InlineData(16, "sowt")]
    [InlineData(24, "sowt")]
    [InlineData(32, "fl32")]
    [InlineData(64, "fl64")]
    public void Each_kind_decodes_to_its_samples_and_stops_at_the_tag_after_them(int bits, string? compression)
    {
        var path = _folder["song.aiff"];
        Write(path, bits, compression, Id3(("TIT2", "A Title")));

        var (pcm, title, _) = AudioDecoder.Decode(path);

        Assert.Equal((2, RATE, FRAMES), (pcm.Channels, pcm.SampleRate, (int)pcm.Frames));
        var tolerance = bits == 8 ? 0.01f : 0.0001f;
        for (var i = 0; i < pcm.Samples.Length; i++)
            Assert.InRange(pcm.Samples[i], Expected(i) - tolerance, Expected(i) + tolerance);
        Assert.Equal("A Title", title);
    }

    [Fact]
    public void Tags_and_length_come_from_the_ID3_chunk_and_the_format()
    {
        var path = _folder["song.aif"];
        Write(path, 16, null, Id3(("TIT2", "Café"), ("TPE1", "Some Artist"), ("TALB", "An Album")));

        var tags = Aiff.ReadTags(path);

        Assert.Equal(("Café", "Some Artist", "An Album"), (tags.Title, tags.Artist, tags.Album));
        Assert.Equal(TimeSpan.FromSeconds(0.1), tags.Duration);
    }

    [Fact]
    public void Without_an_ID3_tag_the_NAME_and_AUTH_chunks_name_it()
    {
        var path = _folder["song.aiff"];
        Write(path, 16, null, null, ("NAME", "Old Title"u8.ToArray()), ("AUTH", "Old Artist"u8.ToArray()));

        Assert.Equal(("Old Title", "Old Artist"), (Aiff.ReadTags(path).Title, Aiff.ReadTags(path).Artist));
    }

    [Fact]
    public void Anything_else_has_no_tags_rather_than_failing()
    {
        var path = _folder["song.aiff"];
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Equal(default, Aiff.ReadTags(path));
        Assert.Null(Aiff.ReadId3(path));
    }

    [Fact]
    public void A_library_takes_in_AIFFs()
    {
        Assert.True(MusicLibrary.Extensions.Contains(".aif"));
        Assert.True(MusicLibrary.Extensions.Contains(".AIFF"));
        Assert.True(MusicLibrary.Extensions.Contains(".aifc"));
    }

    [Theory]
    [InlineData(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }, 44100)]
    [InlineData(new byte[] { 0x40, 0x0E, 0xBB, 0x80, 0, 0, 0, 0, 0, 0 }, 48000)]
    public void Sample_rates_are_read_from_80_bit_floats(byte[] bytes, double rate) => Assert.Equal(rate, Aiff.Extended(bytes));

    /// <summary>
    /// An ID3v2.3 tag of text frames, UTF-16 with a byte-order mark.
    /// </summary>
    static byte[] Id3(params (string Id, string Text)[] frames)
    {
        var body = new MemoryStream();
        foreach (var (id, text) in frames)
        {
            byte[] value = [1, .. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write(BigEndian(value.Length));
            body.Write([0, 0]);
            body.Write(value);
        }
        var size = (int)body.Length;
        byte[] header = [.. "ID3"u8, 3, 0, 0, (byte)(size >> 21 & 0x7f), (byte)(size >> 14 & 0x7f), (byte)(size >> 7 & 0x7f), (byte)(size & 0x7f)];
        return [.. header, .. body.ToArray()];
    }

    /// <summary>
    /// A tenth of a second of a 440 Hz sine at half volume, in stereo, then the ID3 tag after the samples, as iTunes
    /// and Rekordbox put it.
    /// </summary>
    static void Write(string path, int bits, string? compression, byte[]? id3, params (string Id, byte[] Data)[] more)
    {
        var bytes = bits / 8;
        var comm = new MemoryStream();
        comm.Write(BigEndian((short)2));
        comm.Write(BigEndian(FRAMES));
        comm.Write(BigEndian((short)bits));
        comm.Write([0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0]);
        if (compression != null)
            comm.Write([.. Encoding.ASCII.GetBytes(compression), 0, 0]);

        var ssnd = new byte[8 + FRAMES * 2 * bytes];
        for (var i = 0; i < FRAMES * 2; i++)
        {
            double value = Expected(i);
            var at = ssnd.AsSpan(8 + i * bytes);
            switch (compression)
            {
                case "fl32":
                    BinaryPrimitives.WriteSingleBigEndian(at, (float)value);
                    break;
                case "fl64":
                    BinaryPrimitives.WriteDoubleBigEndian(at, value);
                    break;
                default:
                    var whole = (long)Math.Round(value * ((1L << (bits - 1)) - 1));
                    for (var b = 0; b < bytes; b++)
                        at[compression == "sowt" ? b : bytes - 1 - b] = (byte)(whole >> (8 * b));
                    break;
            }
        }

        var form = new MemoryStream();
        form.Write(compression != null ? "AIFC"u8 : "AIFF"u8);
        if (compression != null)
            Chunk(form, "FVER", [0xA2, 0x80, 0x51, 0x40]);
        Chunk(form, "COMM", comm.ToArray());
        Chunk(form, "SSND", ssnd);
        foreach (var (id, data) in more)
            Chunk(form, id, data);
        if (id3 != null)
            Chunk(form, "ID3 ", id3);

        var file = new MemoryStream();
        Chunk(file, "FORM", form.ToArray());
        File.WriteAllBytes(path, file.ToArray());
    }

    static void Chunk(Stream to, string id, byte[] data)
    {
        to.Write(Encoding.ASCII.GetBytes(id));
        to.Write(BigEndian(data.Length));
        to.Write(data);
        if (data.Length % 2 == 1)
            to.WriteByte(0);
    }

    static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    static byte[] BigEndian(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }
}
