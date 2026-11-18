using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.SdlApi;

namespace Cuepoint.Audio;

/// <summary>
/// The playback device the main mix goes out of. SDL asks for audio as the device needs it, on its own thread, and
/// this pulls that much from the mixer. Normally the mix goes out as it is, in stereo; with
/// <see cref="SplitHeadphones"/> it goes out in four channels, the main mix on the first two and the headphone mix
/// from the same mixing pass on the other two, as a DJ controller's own sound card wants them (master out on 1-2,
/// headphones on 3-4). SDL converts to the device's own rate and layout.
/// </summary>
/// <remarks>
/// A bigger buffer than the device opened with is asked for (as Windows' shared-mode driver keeps its own, whatever's
/// asked), the difference is mixed ahead: a thread of its own keeps that much of the mix waiting in a
/// <see cref="MixAheadBuffer"/>, and the device's thread only copies from it, so a moment where the computer is busy
/// doesn't leave the device with nothing. Otherwise the device's thread mixes as it asks, with nothing waiting.
/// </remarks>
sealed unsafe class AudioOutput : IDisposable
{
    /// <summary>
    /// The mix's format: 48 kHz stereo floats, whatever the device's, so the decks never have to change.
    /// </summary>
    public const int RATE = 48000;

    public const int CHANNELS = 2;

    readonly AudioEngine _engine;
    readonly SdlAudioStream _stream;
    GCHandle _self;

    // Where the mix is made: the device's thread's, or the mixing thread's when mixing ahead; never both.
    float[] _mix = [];
    float[] _out = [];

    // Mixing ahead: what's waiting, the thread keeping it topped up, and the device's thread's own copy of it.
    readonly MixAheadBuffer? _ahead;
    readonly Thread? _mixer;
    readonly AutoResetEvent _taken = new(false);
    readonly ManualResetEventSlim _primed = new(false);
    volatile bool _stopping;
    float[] _read = [];
    int _shortfalls;

    /// <summary>
    /// How many times, mixing ahead, the device has asked for more than was waiting, and got silence for the rest: a
    /// crackle the mix ahead didn't stop.
    /// </summary>
    internal int Shortfalls => Volatile.Read(ref _shortfalls);

    /// <summary>
    /// How long the device can go without asking for audio before it counts as stopped: far longer than any device
    /// waits between asking, but soon enough to get the music going again elsewhere.
    /// </summary>
    static readonly TimeSpan StallTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The longest the device waits, on opening, for the mix ahead to be ready before it starts anyway.
    /// </summary>
    static readonly TimeSpan PrimeTime = TimeSpan.FromMilliseconds(250);

    // When the device last asked for audio, from Stopwatch; from when it was opened, until it first does.
    long _lastFill = Stopwatch.GetTimestamp();

