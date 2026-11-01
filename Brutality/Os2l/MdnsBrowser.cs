using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Brutality.Os2l;

/// <summary>
/// Finds services on the local network by multicast DNS (what Bonjour and Avahi answer), the way OS2L lighting
/// software is found: asks every few seconds who offers the service, and follows the answers to an address and
/// port. It asks for unicast replies from its own port, so it needs nothing else listening on 5353. Services not
/// heard from for a while are dropped.
/// </summary>
sealed class MdnsBrowser : IDisposable
{
    static readonly IPEndPoint MulticastGroup = new(new IPAddress([224, 0, 0, 251]), 5353);

    const ushort TYPE_A = 1, TYPE_PTR = 12, TYPE_SRV = 33;
    const ushort CLASS_IN = 1;
    const ushort UNICAST_RESPONSE = 0x8000;

    static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(4);
    static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(20);

    readonly string _service;
    readonly UdpClient _socket;
    readonly CancellationTokenSource _stop = new();

    // What the answers have said so far: instance names under the service, where each instance is, and hosts'
    // addresses, each with when it was last heard.
    readonly ConcurrentDictionary<string, DateTime> _instances = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (string Host, int Port)> _locations = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="service">The service type, as "_os2l._tcp".</param>
    public MdnsBrowser(string service)
    {
        _service = service.TrimEnd('.') + ".local";
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        _ = Task.Run(Listen);
        _ = Task.Run(Ask);
    }

    /// <summary>
    /// The services found and still heard from: each instance's name (the part before the service type, as
    /// "sLight"), and where to connect.
    /// </summary>
    public IReadOnlyList<(string Name, IPEndPoint EndPoint)> Services
    {
        get
        {
            var found = new List<(string, IPEndPoint)>();
            foreach (var (instance, heard) in _instances)
            {
                if (DateTime.UtcNow - heard > ForgetAfter)
                    continue;
                if (!_locations.TryGetValue(instance, out var location) || !_addresses.TryGetValue(location.Host, out var address))
                    continue;
                var name = instance.EndsWith("." + _service, StringComparison.OrdinalIgnoreCase) ? instance[..^(_service.Length + 1)] : instance;
                found.Add((name, new IPEndPoint(address, location.Port)));
            }
            return found;
        }
    }

    async Task Ask()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Send(Query((_service, TYPE_PTR)));

                // Follow up whatever the answers left out: where an instance is, and a host's address.
                foreach (var instance in _instances.Keys.Where(instance => !_locations.ContainsKey(instance)))
                    await Send(Query((instance, TYPE_SRV)));
                foreach (var host in _locations.Values.Select(location => location.Host).Where(host => !_addresses.ContainsKey(host)).Distinct())
                    await Send(Query((host, TYPE_A)));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
            try
            {
                await Task.Delay(AskEvery, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task Send(byte[] query) => await _socket.SendAsync(query, MulticastGroup, _stop.Token);

    async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var received = await _socket.ReceiveAsync(_stop.Token);
                Read(received.Buffer);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Windows reports an earlier send's failure (an unreachable port, say) on the next receive.
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
                // A malformed packet from someone; ignore it.
            }
        }
    }

    /// <summary>
    /// A query for each name and type, asking for answers sent straight back.
    /// </summary>
    static byte[] Query(params (string Name, ushort Type)[] questions)
    {
        var packet = new List<byte>(64);
        void U16(int value)
        {
            packet.Add((byte)(value >> 8));
            packet.Add((byte)value);
        }

        U16(0); // id
        U16(0); // flags: a standard query
        U16(questions.Length);
        U16(0);
        U16(0);
        U16(0);
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                packet.Add((byte)bytes.Length);
                packet.AddRange(bytes);
            }
            packet.Add(0);
            U16(type);
            U16(CLASS_IN | UNICAST_RESPONSE);
        }
        return [.. packet];
    }

    /// <summary>
    /// Takes in every record in a response that bears on the service: in its answers, or the extra ones a responder
    /// adds to save asking again.
    /// </summary>
    internal void Read(byte[] packet)
    {
        if (packet.Length < 12 || (packet[2] & 0x80) == 0)
            return; // a query, not a response
        var questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        var records = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)) + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));

        var at = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(packet, ref at);
            at += 4;
        }

        var now = DateTime.UtcNow;
        for (var i = 0; i < records; i++)
        {
            var name = ReadName(packet, ref at);
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(at + 4));
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at + 8));
            var data = at + 10;
            at = data + length;
            if (at > packet.Length)
                throw new InvalidDataException("mDNS record runs past the end of the packet");

            switch (type)
            {
                case TYPE_PTR when string.Equals(name, _service, StringComparison.OrdinalIgnoreCase):
                {
                    var instance = ReadName(packet, ref data);
                    // A time to live of 0 says it's going away.
                    if (ttl == 0)
                        _instances.TryRemove(instance, out _);
                    else
                        _instances[instance] = now;
                    break;
                }
                case TYPE_SRV:
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(data + 4));
                    data += 6;
                    var host = ReadName(packet, ref data);
                    _locations[name] = (host, port);
                    break;
                }
                case TYPE_A when length == 4:
                    _addresses[name] = new IPAddress(packet.AsSpan(data, 4));
                    break;
            }
        }
    }

    /// <summary>
    /// A name, label by label, following compression pointers back into the packet.
    /// </summary>
    static string ReadName(byte[] packet, ref int at)
    {
        var labels = new List<string>();
        var position = at;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            var length = packet[position];
            if (length == 0)
            {
                position++;
                if (!jumped)
                    at = position;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                var target = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped)
                    at = position + 2;
                jumped = true;
                position = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(packet, position + 1, length));
            position += 1 + length;
        }
        throw new InvalidDataException("mDNS name loops");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
    }
}
