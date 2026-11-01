using System.Collections.Concurrent;

namespace Brutality.Midi;

/// <summary>
/// A connected controller: its input, and its output of the same name if it has one, for lights.
/// </summary>
sealed class MidiDevice(string name, IDisposable input, IMidiOutput? output)
{
    public string Name { get; } = name;
    internal IDisposable Input { get; } = input;
    internal IMidiOutput? Output { get; } = output;

    public bool CanSend => Output is not null;

    public void Send(MidiMessage message) => Output?.Send(message);
}

/// <summary>
/// Keeps every MIDI input open, with its matching output, as controllers come and go: the list is checked every
/// couple of seconds, so plugging one in mid-set just works. What they send queues up to be read on the UI thread.
/// </summary>
sealed class MidiDevices : IDisposable
{
    const double SCAN_SECONDS = 2;

    readonly IMidiPorts _ports;
    readonly List<MidiDevice> _devices = [];
    readonly ConcurrentQueue<(MidiDevice Device, MidiMessage Message)> _received = new();
    double _nextScan = double.NegativeInfinity;

    public MidiDevices(IMidiPorts ports) => _ports = ports;

    public IReadOnlyList<MidiDevice> Devices => _devices;

    public event Action<MidiDevice>? Connected;
    public event Action<MidiDevice>? Disconnected;

    /// <summary>
    /// Looks for devices plugged in or out, if it's time to. <paramref name="now"/> is in seconds, from any start.
    /// </summary>
    public void Update(double now)
    {
        if (now < _nextScan)
            return;
        _nextScan = now + SCAN_SECONDS;
        Scan();
    }

    public void Scan()
    {
        var inputs = _ports.InputNames();

        foreach (var gone in _devices.Where(device => !inputs.Contains(device.Name)).ToList())
        {
            _devices.Remove(gone);
            Close(gone);
            Disconnected?.Invoke(gone);
        }

        for (var index = 0; index < inputs.Count; index++)
        {
            var name = inputs[index];
            if (name == "" || _devices.Any(device => device.Name == name))
                continue;

            MidiDevice? device = null;
            var input = _ports.OpenInput(index, message => _received.Enqueue((device!, message)));
            if (input is null)
                continue;
            var outputIndex = _ports.OutputNames().ToList().IndexOf(name);
            device = new MidiDevice(name, input, outputIndex >= 0 ? _ports.OpenOutput(outputIndex) : null);
            _devices.Add(device);
            Connected?.Invoke(device);
        }
    }

    /// <summary>
    /// The next message received, with the device it came from, if there is one.
    /// </summary>
    public bool TryRead(out MidiDevice device, out MidiMessage message)
    {
        while (_received.TryDequeue(out var item))
        {
            // One that arrived as its device was closing, or before the device was recorded, goes.
            if (item.Device is null || !_devices.Contains(item.Device))
                continue;
            (device, message) = item;
            return true;
        }
        (device, message) = (null!, default);
        return false;
    }

    static void Close(MidiDevice device)
    {
        device.Input.Dispose();
        device.Output?.Dispose();
    }

    public void Dispose()
    {
        foreach (var device in _devices)
            Close(device);
        _devices.Clear();
    }
}
