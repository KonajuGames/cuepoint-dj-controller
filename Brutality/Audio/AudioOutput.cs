using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.SdlApi;

namespace Brutality.Audio;

/// <summary>
/// The playback device the main mix goes out of. SDL asks for audio as the device needs it, on its own thread, and
/// this pulls that much from the mixer. Normally the mix goes out as it is, in stereo; with
/// <see cref="SplitHeadphones"/> it goes out in four channels, the main mix on the first two and the headphone mix
/// from the same mixing pass on the other two, as a DJ controller's own sound card wants them (master out on 1-2,
/// headphones on 3-4). SDL converts to the device's own rate and layout.
/// </summary>
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
    float[] _mix = [];
    float[] _out = [];

    /// <summary>
    /// How long the device can go without asking for audio before it counts as stopped: far longer than any device
    /// waits between asking, but soon enough to get the music going again elsewhere.
    /// </summary>
    static readonly TimeSpan StallTime = TimeSpan.FromSeconds(1.5);

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
    /// Opens <paramref name="deviceId"/> for the main mix, with the headphones on its channels 3 and 4 if
    /// <paramref name="splitHeadphones"/> is given. Throws <see cref="SdlException"/> if it can't be opened.
    /// </summary>
    public AudioOutput(AudioEngine engine, uint deviceId, string? deviceName, ChannelHeadphones? splitHeadphones)
    {
        _engine = engine;
        DeviceName = deviceName;
        SplitHeadphones = splitHeadphones;

        SdlAudioSpec device;
        int frames;
        DeviceChannels = Sdl.PInvoke.GetAudioDeviceFormat(deviceId, &device, &frames) != 0 ? device.channels : CHANNELS;

        var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = splitHeadphones is null ? CHANNELS : 4, freq = RATE };
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
        Sdl.PInvoke.ResumeAudioStreamDevice(_stream);
    }

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
        var outChannels = SplitHeadphones is null ? CHANNELS : 4;
        var frames = (needed + outChannels * sizeof(float) - 1) / (outChannels * sizeof(float));
        var samples = frames * CHANNELS;
        if (_mix.Length < samples)
            _mix = new float[samples];

        // The mix, which also works out the headphones' share of this same stretch.
        fixed (float* mix = _mix)
            _engine.Generate(mix, samples);

        if (SplitHeadphones is not { } phones)
        {
            fixed (float* mix = _mix)
                Sdl.PInvoke.PutAudioStreamData(stream, mix, samples * sizeof(float));
            return;
        }

        if (_out.Length < frames * 4)
            _out = new float[frames * 4];
        Interleave(_mix.AsSpan(0, samples), phones.Take(samples), _out.AsSpan(0, frames * 4));
        fixed (float* outSamples = _out)
            Sdl.PInvoke.PutAudioStreamData(stream, outSamples, frames * 4 * sizeof(float));
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
