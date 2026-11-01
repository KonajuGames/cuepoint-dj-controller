using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Brutality.Os2l;

/// <summary>
/// One TCP connection to an OS2L server (the lighting software). It connects, reads and writes on its own tasks, so
/// nothing here waits on the network: messages to send are queued, and messages received are queued for
/// <see cref="TryReceive"/>. Once closed, by either side or a failure, it stays closed; make another to try again.
/// </summary>
sealed class Os2lConnection : IDisposable
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    readonly TcpClient _client = new() { NoDelay = true };
    readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentQueue<string> _incoming = new();
    readonly CancellationTokenSource _stop = new();
    volatile bool _connected;
    volatile bool _closed;

    public Os2lConnection(string name, IPEndPoint endPoint)
    {
        Name = name;
        EndPoint = endPoint;
        _ = Task.Run(Run);
    }

    /// <summary>
    /// What it's connected to: the service's name, or the address for one given directly.
    /// </summary>
    public string Name { get; }

    public IPEndPoint EndPoint { get; }

    public bool IsConnected => _connected && !_closed;
    public bool IsClosed => _closed;

    /// <summary>
    /// Why it closed, if it failed.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Queues a message to send; dropped if it isn't connected.
    /// </summary>
    public void Send(string message)
    {
        if (IsConnected)
            _outgoing.Writer.TryWrite(Encoding.UTF8.GetBytes(message));
    }

    /// <summary>
    /// The next message received, a whole JSON object as text.
    /// </summary>
    public bool TryReceive(out string message) => _incoming.TryDequeue(out message!);

    async Task Run()
    {
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(ConnectTimeout);
                await _client.ConnectAsync(EndPoint, timeout.Token);
            }
            _connected = true;
            var stream = _client.GetStream();
            await Task.WhenAny(Read(stream), Write(stream));
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            Error = "it didn't answer";
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested)
                Error = e.Message;
        }
        finally
        {
            _closed = true;
            _client.Dispose();
        }
    }

    async Task Read(NetworkStream stream)
    {
        var bytes = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        var text = new StringBuilder();
        while (true)
        {
            var count = await stream.ReadAsync(bytes, _stop.Token);
            if (count == 0)
                return;
            var decoded = decoder.GetChars(bytes, 0, count, chars, 0);
            text.Append(chars, 0, decoded);
            foreach (var message in Os2lMessages.Split(text))
                _incoming.Enqueue(message);

            // Something that never closes its braces isn't OS2L.
            if (text.Length > 1 << 20)
                throw new IOException("it sent something that isn't OS2L");
        }
    }

    async Task Write(NetworkStream stream)
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync(_stop.Token))
            await stream.WriteAsync(message, _stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        _client.Dispose();
    }
}
