using System.Text;
using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// Covers read from songs' tags, as a deck shows them.
/// </summary>
public class CoverArtTests
{
    [Fact]
    public void A_cover_is_scaled_down_to_fit_keeping_its_shape_but_never_up()
    {
        Assert.Equal((80, 80), CoverArt.Fit(600, 600, 80));
        Assert.Equal((80, 45), CoverArt.Fit(1280, 720, 80));
        Assert.Equal((40, 80), CoverArt.Fit(300, 600, 80));
        Assert.Equal((50, 30), CoverArt.Fit(50, 30, 80));
    }

    /// <summary>
    /// An MP3 made here, its cover the app's own logo; and one without a cover, and a file that isn't there.
    /// </summary>
    [Fact]
    public void A_song_s_cover_comes_back_as_pixels_and_a_song_without_one_gives_nothing()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var folder = new TempFolder();
        var logo = new MemoryStream();
        using (var resource = typeof(Deck).Assembly.GetManifestResourceStream("logo-32.png")!)
            resource.CopyTo(logo);

        var withCover = folder["cover.mp3"];
        File.WriteAllBytes(withCover, Mp3(logo.ToArray()));
        var cover = CoverArt.Read(withCover, 16);
        Assert.NotNull(cover);
        Assert.Equal((16, 16), (cover.Width, cover.Height));
        Assert.Equal(16 * 16 * 4, cover.Rgba.Length);
        // The logo's not all see-through.
        Assert.Contains(cover.Rgba.Where((_, i) => i % 4 == 3), alpha => alpha > 0);

        // At its own size when that's smaller.
        Assert.Equal((32, 32), CoverArt.Read(withCover, 80) is { } full ? (full.Width, full.Height) : default);

        var without = folder["plain.mp3"];
        File.WriteAllBytes(without, Mp3(null));
        Assert.Null(CoverArt.Read(without, 16));
        Assert.Null(CoverArt.Read(folder["missing.mp3"], 16));
    }

    /// <summary>
    /// A second of silent MP3, with an ID3v2.3 tag giving it a title and, if <paramref name="png"/> isn't null, that
    /// picture as its front cover.
    /// </summary>
    static byte[] Mp3(byte[]? png)
    {
        var frames = new MemoryStream();
        void Frame(string id, byte[] body)
        {
            frames.Write(Encoding.ASCII.GetBytes(id));
            frames.Write([(byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, 0, 0]);
            frames.Write(body);
        }

        Frame("TIT2", [0, .. Encoding.ASCII.GetBytes("Covered")]);
        if (png is not null)
            // Text in Latin-1, the picture's type, "front cover", and no description.
            Frame("APIC", [0, .. Encoding.ASCII.GetBytes("image/png"), 0, 3, 0, .. png]);

        var tag = frames.ToArray();
        var file = new MemoryStream();
        file.Write("ID3"u8);
        file.Write([3, 0, 0]);
        // The tag's size, in four lots of seven bits.
        file.Write([(byte)((tag.Length >> 21) & 0x7F), (byte)((tag.Length >> 14) & 0x7F), (byte)((tag.Length >> 7) & 0x7F), (byte)(tag.Length & 0x7F)]);
        file.Write(tag);

        // MPEG-1 layer III frames at 128 kbit/s and 44.1 kHz, 417 bytes each, of silence.
        for (var i = 0; i < 39; i++)
        {
            var frame = new byte[417];
            (frame[0], frame[1], frame[2], frame[3]) = (0xFF, 0xFB, 0x90, 0x64);
            file.Write(frame);
        }
        return file.ToArray();
    }
}
