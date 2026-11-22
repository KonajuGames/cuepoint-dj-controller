using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cuepoint.Streaming;

namespace Cuepoint.Tests.Streaming;

/// <summary>
/// Streaming to a pretend Icecast server on this machine: what's asked of it, and the Ogg Opus it's sent.
/// </summary>
public class StreamingTests
{
    /// <summary>
    /// An Ogg page as read back: its flags, granule position, serial number, and what's in it.
    /// </summary>
    sealed record Page(byte Flags, long Granule, uint Serial, byte[] Body)
    {
        public bool First => (Flags & 0x02) != 0;
        public bool Last => (Flags & 0x04) != 0;
        public string Start => Encoding.ASCII.GetString(Body, 0, Math.Min(8, Body.Length));
    }

    static List<Page> Pages(byte[] data)
    {
        var pages = new List<Page>();
        var at = 0;
        while (at + 27 <= data.Length && Encoding.ASCII.GetString(data, at, 4) == "OggS")
        {
            var segments = data[at + 26];
            if (at + 27 + segments > data.Length)
                break;
            var length = 0;
            for (var i = 0; i < segments; i++)
                length += data[at + 27 + i];
            var total = 27 + segments + length;
            if (at + total > data.Length)
                break;
            var page = data.AsSpan(at, total);
            Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(page[22..]), OggOpusStream.Crc(page));
            pages.Add(new Page(page[5], BinaryPrimitives.ReadInt64LittleEndian(page[6..]), BinaryPrimitives.ReadUInt32LittleEndian(page[14..]), page[(27 + segments)..].ToArray()));
            at += total;
        }
        return pages;
    }

    [Fact]
    public void A_stream_begins_with_its_head_and_tags_and_a_new_title_chains_a_new_stream_on()
    {
        var ogg = new OggOpusStream(2, 312, serial: 7);
        var bytes = new MemoryStream();
        bytes.Write(ogg.Begin("Glass Horizon - Northern Static"));
        bytes.Write(ogg.Audio([1, 2, 3], 960));
        bytes.Write(ogg.Audio(new byte[600], 960, last: true));
        bytes.Write(ogg.Begin("Mira Vale - Afterglow Avenue"));
        bytes.Write(ogg.Audio([4], 960));

        var pages = Pages(bytes.ToArray());

        Assert.Equal(["OpusHead", "OpusTags"], pages.Take(2).Select(page => page.Start));
        Assert.True(pages[0].First);
        Assert.Equal(312, BinaryPrimitives.ReadUInt16LittleEndian(pages[0].Body.AsSpan(10)));
        Assert.Contains("TITLE=Glass Horizon - Northern Static", Encoding.UTF8.GetString(pages[1].Body));
        // Granules count from the pre-skip, in 48 kHz samples.
        Assert.Equal(312 + 960, pages[2].Granule);
        Assert.True(pages[3].Last);
        Assert.Equal(600, pages[3].Body.Length);
        Assert.True(pages[4].First);
        Assert.Equal(8u, pages[4].Serial);
        Assert.Contains("TITLE=Mira Vale - Afterglow Avenue", Encoding.UTF8.GetString(pages[5].Body));
        Assert.Equal(312 + 960, pages[6].Granule);
    }

    /// <summary>
    /// A pretend Icecast: takes a source's request, answers it as told, and keeps what's sent after.
    /// </summary>
    sealed class FakeIcecast : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly Thread _thread;
        readonly MemoryStream _received = new();
        public readonly List<string> Requests = [];
        public string Answer = "HTTP/1.1 100 Continue\r\n\r\n";
        public int CloseAfterBytes = int.MaxValue;
        int _connections;

        public FakeIcecast()
        {
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref _connections);

        public byte[] Received
        {
            get
            {
                lock (_received)
                    return _received.ToArray();
            }
        }

        public string FirstRequest
        {
            get
            {
                lock (Requests)
                    return Requests.Count > 0 ? Requests[0] : "";
            }
        }

        void Serve()
        {
            try
            {
                while (true)
                {
                    using var client = _listener.AcceptTcpClient();
                    Interlocked.Increment(ref _connections);
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var b = stream.ReadByte();
                        if (b < 0)
                            break;
                        request.Append((char)b);
                    }
                    lock (Requests)
                        Requests.Add(request.ToString());
                    stream.Write(Encoding.ASCII.GetBytes(Answer));
                    var buffer = new byte[4096];
                    var taken = 0;
                    int read;
                    while (taken < CloseAfterBytes && (read = stream.Read(buffer)) > 0)
                    {
                        lock (_received)
                            _received.Write(buffer, 0, read);
                        taken += read;
                    }
                }
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        public void Dispose() => _listener.Stop();
    }

    static StreamServer Server(int port) => new("127.0.0.1", port, "live", "hackme", "Cuepoint test", 128);

    static bool Within(TimeSpan time, Func<bool> done)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            if (done())
                return true;
            Thread.Sleep(20);
        }
        return done();
    }

    static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * 48000)).SelectMany(i => Enumerable.Repeat((float)(0.3 * Math.Sin(i * 0.05)), 2)).ToArray();

    [Fact]
    public void It_asks_for_the_mount_point_with_the_password_then_streams_Ogg_Opus_titled_with_the_song_playing()
    {
        using var server = new FakeIcecast();
        using var stream = new LiveStream(Server(server.Port)) { Title = "Glass Horizon - Northern Static" };

        Assert.True(Within(TimeSpan.FromSeconds(5), () => stream.State == StreamState.Live));
        stream.Write(Tone(0.5));
        Assert.True(Within(TimeSpan.FromSeconds(5), () => Pages(server.Received).Count >= 10));
        stream.Title = "Mira Vale - Afterglow Avenue";
        stream.Write(Tone(0.5));
        Assert.True(Within(TimeSpan.FromSeconds(5), () => Encoding.UTF8.GetString(server.Received).Contains("TITLE=Mira Vale")));

        var request = server.FirstRequest;
        Assert.StartsWith("PUT /live HTTP/1.1\r\n", request);
        Assert.Contains($"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("source:hackme"))}\r\n", request);
        Assert.Contains("Content-Type: audio/ogg\r\n", request);
        Assert.Contains("Ice-Name: Cuepoint test\r\n", request);
        var pages = Pages(server.Received);
        Assert.Equal("OpusHead", pages[0].Start);
        Assert.Contains("TITLE=Glass Horizon - Northern Static", Encoding.UTF8.GetString(pages[1].Body));
        Assert.Contains(pages, page => page.Last);
        Assert.True(stream.BytesSent > 0);

        // Every audio page decodes as 20 ms of stereo Opus, and some of it isn't silence.
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 2);
        var output = new float[960 * 2];
        var heard = 0f;
        foreach (var page in pages.Where(page => page.Start is not ("OpusHead" or "OpusTags")))
        {
            Assert.Equal(960, decoder.Decode(page.Body, output, 960));
            heard = MathF.Max(heard, output.Max(MathF.Abs));
        }
        Assert.True(heard > 0.1f);
    }

    [Fact]
    public void A_password_turned_down_stops_it_and_says_so()
    {
        using var server = new FakeIcecast { Answer = "HTTP/1.1 401 Unauthorized\r\n\r\n" };
        using var stream = new LiveStream(Server(server.Port));

        Assert.True(Within(TimeSpan.FromSeconds(5), () => stream.State == StreamState.Stopped));
        Assert.Equal("The server turned down the password", stream.Problem);
    }

    [Fact]
    public void A_connection_dropped_is_made_again()
    {
        using var server = new FakeIcecast { CloseAfterBytes = 2000 };
        using var stream = new LiveStream(Server(server.Port));
        stream.Write(Tone(1));

        Assert.True(Within(TimeSpan.FromSeconds(5), () => stream.State == StreamState.Retrying));
        Assert.Contains("trying again in 2 seconds", stream.Problem);
        Assert.True(Within(TimeSpan.FromSeconds(8), () => server.Connections >= 2));
    }

    [Fact]
    public void Nobody_there_is_tried_again_rather_than_given_up_on()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var stream = new LiveStream(Server(port));

        Assert.True(Within(TimeSpan.FromSeconds(10), () => stream.State == StreamState.Retrying));
        Assert.StartsWith("The server isn't answering", stream.Problem);
    }

    [Fact]
    public void A_password_kept_in_the_settings_is_encrypted_and_reads_back()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var saved = SavedSecret.Protect("hackme");

        Assert.NotNull(saved);
        Assert.DoesNotContain("hackme", saved);
        Assert.Equal("hackme", SavedSecret.Unprotect(saved));
        Assert.Equal("", SavedSecret.Unprotect("not one of ours"));
        Assert.Equal("", SavedSecret.Protect(""));
    }
}
