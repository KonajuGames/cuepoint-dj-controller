namespace Cuepoint.Audio;

/// <summary>
/// The mix made ahead of the device, so a moment where the computer is busy doesn't leave the device with nothing to
/// play. One thread writes and one reads, at the same time, with no lock: the writer only moves where it's written up
/// to, and the reader only where it's read up to.
/// </summary>
sealed class MixAheadBuffer
{
    readonly float[] _samples;

    // How many samples have ever been written and read; the difference is what's waiting. Each is moved by one side.
    long _written;
    long _read;

    public MixAheadBuffer(int capacity) => _samples = new float[capacity];

    public int Capacity => _samples.Length;

    /// <summary>
    /// How many samples are waiting to be read.
    /// </summary>
    public int Available => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>
    /// How many samples there's room for.
    /// </summary>
    public int Free => Capacity - Available;

    /// <summary>
    /// Adds as much of <paramref name="samples"/> as there's room for, and returns how many that was. Writer only.
    /// </summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        var written = _written;
        var count = Math.Min(samples.Length, Capacity - (int)(written - Volatile.Read(ref _read)));
        var start = (int)(written % Capacity);
        var first = Math.Min(count, Capacity - start);
        samples[..first].CopyTo(_samples.AsSpan(start));
        samples[first..count].CopyTo(_samples);
        // Published only once the samples are in, so the reader never sees a place before its sound.
        Volatile.Write(ref _written, written + count);
        return count;
    }

    /// <summary>
    /// Takes up to <paramref name="output"/>'s length of what's waiting, oldest first, and returns how many it took.
    /// Reader only.
    /// </summary>
    public int Read(Span<float> output)
    {
        var read = _read;
        var count = Math.Min(output.Length, (int)(Volatile.Read(ref _written) - read));
        var start = (int)(read % Capacity);
        var first = Math.Min(count, Capacity - start);
        _samples.AsSpan(start, first).CopyTo(output);
        _samples.AsSpan(0, count - first).CopyTo(output[first..]);
        Volatile.Write(ref _read, read + count);
        return count;
    }
}
