using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace Brutality.Audio;

/// <summary>
/// Records the main mix, as the room hears it, to a WAV file: 24-bit stereo at the mixer's 48 kHz. The audio thread
/// only copies each buffer into a queue (see <see cref="Write"/>); a thread of its own turns them into samples and
/// writes them, so a slow disk can never hold up the audio. A recording that would pass a WAV file's 4 GB limit,
/// about four hours in, carries on in a second file, then a third, named after the first.
/// </summary>
sealed unsafe class MixRecorder : IDisposable
{
    const int BYTES_PER_SAMPLE = 3;
    const int CHANNELS = AudioOutput.CHANNELS;
    const int RATE = AudioOutput.RATE;
    const int FRAME_BYTES = BYTES_PER_SAMPLE * CHANNELS;

    /// <summary>
    /// How much sound a file takes before the recording moves on to the next, short of the 4 GB a WAV file's sizes
    /// can say.
    /// </summary>
    const long MAX_DATA_BYTES = 4_000_000_000 / FRAME_BYTES * FRAME_BYTES;

    readonly ConcurrentQueue<(float[] Samples, int Count)> _queue = new();
    readonly SemaphoreSlim _queued = new(0);
    readonly Thread _writer;
    volatile bool _stopping;
    long _frames;

    readonly long _maxDataBytes;
    FileStream? _file;
    long _dataBytes;
    readonly List<string> _paths = [];

    /// <summary>
    /// The file the recording started in; any it went on into are named after it, " (2)" and on.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Every file written so far, in order.
    /// </summary>
    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_paths)
                return [.. _paths];
        }
    }

    /// <summary>
    /// How long the recording is so far, from what's come off the mixer.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Interlocked.Read(ref _frames) / RATE);

    /// <summary>
    /// Why it stopped writing, if it couldn't carry on (a full disk, say); what's been written is kept.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Starts a recording into <paramref name="path"/>, making its folder if need be. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the file can't be made.
    /// </summary>
    public MixRecorder(string path)
        : this(path, MAX_DATA_BYTES) { }

    /// <summary>
    /// A recording that moves on to its next file every <paramref name="maxDataBytes"/> (whole frames), for tests.
    /// </summary>
    internal MixRecorder(string path, long maxDataBytes)
    {
        _maxDataBytes = maxDataBytes / FRAME_BYTES * FRAME_BYTES;
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        Open(Path);
        _writer = new Thread(Work) { Name = "Mix recorder", IsBackground = true };
        _writer.Start();
    }

    /// <summary>
    /// Queues a buffer of the mix, <paramref name="count"/> interleaved stereo samples, for writing. Called on the
    /// audio thread, so it only copies.
    /// </summary>
    internal void Write(float* samples, int count)
    {
        if (_stopping || count <= 0)
            return;
        var copy = ArrayPool<float>.Shared.Rent(count);
        new ReadOnlySpan<float>(samples, count).CopyTo(copy);
        _queue.Enqueue((copy, count));
        Interlocked.Add(ref _frames, count / CHANNELS);
        _queued.Release();
    }

    void Work()
    {
        var bytes = Array.Empty<byte>();
        while (true)
        {
            // Woken by each buffer, or every so often to see if it's time to stop.
            _queued.Wait(100);
            while (_queue.TryDequeue(out var buffer))
            {
                try
                {
                    if (Error is null)
                    {
                        var needed = buffer.Count / CHANNELS * FRAME_BYTES;
                        if (bytes.Length < needed)
                            bytes = new byte[needed];
                        Convert(buffer.Samples.AsSpan(0, buffer.Count), bytes);
                        WriteData(bytes.AsSpan(0, needed));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Error = e.Message;
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(buffer.Samples);
                }
            }
            if (_stopping && _queue.IsEmpty)
                return;
        }
    }

    /// <summary>
    /// Floats from -1 to 1 into little-endian 24-bit samples, clipped at full scale as the speakers would.
    /// </summary>
    static void Convert(ReadOnlySpan<float> samples, Span<byte> bytes)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (int)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607f);
            bytes[3 * i] = (byte)value;
            bytes[3 * i + 1] = (byte)(value >> 8);
            bytes[3 * i + 2] = (byte)(value >> 16);
        }
    }

    void WriteData(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_dataBytes >= _maxDataBytes)
            {
                Close();
                Open(NextPath());
            }
            var room = (int)Math.Min(data.Length, _maxDataBytes - _dataBytes);
            _file!.Write(data[..room]);
            _dataBytes += room;
            data = data[room..];
        }
    }

    string NextPath()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(Path);
        lock (_paths)
            return System.IO.Path.Combine(folder, $"{name} ({_paths.Count + 1}).wav");
    }

    void Open(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _dataBytes = 0;
        // The sizes are filled in on closing; until then they say nothing's there.
        WriteHeader(_file, 0);
        lock (_paths)
            _paths.Add(path);
    }

    void Close()
    {
        if (_file is not { } file)
            return;
        _file = null;
        try
        {
            file.Position = 0;
            WriteHeader(file, _dataBytes);
        }
        finally
        {
            file.Dispose();
        }
    }

    /// <summary>
    /// A canonical 44-byte WAV header for <paramref name="dataBytes"/> of 24-bit stereo PCM.
    /// </summary>
    static void WriteHeader(Stream stream, long dataBytes)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1); // PCM
        writer.Write((ushort)CHANNELS);
        writer.Write((uint)RATE);
        writer.Write((uint)(RATE * FRAME_BYTES));
        writer.Write((ushort)FRAME_BYTES);
        writer.Write((ushort)(8 * BYTES_PER_SAMPLE));
        writer.Write("data"u8);
        writer.Write((uint)dataBytes);
    }

    /// <summary>
    /// Stops taking the mix, writes out everything queued, and finishes the file. Safe to call more than once.
    /// </summary>
    public void Stop()
    {
        if (_stopping && _file is null)
            return;
        _stopping = true;
        _queued.Release();
        _writer.Join();
        try
        {
            Close();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error ??= e.Message;
        }
    }

    public void Dispose()
    {
        Stop();
        _queued.Dispose();
    }
}
