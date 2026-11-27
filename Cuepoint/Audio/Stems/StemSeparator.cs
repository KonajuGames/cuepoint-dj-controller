namespace Cuepoint.Audio.Stems;

/// <summary>
/// Finding one song's stems, in the background: <see cref="Done"/> once it's finished, with the stems, or null if
/// they weren't on disk and it wasn't asked to separate them.
/// </summary>
sealed class StemJob
{
    public required string Path { get; init; }

    public required bool Separating { get; init; }

    internal Task<SongStems?> Task { get; set; } = null!;

    internal float _progress;

    /// <summary>
    /// How far separating has got, from 0 to 1.
    /// </summary>
    public float Progress => Volatile.Read(ref _progress);

    public bool Done => Task.IsCompleted;

    public SongStems? Stems => Task.IsCompletedSuccessfully ? Task.Result : null;

    public Exception? Failed => Task.IsFaulted ? Task.Exception?.GetBaseException() : null;
}

/// <summary>
/// Separates songs into their stems with Demucs (on the graphics card where it can be), one at a time on a
/// low-priority thread, keeping each on disk so it's
/// only done once. The model is an ONNX export of Demucs's htdemucs (tools/export_demucs.py makes it), which isn't
/// shipped with the app: <paramref name="download"/> fetches it the first time it's wanted. Without it, only stems
/// already on disk can be had.
/// </summary>
sealed class StemSeparator(StemCache cache, string modelPath, StemModelDownload? download = null) : IDisposable
{
    readonly SemaphoreSlim _one = new(1, 1);
    readonly Lock _lock = new();
    readonly Dictionary<string, StemJob> _jobs = [];
    Demucs? _model;

    public string ModelPath { get; } = modelPath;

    public bool HasModel => File.Exists(ModelPath);

    /// <summary>
    /// Fetches the model when it isn't there; null if it can't be fetched, only made with tools/export_demucs.py.
    /// </summary>
    public StemModelDownload? Download { get; } = download;

    /// <summary>
    /// Whether the song at <paramref name="path"/> has been separated already, its stems kept on disk.
    /// </summary>
    public bool HasStems(string path) => cache.Has(path);

    /// <summary>
    /// <see cref="HasStems"/>, cheaply enough for every song in view on every frame (the song list's stems icon), at
    /// the cost of noticing a song's file changed outside the app only after a few seconds.
    /// </summary>
    public bool HasStemsQuickly(string path) => cache.HasQuickly(path);

    /// <summary>
    /// How many bytes the stems kept on disk for the song at <paramref name="path"/> take; null if there are none.
    /// </summary>
    public long? StemsSize(string path) => cache.SizeOf(path);

    /// <summary>
    /// The file the stems of the song at <paramref name="path"/> are kept in, made or not; null if the song isn't there.
    /// </summary>
    public string? StemsFile(string path) => cache.FileFor(path);

    /// <summary>
    /// Keeps the stems that were in <paramref name="oldFile"/> (from <see cref="StemsFile"/>) for the song at
    /// <paramref name="path"/> as its file is now, once only its tags have changed, so it isn't separated again. Says
    /// nothing if they can't be: it's separated again if they're wanted.
    /// </summary>
    public void FollowStems(string? oldFile, string path)
    {
        try
        {
            cache.Follow(oldFile, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes the stems kept on disk for the song at <paramref name="path"/>, and forgets any found for it, so it's
    /// separated again if they're wanted. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// if they can't be deleted.
    /// </summary>
    public void DeleteStems(string path)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(path, out var job) && job.Done)
                _jobs.Remove(path);
        }
        cache.Delete(path);
    }

    /// <summary>
    /// Whether something's playing (a deck, or a preview), when separating keeps off the graphics card, which the
    /// screen needs, and to half the processor's cores. Set it once a frame.
    /// </summary>
    public bool Busy
    {
        get => Volatile.Read(ref _busy);
        set => Volatile.Write(ref _busy, value);
    }

    bool _busy;

    /// <summary>
    /// Finds the stems of the song at <paramref name="path"/>, whose decoded samples are <paramref name="audio"/>:
    /// from disk, or if they aren't there and <paramref name="separate"/>, by separating it. Asking again for a song
    /// already being found gives the same job, unless that one failed or didn't separate and this one would.
    /// </summary>
    public StemJob Request(string path, PcmAudio audio, bool separate)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(path, out var existing) && existing.Failed is null && (existing.Separating || !separate || !existing.Done || existing.Stems is not null))
                return existing;
            var job = new StemJob { Path = path, Separating = separate };
            job.Task = Task.Run(() => Find(job, audio));
            _jobs[path] = job;
            return job;
        }
    }

    async Task<SongStems?> Find(StemJob job, PcmAudio audio)
    {
        if (cache.Load(job.Path) is { } cached && cached.Channels == audio.Channels && cached.Frames == audio.Frames)
            return cached;
        if (!job.Separating)
            return null;
        if (!HasModel)
            throw new FileNotFoundException($"There's no stem model at {ModelPath}");

        await _one.WaitAsync();
        try
        {
            var stems = await OnLowPriorityThread(() =>
            {
                _model ??= new Demucs(ModelPath);
                try
                {
                    return _model.Separate(audio, progress => Volatile.Write(ref job._progress, progress), () => Busy);
                }
                catch (GpuWentWrongException e)
                {
                    // The model's dropped the graphics card, so it's all on the processor from here on.
                    ErrorLog.Write($"separating the stems of {job.Path} on the graphics card, so it's on the processor", e);
                    return _model.Separate(audio, progress => Volatile.Write(ref job._progress, progress), () => Busy);
                }
            });
            try
            {
                cache.Save(job.Path, stems);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write($"keeping the stems of {job.Path}", e);
            }
            return stems;
        }
        finally
        {
            _one.Release();
        }
    }

    // Separating takes a while, so it gives way to everything else, the decks' audio above all.
    static Task<T> OnLowPriorityThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "Stem separation",
        };
        thread.Start();
        return done.Task;
    }

    public void Dispose()
    {
        Download?.Dispose();
        _model?.Dispose();
    }
}