    /// <summary>
    /// Whether the device has stopped asking for audio, as one unplugged or whose driver has failed does. The decks
    /// only move on as it asks, so they've stopped too.
    /// </summary>
    public bool IsStalled => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFill)) > StallTime;

    /// <summary>
    /// The device's name, or null for the system's default.
    /// </summary>
    public string? DeviceName { get; }

    /// <summary>
    /// How many channels the device has; four or more can carry the headphones too.
    /// </summary>
    public int DeviceChannels { get; }

    /// <summary>
    /// Where the headphone mix goes onto channels 3 and 4, if it does; null if the device carries only the main mix.
    /// </summary>
    public ChannelHeadphones? SplitHeadphones { get; }

    /// <summary>
    /// The buffer the device actually opened with: how many sample frames it asks for at a time, at its own rate.
    /// Null if SDL can't say. It may not be the size asked for, as some drivers keep their own.
    /// </summary>
    public (int Frames, int Rate)? Buffer { get; }

    /// <summary>
    /// How many frames of the mix (at <see cref="RATE"/>) are kept waiting ahead of the device, to make up a buffer
    /// bigger than it opened with; 0 if none are.
    /// </summary>
    public int MixAheadFrames { get; }

    /// <summary>
    /// The SDL hint for how many sample frames a device asks for at a time, read as a device is opened.
    /// </summary>
    const string SAMPLE_FRAMES_HINT = "SDL_AUDIO_DEVICE_SAMPLE_FRAMES";

    /// <summary>
    /// Asks devices opened from now on for buffers of <paramref name="frames"/> sample frames, or the driver's own
    /// size with null. Only a device opened afresh takes it.
    /// </summary>
    public static void RequestBufferFrames(int? frames)
    {
        if (frames is { } n)
            Sdl.PInvoke.SetHint(SAMPLE_FRAMES_HINT, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else
            Sdl.PInvoke.ResetHint(SAMPLE_FRAMES_HINT);
    }

    /// <summary>
    /// How long a buffer of <paramref name="frames"/> lasts at <paramref name="rate"/>, in milliseconds.
    /// </summary>
    public static double Milliseconds(int frames, int rate) => rate > 0 ? frames * 1000.0 / rate : 0;

    /// <summary>
    /// How many frames of the mix to keep waiting so the device's buffer and they come to
    /// <paramref name="requestedFrames"/> (at <see cref="RATE"/>); 0 for no request, or a device that opened with as
    /// much or more.
    /// </summary>
    internal static int MixAheadFor(int? requestedFrames, (int Frames, int Rate)? opened) =>
        requestedFrames is { } requested && opened is var (frames, rate) && rate > 0
            ? Math.Max(0, requested - (int)Math.Round((double)frames * RATE / rate))
            : 0;

    /// <summary>
    /// Opens <paramref name="deviceId"/> for the main mix, with the headphones on its channels 3 and 4 if
    /// <paramref name="splitHeadphones"/> is given, aiming for a buffer of <paramref name="bufferFrames"/> (the
    /// device's own with null), mixing ahead whatever the device falls short of it by. Throws
    /// <see cref="SdlException"/> if it can't be opened.
    /// </summary>
    public AudioOutput(AudioEngine engine, uint deviceId, string? deviceName, ChannelHeadphones? splitHeadphones, int? bufferFrames = null)
    {
        _engine = engine;
        DeviceName = deviceName;
        SplitHeadphones = splitHeadphones;

        SdlAudioSpec device;
        int frames;
        DeviceChannels = Sdl.PInvoke.GetAudioDeviceFormat(deviceId, &device, &frames) != 0 ? device.channels : CHANNELS;

        var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = OutChannels, freq = RATE };
        _self = GCHandle.Alloc(this);
        _stream = Sdl.PInvoke.OpenAudioDeviceStream(
            deviceId,
            &spec,
            (delegate* unmanaged[Cdecl]<void*, SdlAudioStream, int, int, void>)&OnFill,
            (void*)GCHandle.ToIntPtr(_self)
        );
        if (_stream.IsNull())
        {
            _self.Free();
            throw new SdlException($"Couldn't open {deviceName ?? "the default output"}: {Sdl.PInvoke.GetError()}");
        }

        // What it opened with, as opposed to what was asked for. It's still paused, so nothing's been asked for yet.
        SdlAudioSpec opened;
        int openedFrames;
        if (Sdl.PInvoke.GetAudioDeviceFormat(Sdl.PInvoke.GetAudioStreamDevice(_stream), &opened, &openedFrames) != 0 && openedFrames > 0)
            Buffer = (openedFrames, opened.freq);

        MixAheadFrames = MixAheadFor(bufferFrames, Buffer);
        if (MixAheadFrames > 0)
        {
            // Made a device's buffer at a time; room for what's kept waiting, what the device takes at once, and one
            // more made as it does.
            var chunk = ChunkFrames;
            _ahead = new MixAheadBuffer((MixAheadFrames + 3 * chunk) * OutChannels);
            _mixer = new Thread(MixAhead) { Name = "Cuepoint mix ahead", IsBackground = true, Priority = ThreadPriority.Highest };
            _mixer.Start();
            _primed.Wait(PrimeTime);
        }
        Sdl.PInvoke.ResumeAudioStreamDevice(_stream);
    }

    int OutChannels => SplitHeadphones is null ? CHANNELS : 4;

    /// <summary>
    /// The device's buffer in frames of the mix: how much it takes at a time, and how much is mixed at once ahead.
    /// </summary>
    int ChunkFrames => Buffer is var (frames, rate) && rate > 0 ? Math.Max(64, (int)Math.Ceiling((double)frames * RATE / rate)) : 512;

    /// <summary>
    /// How many channels a device has, as SDL reports it; 2 if it can't say.
    /// </summary>
    public static int ChannelsOf(uint deviceId)
    {
        SdlAudioSpec device;
        int frames;
        return Sdl.PInvoke.GetAudioDeviceFormat(deviceId, &device, &frames) != 0 ? device.channels : CHANNELS;
    }

    // SDL wants more audio: at least `needed` bytes, in the stream's format. On the device's audio thread.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void OnFill(void* userData, SdlAudioStream stream, int needed, int total)
    {
        try
        {
            if (needed > 0 && GCHandle.FromIntPtr((nint)userData).Target is AudioOutput output)
                output.Fill(stream, needed);
        }
        catch
        {
            // Nothing can be done about it on the audio thread, and an exception mustn't escape into SDL.
        }
    }

    void Fill(SdlAudioStream stream, int needed)
    {
        Volatile.Write(ref _lastFill, Stopwatch.GetTimestamp());
        var outChannels = OutChannels;
        var frames = (needed + outChannels * sizeof(float) - 1) / (outChannels * sizeof(float));

        if (_ahead is { } ahead)
        {
            // What's waiting; silence for any it's short of, which only a computer too busy even for the mix ahead
            // leaves.
            var samples = frames * outChannels;
            if (_read.Length < samples)
                _read = new float[samples];
            var read = ahead.Read(_read.AsSpan(0, samples));
            if (read < samples)
                Interlocked.Increment(ref _shortfalls);
            _read.AsSpan(read, samples - read).Clear();
            _taken.Set();
            fixed (float* output = _read)
                Sdl.PInvoke.PutAudioStreamData(stream, output, samples * sizeof(float));
            return;
        }

        var made = Mix(frames);
        fixed (float* output = made)
            Sdl.PInvoke.PutAudioStreamData(stream, output, frames * outChannels * sizeof(float));
    }

    /// <summary>
    /// Mixes the next <paramref name="frames"/> in the device's layout, into a buffer that's good until the next call.
    /// </summary>
    float[] Mix(int frames)
    {
        var samples = frames * CHANNELS;
        if (_mix.Length < samples)
            _mix = new float[samples];

        // The mix, which also works out the headphones' share of this same stretch.
        fixed (float* mix = _mix)
            _engine.Generate(mix, samples);

        if (SplitHeadphones is not { } phones)
            return _mix;

        if (_out.Length < frames * 4)
            _out = new float[frames * 4];
        Interleave(_mix.AsSpan(0, samples), phones.Take(samples), _out.AsSpan(0, frames * 4));
        return _out;
    }

    /// <summary>
    /// The mixing thread, when mixing ahead: keeps the device's buffer and <see cref="MixAheadFrames"/> more waiting,
    /// a device's buffer at a time, and waits for the device to take some when there's that much.
    /// </summary>
    void MixAhead()
    {
        var ahead = _ahead!;
        var chunk = ChunkFrames;
        var samples = chunk * OutChannels;
        var target = (MixAheadFrames + chunk) * OutChannels;
        while (!_stopping)
        {
            try
            {
                while (!_stopping && ahead.Available < target && ahead.Free >= samples)
                    ahead.Write(Mix(chunk).AsSpan(0, samples));
            }
            catch
            {
                // As on the device's thread: nothing to be done but carry on with the next.
            }
            _primed.Set();
            // Woken as the device takes some; the timeout only in case it never does, to see whether it's stopping.
            _taken.WaitOne(TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>
    /// Four channels from two stereo pairs: <paramref name="main"/> on 1 and 2, <paramref name="phones"/> on 3 and 4
    /// (silence where it falls short).
    /// </summary>
    internal static void Interleave(ReadOnlySpan<float> main, ReadOnlySpan<float> phones, Span<float> output)
    {
        var frames = output.Length / 4;
        for (var f = 0; f < frames; f++)
        {
            var i = f * 2;
            output[f * 4] = i < main.Length ? main[i] : 0;
            output[f * 4 + 1] = i + 1 < main.Length ? main[i + 1] : 0;
            output[f * 4 + 2] = i < phones.Length ? phones[i] : 0;
            output[f * 4 + 3] = i + 1 < phones.Length ? phones[i + 1] : 0;
        }
    }

    public void Dispose()
    {
        // Destroying the stream waits for a callback in progress, so nothing's using this after.
        Sdl.PInvoke.DestroyAudioStream(_stream);
        // Then the mixing thread, so nothing's pulling from the mixer once this is gone.
        if (_mixer is not null)
        {
            _stopping = true;
            _taken.Set();
            _mixer.Join();
        }
        _taken.Dispose();
        _primed.Dispose();
        if (_self.IsAllocated)
            _self.Free();
    }
}

/// <summary>
/// The headphone mix gathered as each mixing pass makes it, for <see cref="AudioOutput"/> to put on the main
/// device's channels 3 and 4 alongside the main mix from the same pass.
/// </summary>
sealed unsafe class ChannelHeadphones : IHeadphoneOutput
{
    float[] _buffer = new float[4096];
    int _filled;

    public void Write(float* samples, int count)
    {
        if (_buffer.Length < _filled + count)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _filled + count));
        new ReadOnlySpan<float>(samples, count).CopyTo(_buffer.AsSpan(_filled));
        _filled += count;
    }

    /// <summary>
    /// What's been gathered, up to <paramref name="count"/> samples, then forgets it all, ready for the next pass.
    /// Only valid until the next <see cref="Write"/>.
    /// </summary>
    public ReadOnlySpan<float> Take(int count)
    {
        var taken = _buffer.AsSpan(0, Math.Min(count, _filled));
        _filled = 0;
        return taken;
    }

    public void Dispose() { }
}
