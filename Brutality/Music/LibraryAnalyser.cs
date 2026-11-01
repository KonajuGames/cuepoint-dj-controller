using System.Collections.Concurrent;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// Analyses the library's songs in the background as they're asked for, a few at a time, and records the results in
/// a <see cref="LibraryDatabase"/>, saving it whenever one comes in. The most recent request goes first, so the songs
/// on screen now come before ones scrolled past earlier. Call <see cref="Request"/>, <see cref="ProgressOf"/> and
/// <see cref="Update"/> from the UI thread only.
/// </summary>
sealed class LibraryAnalyser : IDisposable
{
    public const int MAX_CONCURRENT = 4;

    /// <summary>
    /// A song was analysed, or failed to be; raised from <see cref="Update"/>.
    /// </summary>
    public event Action<Song>? Analysed;

    /// <summary>
    /// Why the database couldn't be saved last time, if it couldn't.
    /// </summary>
    public string? SaveError { get; private set; }

    public LibraryDatabase Database { get; }

    /// <summary>
    /// Songs being analysed right now, never more than the number of workers.
    /// </summary>
    public int ActiveCount => _active.Values.Count(job => !job.IsDone);

    public int QueuedCount
    {
        get
        {
            lock (_lock)
                return _queue.Count;
        }
    }

    sealed class Job
    {
        public volatile float Progress;

        /// <summary>
        /// Its worker has moved on, though <see cref="Update"/> may not have collected the result yet.
        /// </summary>
        public volatile bool IsDone;
    }

    readonly object _lock = new();

    /// <summary>
    /// Songs waiting, the next at the end. Guarded by <see cref="_lock"/>.
    /// </summary>
    readonly List<Song> _queue = [];

    /// <summary>
    /// Every song asked for that hasn't had its result applied: waiting, being analysed, or finished. UI thread only.
    /// </summary>
    readonly HashSet<Song> _requested = [];

    /// <summary>
    /// Songs already looked up in the database, so each is looked up once. UI thread only.
    /// </summary>
    readonly HashSet<Song> _lookedUp = [];

    readonly ConcurrentDictionary<Song, Job> _active = new();
    readonly ConcurrentQueue<(Song Song, TrackAnalysis? Analysis, string? Error)> _finished = new();
    readonly CancellationTokenSource _stop = new();
    readonly Thread[] _workers;
    bool _stopping;

    /// <summary>
    /// Puts what <paramref name="database"/> already knows onto <paramref name="songs"/>, and starts
    /// <paramref name="workers"/> threads to analyse more as they're asked for.
    /// </summary>
    public LibraryAnalyser(IEnumerable<Song> songs, LibraryDatabase database, int workers = MAX_CONCURRENT)
    {
        Database = database;
        foreach (var song in songs)
            LookUp(song);

        // Below normal priority, so decoding four songs at once never gets in the way of drawing or the audio.
        _workers = Enumerable
            .Range(1, workers)
            .Select(i => new Thread(Work)
            {
                Name = $"Analysis {i}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            })
            .ToArray();
        foreach (var worker in _workers)
            worker.Start();
    }

    /// <summary>
    /// Asks for <paramref name="song"/> to be analysed, ahead of everything already waiting. Does nothing if it's
    /// already analysed, failed, or being analysed now.
    /// </summary>
    public void Request(Song song) => Request([song]);

    /// <summary>
    /// Asks for <paramref name="songs"/> to be analysed, ahead of everything already waiting and in the order given,
    /// such as the rows on screen from the top down. All are queued before any worker takes one, so the first is
    /// started first.
    /// </summary>
    public void Request(IReadOnlyList<Song> songs)
    {
        lock (_lock)
        {
            var added = false;
            for (var i = songs.Count - 1; i >= 0; i--)
            {
                var song = songs[i];
                // Songs from outside the library may have been analysed in an earlier run.
                LookUp(song);
                if (song.Analysis is not null || song.AnalysisError is not null)
                    continue;
                // Already asked for and not waiting means it's being analysed or just finished.
                if (!_queue.Remove(song) && !_requested.Add(song))
                    continue;
                _queue.Add(song);
                added = true;
            }
            if (added)
                Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Puts what the database knows about <paramref name="song"/> on it, the first time it's seen.
    /// </summary>
    void LookUp(Song song)
    {
        if (_lookedUp.Add(song) && Database.TryGet(song, out var analysis, out var error))
            (song.Analysis, song.AnalysisError) = (analysis, error);
    }

    /// <summary>
    /// How far through analysing <paramref name="song"/> it is, from 0 to 1, while it's being analysed.
    /// </summary>
    public float? ProgressOf(Song song) => _active.TryGetValue(song, out var job) ? job.Progress : null;

    /// <summary>
    /// Puts finished analyses on their songs and into the database, raising <see cref="Analysed"/> for each, and
    /// saves the database if anything changed. Call once a frame; returns how many songs were updated.
    /// </summary>
    public int Update()
    {
        var updated = 0;
        while (_finished.TryDequeue(out var finished))
        {
            var song = finished.Song;
            _active.TryRemove(song, out _);
            lock (_lock)
                _requested.Remove(song);

            if (finished.Analysis is { } analysis)
            {
                // Kept as analysed; the song gets its grid as fixed by hand, if it was.
                Database.Set(song, analysis);
                song.Analysis = Database.WithEdits(song.Path, analysis);
            }
            else
            {
                song.AnalysisError = finished.Error ?? "Analysis failed";
                Database.SetFailed(song, song.AnalysisError);
            }
            updated++;
            Analysed?.Invoke(song);
        }

        if (updated > 0)
        {
            try
            {
                Database.Save();
                SaveError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Kept in memory, and tried again with the next result.
                SaveError = e.Message;
            }
        }
        return updated;
    }

    void Work()
    {
        var stop = _stop.Token;
        while (true)
        {
            Song song;
            var job = new Job();
            lock (_lock)
            {
                while (_queue.Count == 0 && !_stopping)
                    Monitor.Wait(_lock);
                if (_stopping)
                    return;
                song = _queue[^1];
                _queue.RemoveAt(_queue.Count - 1);
                // Inside the lock, so a song is always either waiting or active, never briefly neither.
                _active[song] = job;
            }

            try
            {
                var analysis = TrackAnalyzer.Analyse(song.Path, progress => job.Progress = progress, stop);
                _finished.Enqueue((song, analysis, null));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _finished.Enqueue((song, null, e.GetBaseException().Message));
            }
            finally
            {
                job.IsDone = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopping = true;
            Monitor.PulseAll(_lock);
        }
        _stop.Cancel();
        foreach (var worker in _workers)
            worker.Join();
        _stop.Dispose();
    }
}
