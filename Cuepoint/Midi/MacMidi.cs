using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuepoint.Midi;

/// <summary>
/// MIDI through the Mac's CoreMIDI, with nothing to install. Its inputs and outputs are CoreMIDI's sources and
/// destinations, by their display names. Make it on the thread that runs the window: CoreMIDI tells of controllers
/// plugged in and out through that thread's run loop, which the window's events keep turning.
/// </summary>
[SupportedOSPlatform("macos")]
sealed unsafe partial class MacMidi : IMidiPorts
{
    const string CORE_MIDI = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    const string CORE_FOUNDATION = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const uint kCFStringEncodingUTF8 = 0x08000100;

    readonly uint _client;

    public MacMidi()
    {
        var name = CFString("Cuepoint");
        try
        {
            uint client;
            if (MIDIClientCreate(name, 0, 0, &client) == 0)
                _client = client;
        }
        finally
        {
            CFRelease(name);
        }
    }

    public IReadOnlyList<string> InputNames()
    {
        var names = new List<string>();
        var count = (int)MIDIGetNumberOfSources();
        for (var i = 0; i < count; i++)
            names.Add(NameOf(MIDIGetSource((nuint)i)));
        return names;
    }

    public IReadOnlyList<string> OutputNames()
    {
        var names = new List<string>();
        var count = (int)MIDIGetNumberOfDestinations();
        for (var i = 0; i < count; i++)
            names.Add(NameOf(MIDIGetDestination((nuint)i)));
        return names;
    }

    public IDisposable? OpenInput(int index, Action<MidiMessage> received)
    {
        if (_client == 0 || index < 0 || index >= (int)MIDIGetNumberOfSources())
            return null;
        var source = MIDIGetSource((nuint)index);
        if (source == 0)
            return null;

        var input = new Input(received);
        var name = CFString("Cuepoint in");
        try
        {
            uint port;
            var read = (nint)(delegate* unmanaged<MidiPacketList*, nint, nint, void>)&OnRead;
            if (MIDIInputPortCreate(_client, name, read, GCHandle.ToIntPtr(input.Self), &port) != 0)
            {
                input.Self.Free();
                return null;
            }
            input.Port = port;
        }
        finally
        {
            CFRelease(name);
        }
        if (MIDIPortConnectSource(input.Port, source, 0) != 0)
        {
            input.Dispose();
            return null;
        }
        return input;
    }

    public IMidiOutput? OpenOutput(int index)
    {
        if (_client == 0 || index < 0 || index >= (int)MIDIGetNumberOfDestinations())
            return null;
        var destination = MIDIGetDestination((nuint)index);
        if (destination == 0)
            return null;
        var name = CFString("Cuepoint out");
        try
        {
            uint port;
            return MIDIOutputPortCreate(_client, name, &port) == 0 ? new Output(port, destination) : null;
        }
        finally
        {
            CFRelease(name);
        }
    }

