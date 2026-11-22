using System.Net.Sockets;
using System.Text;
using Concentus;
using Concentus.Enums;

namespace Cuepoint.Streaming;

/// <summary>
/// Where to stream to: an Icecast server, the mount point listeners tune in to, its source password, what to call the
/// stream, and how many kilobits a second of Opus to send.
/// </summary>
sealed record StreamServer(string Host, int Port, string Mount, string Password, string Name, int Kbps)
{
    /// <summary>
    /// The bitrates there are to choose from, in kilobits a second.
    /// </summary>
    public static readonly IReadOnlyList<int> Bitrates = [64, 96, 128, 160, 192, 256];

    /// <summary>
    /// Where listeners tune in, as "http://host:8000/live".
    /// </summary>
    public string ListenAt => $"http://{Host}:{Port}{MountPath}";

    /// <summary>
    /// The mount point with its leading slash.
    /// </summary>
    public string MountPath => Mount.StartsWith('/') ? Mount : "/" + Mount;
}

/// <summary>
/// How a live stream's getting on.
/// </summary>
enum StreamState
{
    Connecting,
    Live,

    /// <summary>
    /// The connection went, or couldn't be made; it's tried again, waiting longer each time.
    /// </summary>
    Retrying,

    /// <summary>
    /// The server turned it down for good (the password, say), or it was stopped.
    /// </summary>
    Stopped,
}

/// <summary>
/// The main mix streamed live to an Icecast server, as Opus in Ogg, with the song playing as the stream's title. The
/// audio thread hands over each block of the mix (<see cref="Write"/>), as it does to the recorder, into a buffer a
/// thread of its own reads, encodes and sends, so the network never holds the audio up. A dropped connection is made
/// again, waiting 2, 4, 8 then up to 30 seconds between tries, and picks up with the mix as it is then rather than
/// falling behind.
/// </summary>
sealed unsafe class LiveStream : IDisposable
{
    const int CHANNELS = 2;

    /// <summary>
    /// 20 ms of audio at a time, Opus's usual frame.
    /// </summary>
    const int FRAME = OggOpusStream.RATE / 50;

    /// <summary>
    /// How much of the mix is held for the sender, in seconds; any more waiting than that is let go.
    /// </summary>
    const int BUFFER_SECONDS = 8;

    /// <summary>
    /// How far behind the mix the stream may start, in seconds, once connected; anything older's skipped.
    /// </summary>
    const double CATCH_UP_SECONDS = 0.5;

    static readonly int[] RetrySeconds = [2, 4, 8, 16, 30];

    readonly StreamServer _server;
    readonly float[] _ring = new float[OggOpusStream.RATE * CHANNELS * BUFFER_SECONDS];
    long _written;
    long _read;
    readonly Thread _thread;
    readonly CancellationTokenSource _stop = new();
    string _title = "";
    int _titleChanged;

    /// <summary>
    /// Starts streaming to <paramref name="server"/> straight away, at first with nothing but silence until the mix
    /// comes in.
    /// </summary>
    public LiveStream(StreamServer server)
    {
        _server = server;
        _thread = new Thread(Run) { IsBackground = true, Name = "Live stream" };
        _thread.Start();
    }

    public StreamServer Server => _server;

    public StreamState State { get; private set; } = StreamState.Connecting;

    /// <summary>
    /// Why it isn't live, while it's retrying or stopped; null while all's well.
    /// </summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// When it last went live, while it is.
    /// </summary>
    public DateTime? LiveSince { get; private set; }

    /// <summary>
    /// How many bytes it's sent, all told.
    /// </summary>
    public long BytesSent => Interlocked.Read(ref _sent);

    long _sent;

    /// <summary>
    /// The stream's title, the song playing, as "Artist - Title"; listeners' players show it as it changes. Set from
    /// the UI thread.
    /// </summary>
    public string Title
    {
        get => Volatile.Read(ref _title);
        set
        {
            if (value == Title)
                return;
            Volatile.Write(ref _title, value);
            Interlocked.Exchange(ref _titleChanged, 1);
        }
    }

