using System.Security.Cryptography;

namespace Cuepoint.Audio.Stems;

/// <summary>
/// Downloads the stem model (tools/export_demucs.py's ONNX export of htdemucs, 174 MB) the first time it's wanted,
/// from the app's GitHub release, rather than shipping it with every copy. It's written beside where it goes and
/// moved there only once its SHA-256 is right, so a download cut short or tampered with is never used.
/// </summary>
sealed class StemModelDownload : IDisposable
{
    /// <summary>
    /// Where the model's published: a release of its own, so it isn't downloaded again with each new version of the
    /// app. A new model goes in a new release, with its hash and size here.
    /// </summary>
    public static readonly Uri MODEL_URL = new("https://github.com/KonajuGames/cuepoint-dj-controller/releases/download/htdemucs-1/htdemucs.onnx");

    public const string MODEL_SHA256 = "fd21cb93a6f7b963b6d264e44f91137a5c66fc92d466a9b332bbedc2eacff21b";

    public const long MODEL_SIZE = 174_263_735;

    // How long the download can go without a byte before it gives up.
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    readonly string _path;
    readonly Uri _url;
    readonly string _sha256;
    readonly HttpClient _http;
    readonly CancellationTokenSource _stop = new();
    Task? _task;
    long _received;

    public StemModelDownload(string path) : this(path, MODEL_URL, MODEL_SHA256, MODEL_SIZE) { }

    public StemModelDownload(string path, Uri url, string sha256, long size, HttpMessageHandler? handler = null)
    {
        (_path, _url, _sha256, Size) = (path, url, sha256, size);
        // The body's timed by Stall, read by read, as a slow connection can take many minutes over all of it.
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// How many bytes the model is.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// How many bytes have come so far.
    /// </summary>
    public long Received => Volatile.Read(ref _received);

    /// <summary>
    /// How far the download has got, from 0 to 1.
    /// </summary>
    public float Progress => Size > 0 ? Math.Clamp((float)Received / Size, 0, 1) : 0;

    public bool Downloading => _task is { IsCompleted: false };

    /// <summary>
    /// Why the last download failed; null if it didn't, or there hasn't been one. It isn't tried again until
    /// <see cref="ForgetFailure"/>.
    /// </summary>
    public Exception? Failed => _task is { IsFaulted: true } task ? task.Exception!.GetBaseException() : null;

    /// <summary>
    /// The download, once it's finished, for a test to wait for; null before it's started.
    /// </summary>
    internal Task? Task => _task;

    /// <summary>
    /// Starts downloading the model, unless it's there already, it's downloading, or the last try failed. Call from
    /// one thread only (the UI's).
    /// </summary>
    public void Start()
    {
        if (File.Exists(_path) || Downloading || Failed is not null || _stop.IsCancellationRequested)
            return;
        Volatile.Write(ref _received, 0);
        _task = System.Threading.Tasks.Task.Run(() => Fetch(_stop.Token));
    }

    /// <summary>
    /// Lets <see cref="Start"/> try again after a download failed.
    /// </summary>
    public void ForgetFailure()
    {
        if (Failed is not null)
            _task = null;
    }

    async Task Fetch(CancellationToken stop)
    {
        var part = _path + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using var stalled = CancellationTokenSource.CreateLinkedTokenSource(stop);
            stalled.CancelAfter(Stall);
            using var response = await _http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, stalled.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != Size)
                throw new InvalidDataException($"The download is {length:N0} bytes, not the model's {Size:N0}");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var body = await response.Content.ReadAsStreamAsync(stalled.Token))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long received = 0;
                while (true)
                {
                    stalled.CancelAfter(Stall);
                    var read = await body.ReadAsync(buffer, stalled.Token);
                    if (read == 0)
                        break;
                    received += read;
                    if (received > Size)
                        throw new InvalidDataException($"The download is more than the model's {Size:N0} bytes");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), stop);
                    Volatile.Write(ref _received, received);
                }
                if (received != Size)
                    throw new InvalidDataException($"The download stopped at {received:N0} bytes of the model's {Size:N0}");
            }
            if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(_sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download isn't the model: its SHA-256 is wrong");

            File.Move(part, _path, overwrite: true);
        }
        catch (Exception e)
        {
            TryDelete(part);
            if (stop.IsCancellationRequested)
                throw;
            // Not stopped, so it stalled.
            Exception failure = e is OperationCanceledException ? new TimeoutException($"Nothing came for {Stall.TotalSeconds:0} seconds", e) : e;
            ErrorLog.Write($"downloading the stem model from {_url}", failure);
            if (failure != e)
                throw failure;
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException) { }
        _http.Dispose();
    }
}