    // A MIDIPacketList, packed to four bytes as CoreMIDI lays it out: the count, then each packet's time, length and
    // bytes, the next starting at the four-byte boundary after.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct MidiPacketList
    {
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct MidiPacket
    {
        public ulong TimeStamp;
        public ushort Length;
    }

    const int PACKET_HEADER = 10;

    // Called on CoreMIDI's own thread; it only reads the bytes and hands each message on.
    [UnmanagedCallersOnly]
    static void OnRead(MidiPacketList* list, nint instance, nint connection)
    {
        if (instance == 0)
            return;
        try
        {
            if (GCHandle.FromIntPtr(instance).Target is not Input input)
                return;
            var packet = (byte*)list + sizeof(uint);
            for (var i = 0; i < list->Count; i++)
            {
                var length = ((MidiPacket*)packet)->Length;
                input.Read(new ReadOnlySpan<byte>(packet + PACKET_HEADER, length));
                packet = (byte*)(((nint)(packet + PACKET_HEADER + length) + 3) & ~3);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
    }

    // Pinned by a handle for as long as it's open, so the callback can find it from its reference.
    sealed class Input : IDisposable
    {
        readonly Action<MidiMessage> _received;
        readonly MidiBytes _bytes = new();
        public readonly GCHandle Self;
        public uint Port;

        public Input(Action<MidiMessage> received)
        {
            _received = received;
            Self = GCHandle.Alloc(this);
        }

        public void Read(ReadOnlySpan<byte> bytes) => _bytes.Read(bytes, _received);

        public void Dispose()
        {
            if (Port == 0)
                return;
            // Disposing the port disconnects it and stops the callbacks before the handle's let go.
            MIDIPortDispose(Port);
            Port = 0;
            Self.Free();
        }
    }

    sealed class Output(uint port, uint destination) : IMidiOutput
    {
        uint _port = port;

        public void Send(MidiMessage message)
        {
            if (_port == 0)
                return;
            // One packet, of one message, to go now (time 0).
            var buffer = stackalloc byte[sizeof(uint) + PACKET_HEADER + 3 + 3];
            ((MidiPacketList*)buffer)->Count = 1;
            var packet = (MidiPacket*)(buffer + sizeof(uint));
            packet->TimeStamp = 0;
            packet->Length = (ushort)MidiBytes.Write(message, new Span<byte>(buffer + sizeof(uint) + PACKET_HEADER, 3));
            MIDISend(_port, destination, (MidiPacketList*)buffer);
        }

        public void Dispose()
        {
            if (_port == 0)
                return;
            MIDIPortDispose(_port);
            _port = 0;
        }
    }

    // What CoreMIDI calls an endpoint, as people see it: its display name, which includes its device's.
    static string NameOf(uint endpoint)
    {
        if (endpoint == 0)
            return "";
        var property = CFString("displayName"); // kMIDIPropertyDisplayName
        try
        {
            nint name;
            if (MIDIObjectGetStringProperty(endpoint, property, &name) != 0 || name == 0)
                return "";
            try
            {
                return FromCFString(name);
            }
            finally
            {
                CFRelease(name);
            }
        }
        finally
        {
            CFRelease(property);
        }
    }

    static nint CFString(string text) => CFStringCreateWithCString(0, text, kCFStringEncodingUTF8);

    static string FromCFString(nint text)
    {
        var length = CFStringGetLength(text);
        var size = (int)CFStringGetMaximumSizeForEncoding(length, kCFStringEncodingUTF8) + 1;
        var buffer = new byte[size];
        fixed (byte* bytes = buffer)
        {
            if (!CFStringGetCString(text, bytes, size, kCFStringEncodingUTF8))
                return "";
            return Marshal.PtrToStringUTF8((nint)bytes) ?? "";
        }
    }

    [LibraryImport(CORE_MIDI)]
    private static partial int MIDIClientCreate(nint name, nint notify, nint notifyReference, uint* client);

    [LibraryImport(CORE_MIDI)]
    private static partial nuint MIDIGetNumberOfSources();

    [LibraryImport(CORE_MIDI)]
    private static partial uint MIDIGetSource(nuint index);

    [LibraryImport(CORE_MIDI)]
    private static partial nuint MIDIGetNumberOfDestinations();

    [LibraryImport(CORE_MIDI)]
    private static partial uint MIDIGetDestination(nuint index);

    [LibraryImport(CORE_MIDI)]
    private static partial int MIDIObjectGetStringProperty(uint midiObject, nint property, nint* value);

    [LibraryImport(CORE_MIDI)]
    private static partial int MIDIInputPortCreate(uint client, nint name, nint read, nint reference, uint* port);

    [LibraryImport(CORE_MIDI)]
    private static partial int MIDIOutputPortCreate(uint client, nint name, uint* port);

    [LibraryImport(CORE_MIDI)]
    private static partial int MIDIPortConnectSource(uint port, uint source, nint connectionReference);

    [LibraryImport(CORE_MIDI)]
    private static partial int MIDIPortDispose(uint port);

    [LibraryImport(CORE_MIDI)]
    private static partial int MIDISend(uint port, uint destination, MidiPacketList* packets);

    [LibraryImport(CORE_FOUNDATION, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [LibraryImport(CORE_FOUNDATION)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial void CFRelease(nint item);
}