    /// <summary>
    /// Takes <paramref name="count"/> interleaved stereo samples of the mix. On the audio thread: it only copies them
    /// in, and if the sender's that far behind, what's oldest is overwritten.
    /// </summary>
    public void Write(float* samples, int count)
    {
        var written = Volatile.Read(ref _written);
        for (var i = 0; i < count; i++)
            _ring[(written + i) % _ring.Length] = samples[i];
        Volatile.Write(ref _written, written + count);
    }

    /// <summary>
    /// Writes managed samples, for tests.
    /// </summary>
    internal void Write(float[] samples)
    {
        fixed (float* at = samples)
            Write(at, samples.Length);
    }

    void Run()
    {
        var stop = _stop.Token;
        var tries = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                State = StreamState.Connecting;
                Stream(stop);
            }
            catch (StreamRefused refused)
            {
                (State, Problem, LiveSince) = (StreamState.Stopped, refused.Message, null);
                return;
            }
            catch (Exception e) when (e is IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                if (stop.IsCancellationRequested)
                    break;
                var wait = RetrySeconds[Math.Min(tries++, RetrySeconds.Length - 1)];
                (State, Problem, LiveSince) = (StreamState.Retrying, $"{Reason(e)}; trying again in {wait} seconds", null);
                stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait));
                continue;
            }
            tries = 0;
        }
        (State, LiveSince) = (StreamState.Stopped, null);
    }

    static string Reason(Exception e) =>
        e switch
        {
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "The server isn't answering",
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "Couldn't find the server",
            TimeoutException => "The server's taking too long to answer",
            _ => $"The connection went ({e.Message.TrimEnd('.')})",
        };

    /// <summary>
    /// Connects, is let in, and sends until the connection goes or it's stopped.
    /// </summary>
    void Stream(CancellationToken stop)
    {
        using var client = Connect(stop);
        var network = client.GetStream();
        network.WriteTimeout = 10_000;
        if (!Handshake(network, "PUT"))
        {
            // An Icecast older than 2.4 only knows the SOURCE way in, on a fresh connection.
            client.Dispose();
            using var again = Connect(stop);
            network = again.GetStream();
            network.WriteTimeout = 10_000;
            if (!Handshake(network, "SOURCE"))
                throw new StreamRefused("The server doesn't take a stream in either way Icecast does");
            Send(network, stop);
            return;
        }
        Send(network, stop);
    }

    TcpClient Connect(CancellationToken stop)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            var connecting = client.ConnectAsync(_server.Host, _server.Port, stop).AsTask();
            if (!connecting.Wait(TimeSpan.FromSeconds(8), stop))
                throw new TimeoutException();
            if (connecting.Exception?.GetBaseException() is { } failed)
                throw failed;
            return client;
        }
        catch (AggregateException e) when (e.GetBaseException() is SocketException or IOException)
        {
            client.Dispose();
            throw e.GetBaseException();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks to send to the mount point, by <paramref name="method"/>. True once let in; false if the server doesn't
    /// know that way; throws <see cref="StreamRefused"/> if it says no for good.
    /// </summary>
    bool Handshake(NetworkStream network, string method)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"source:{_server.Password}"));
        var request = new StringBuilder();
        request.Append(method == "PUT" ? $"PUT {_server.MountPath} HTTP/1.1\r\n" : $"SOURCE {_server.MountPath} ICE/1.0\r\n");
        request.Append($"Host: {_server.Host}:{_server.Port}\r\n");
        request.Append($"Authorization: Basic {credentials}\r\n");
        request.Append("User-Agent: Cuepoint\r\n");
        request.Append("Content-Type: audio/ogg\r\n");
        request.Append($"Ice-Name: {Header(_server.Name)}\r\n");
        request.Append("Ice-Public: 0\r\n");
        request.Append($"Ice-Audio-Info: channels=2;samplerate=48000;bitrate={_server.Kbps}\r\n");
        if (method == "PUT")
            request.Append("Expect: 100-continue\r\n");
        request.Append("\r\n");
        var bytes = Encoding.UTF8.GetBytes(request.ToString());
        network.Write(bytes);

        var status = ReadStatus(network);
        return status switch
        {
            100 or 200 => true,
            400 or 405 or 501 when method == "PUT" => false,
            401 => throw new StreamRefused("The server turned down the password"),
            403 => throw new StreamRefused("The server won't take a stream there: the mount point's in use, or not allowed"),
            404 => throw new StreamRefused("The server has no such mount point"),
            _ => throw new StreamRefused($"The server said no (HTTP {status})"),
        };
    }

    static string Header(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// The status code of the server's answer, its headers read through to the blank line after them.
    /// </summary>
    static int ReadStatus(NetworkStream network)
    {
        network.ReadTimeout = 10_000;
        var answer = new StringBuilder();
        Span<byte> one = stackalloc byte[1];
        try
        {
            while (!answer.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && answer.Length < 8192)
            {
                if (network.Read(one) == 0)
                    throw new IOException("The server closed the connection");
                answer.Append((char)one[0]);
            }
        }
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new TimeoutException();
        }
        var line = answer.ToString().Split("\r\n")[0].Split(' ');
        return line.Length > 1 && int.TryParse(line[1], out var code) ? code : 0;
    }

    void Send(NetworkStream network, CancellationToken stop)
    {
        var encoder = OpusCodecFactory.CreateEncoder(OggOpusStream.RATE, CHANNELS, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = _server.Kbps * 1000;
        var ogg = new OggOpusStream(CHANNELS, encoder.Lookahead, (uint)Random.Shared.Next());
        Interlocked.Exchange(ref _titleChanged, 0);
        Sent(network, ogg.Begin(Title));

        // From the mix as it is now, not what piled up while connecting.
        var behind = Volatile.Read(ref _written) - (long)(CATCH_UP_SECONDS * OggOpusStream.RATE * CHANNELS);
        _read = Math.Max(_read, behind);
        (State, Problem, LiveSince) = (StreamState.Live, null, DateTime.Now);

        var frame = new float[FRAME * CHANNELS];
        var packet = new byte[4000];
        while (!stop.IsCancellationRequested)
        {
            if (!TakeFrame(frame))
            {
                stop.WaitHandle.WaitOne(5);
                continue;
            }
            var length = encoder.Encode(frame, FRAME, packet, packet.Length);
            if (Interlocked.Exchange(ref _titleChanged, 0) != 0)
            {
                // The old title's stream ended with this packet, the new one's begun after it.
                Sent(network, ogg.Audio(packet.AsSpan(0, length), FRAME, last: true));
                Sent(network, ogg.Begin(Title));
            }
            else
                Sent(network, ogg.Audio(packet.AsSpan(0, length), FRAME));
        }
    }

    void Sent(NetworkStream network, byte[] bytes)
    {
        network.Write(bytes);
        Interlocked.Add(ref _sent, bytes.Length);
    }

    /// <summary>
    /// The next frame of the mix, if there's a whole one waiting; what was overwritten before it was read is skipped.
    /// </summary>
    bool TakeFrame(float[] frame)
    {
        var written = Volatile.Read(ref _written);
        if (written - _read > _ring.Length)
            _read = written - _ring.Length + frame.Length;
        if (written - _read < frame.Length)
            return false;
        for (var i = 0; i < frame.Length; i++)
            frame[i] = _ring[(_read + i) % _ring.Length];
        _read += frame.Length;
        return true;
    }

    /// <summary>
    /// Stops sending and closes the connection, waiting a moment for the thread to finish.
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(3));
        State = StreamState.Stopped;
        _stop.Dispose();
    }
}

/// <summary>
/// The server said no in a way trying again won't change.
/// </summary>
sealed class StreamRefused(string message) : Exception(message);
