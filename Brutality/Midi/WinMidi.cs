using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Brutality.Midi;

/// <summary>
/// The system's MIDI inputs and outputs. <see cref="MidiDevices"/> goes through this, so tests can stand in their
/// own.
/// </summary>
interface IMidiPorts
{
    /// <summary>
    /// The names of the inputs and outputs connected now, in the system's order.
    /// </summary>
    IReadOnlyList<string> InputNames();

    IReadOnlyList<string> OutputNames();

    /// <summary>
    /// Opens input <paramref name="index"/>, whose messages go to <paramref name="received"/> on whatever thread the
    /// system calls back on; null if it can't be opened.
    /// </summary>
    IDisposable? OpenInput(int index, Action<MidiMessage> received);

    /// <summary>
    /// Opens output <paramref name="index"/>; null if it can't be opened.
    /// </summary>
    IMidiOutput? OpenOutput(int index);
}

interface IMidiOutput : IDisposable
{
    void Send(MidiMessage message);
}

/// <summary>
/// MIDI through Windows' own multimedia API (winmm), with nothing to install.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe partial class WinMidi : IMidiPorts
{
    const uint CALLBACK_NULL = 0;
    const uint CALLBACK_FUNCTION = 0x30000;
    const uint MIM_DATA = 0x3C3;
    const int NAME_LENGTH = 32;

    [StructLayout(LayoutKind.Sequential)]
    struct InCaps
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint DriverVersion;
        public fixed char Name[NAME_LENGTH];
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OutCaps
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint DriverVersion;
        public fixed char Name[NAME_LENGTH];
        public ushort Technology;
        public ushort Voices;
        public ushort Notes;
        public ushort ChannelMask;
        public uint Support;
    }

    public IReadOnlyList<string> InputNames()
    {
        var names = new List<string>();
        var count = midiInGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            InCaps caps;
            names.Add(midiInGetDevCapsW(i, &caps, (uint)sizeof(InCaps)) == 0 ? new string(caps.Name) : "");
        }
        return names;
    }

    public IReadOnlyList<string> OutputNames()
    {
        var names = new List<string>();
        var count = midiOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            OutCaps caps;
            names.Add(midiOutGetDevCapsW(i, &caps, (uint)sizeof(OutCaps)) == 0 ? new string(caps.Name) : "");
        }
        return names;
    }

    public IDisposable? OpenInput(int index, Action<MidiMessage> received)
    {
        var input = new Input(received);
        nint handle;
        if (midiInOpen(&handle, (uint)index, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, void>)&OnInput, GCHandle.ToIntPtr(input.Self), CALLBACK_FUNCTION) != 0)
        {
            input.Self.Free();
            return null;
        }
        input.Handle = handle;
        midiInStart(handle);
        return input;
    }

    public IMidiOutput? OpenOutput(int index)
    {
        nint handle;
        return midiOutOpen(&handle, (uint)index, 0, 0, CALLBACK_NULL) == 0 ? new Output(handle) : null;
    }

    // Called on a system thread, which mustn't call back into winmm; it only hands the message on.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void OnInput(nint handle, uint message, nint instance, nint param1, nint param2)
    {
        if (message != MIM_DATA || instance == 0)
            return;
        try
        {
            if (GCHandle.FromIntPtr(instance).Target is Input input)
                input.Received(MidiMessage.FromPacked((uint)param1));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
    }

    // Pinned by a handle for as long as it's open, so the callback can find it from its instance pointer.
    sealed class Input : IDisposable
    {
        public readonly Action<MidiMessage> Received;
        public readonly GCHandle Self;
        public nint Handle;

        public Input(Action<MidiMessage> received)
        {
            Received = received;
            Self = GCHandle.Alloc(this);
        }

        public void Dispose()
        {
            if (Handle == 0)
                return;
            midiInStop(Handle);
            midiInReset(Handle);
            midiInClose(Handle);
            Handle = 0;
            Self.Free();
        }
    }

    sealed class Output(nint handle) : IMidiOutput
    {
        nint _handle = handle;

        public void Send(MidiMessage message)
        {
            if (_handle != 0)
                midiOutShortMsg(_handle, message.Packed);
        }

        public void Dispose()
        {
            if (_handle == 0)
                return;
            midiOutReset(_handle);
            midiOutClose(_handle);
            _handle = 0;
        }
    }

    [LibraryImport("winmm.dll")]
    private static partial uint midiInGetNumDevs();

    [LibraryImport("winmm.dll")]
    private static partial uint midiOutGetNumDevs();

    [LibraryImport("winmm.dll")]
    private static partial uint midiInGetDevCapsW(nuint id, InCaps* caps, uint size);

    [LibraryImport("winmm.dll")]
    private static partial uint midiOutGetDevCapsW(nuint id, OutCaps* caps, uint size);

    [LibraryImport("winmm.dll")]
    private static partial uint midiInOpen(nint* handle, uint id, nint callback, nint instance, uint flags);

    [LibraryImport("winmm.dll")]
    private static partial uint midiInStart(nint handle);

    [LibraryImport("winmm.dll")]
    private static partial uint midiInStop(nint handle);

    [LibraryImport("winmm.dll")]
    private static partial uint midiInReset(nint handle);

    [LibraryImport("winmm.dll")]
    private static partial uint midiInClose(nint handle);

    [LibraryImport("winmm.dll")]
    private static partial uint midiOutOpen(nint* handle, uint id, nint callback, nint instance, uint flags);

    [LibraryImport("winmm.dll")]
    private static partial uint midiOutShortMsg(nint handle, uint message);

    [LibraryImport("winmm.dll")]
    private static partial uint midiOutReset(nint handle);

    [LibraryImport("winmm.dll")]
    private static partial uint midiOutClose(nint handle);
}
