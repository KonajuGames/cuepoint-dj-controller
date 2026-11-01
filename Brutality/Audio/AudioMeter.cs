using System.Runtime.InteropServices;
using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace Brutality.Audio;

/// <summary>
/// The peak level of audio as it plays, per side, for a level meter: SDL_mixer's audio thread reports each buffer
/// with <see cref="Measure"/>, and the UI takes the highest peak since its last look with <see cref="TakePeaks"/>.
/// A lost update between the two only means one buffer's peak goes unseen, which a meter can't show anyway.
/// </summary>
sealed unsafe class AudioMeter : IDisposable
{
    float _left;
    float _right;
    GCHandle _handle;

    /// <summary>
    /// What to give SDL_mixer as the callback's user data, so the callback can find this meter.
    /// </summary>
    internal void* UserData
    {
        get
        {
            if (!_handle.IsAllocated)
                _handle = GCHandle.Alloc(this);
            return (void*)GCHandle.ToIntPtr(_handle);
        }
    }

    /// <summary>
    /// Folds a buffer of interleaved samples into the peaks. Mono counts for both sides.
    /// </summary>
    internal void Measure(float* samples, int count, int channels)
    {
        float left = 0, right = 0;
        if (channels <= 1)
        {
            for (var i = 0; i < count; i++)
                left = MathF.Max(left, MathF.Abs(samples[i]));
            right = left;
        }
        else
        {
            for (var i = 0; i + channels <= count; i += channels)
            {
                left = MathF.Max(left, MathF.Abs(samples[i]));
                right = MathF.Max(right, MathF.Abs(samples[i + 1]));
            }
        }
        Volatile.Write(ref _left, MathF.Max(Volatile.Read(ref _left), left));
        Volatile.Write(ref _right, MathF.Max(Volatile.Read(ref _right), right));
    }

    /// <summary>
    /// The highest peak on each side since the last call, where 1 is full scale, and starts again from silence.
    /// </summary>
    public (float Left, float Right) TakePeaks() => (Interlocked.Exchange(ref _left, 0), Interlocked.Exchange(ref _right, 0));

    static AudioMeter FromUserData(void* userData) => (AudioMeter)GCHandle.FromIntPtr((nint)userData).Target!;

    /// <summary>
    /// SDL_mixer's callback for the final mix, on its audio thread.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static void MeasureMix(void* userData, MixMixer mixer, SdlAudioSpec* spec, float* samples, int count)
    {
        try
        {
            FromUserData(userData).Measure(samples, count, spec->channels);
        }
        catch
        {
            // Nothing can be done about it on the audio thread, and an exception mustn't escape into SDL.
        }
    }

    /// <summary>
    /// Call only once nothing can call back with this meter any more.
    /// </summary>
    public void Dispose()
    {
        if (_handle.IsAllocated)
            _handle.Free();
    }
}
