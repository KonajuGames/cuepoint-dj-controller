namespace Brutality.Music;

/// <summary>
/// A list of songs being copied to a removable drive, to take away and play from: into a folder of the list's name
/// under Music at the drive's root. <see cref="Run"/> does the copying, on any thread, its progress to be read from
/// the UI thread meanwhile; then <see cref="Record"/>, on the UI thread, tells the drive's library file all that's
/// known about the songs copied. Each file is copied under a temporary name and only given its own once it's all
/// there, so a song copied already can be told by its size and time, and exporting again carries on where a stop or
/// a full drive left off. A song already on the drive isn't copied: the list has it where it is.
/// </summary>
sealed class DriveExport
{
    /// <summary>
    /// A song on the drive: the song it came from, where it is now, and its file's size and time there.
    /// </summary>
    public sealed record Exported(Song Source, string Path, long Size, DateTime Modified);

    /// <summary>
    /// How it went: the songs on the drive, in order; the paths of those that couldn't be copied; whether it was
    /// stopped; and why it gave up, if it did.
    /// </summary>
    public sealed record Result(IReadOnlyList<Exported> Songs, IReadOnlyList<string> Failed, bool Cancelled, string? Error);

    /// <summary>
    /// Where the lists' folders of songs go, from the drive's root.
    /// </summary>
    public const string MUSIC_FOLDER = "Music";

    const int BUFFER_BYTES = 1 << 20;
    const string PARTIAL_EXTENSION = ".part";

    /// <summary>
    /// How far apart a file's time and the song's can be and it still be the same song: FAT drives keep times to two
    /// seconds.
    /// </summary>
    static readonly TimeSpan SameTime = TimeSpan.FromSeconds(2.5);

    readonly IReadOnlyList<Song> _songs;
    readonly Func<string, long> _freeSpace;
    readonly CancellationTokenSource _cancel = new();
    readonly ManualResetEventSlim _finished = new();

    long _bytesDone;
    long _bytesTotal;
    int _songsDone;
    string? _current;

    /// <param name="name">What the list's called, which names its folder and playlist.</param>
    /// <param name="root">The drive's root.</param>
    /// <param name="freeSpace">How many bytes are free on the drive at a root; by default, what Windows says.</param>
    public DriveExport(string name, IReadOnlyList<Song> songs, string root, Func<string, long>? freeSpace = null)
    {
        FileName = SafeName(name);
        // A playlist can list a song twice, but it's one file, and a virtual folder has a song at most once.
        _songs = songs.DistinctBy(song => song.Path, StringComparer.OrdinalIgnoreCase).ToList();
        Root = LibraryFile.WithSeparator(root);
        _freeSpace = freeSpace ?? (drive => new DriveInfo(drive).AvailableFreeSpace);
    }

    /// <summary>
    /// The list's name, made fit to name a file.
    /// </summary>
    public string FileName { get; }

    public string Root { get; }

    /// <summary>
    /// The folder on the drive the songs go in.
    /// </summary>
    public string Folder => Path.Combine(Root, MUSIC_FOLDER, FileName);

    /// <summary>
    /// How far through it is, by the bytes to copy, from 0 to 1.
    /// </summary>
    public float Fraction
    {
        get
        {
            var total = Interlocked.Read(ref _bytesTotal);
            return total > 0 ? Math.Clamp((float)Interlocked.Read(ref _bytesDone) / total, 0, 1) : 0;
        }
    }

    /// <summary>
    /// What it's doing, to show.
    /// </summary>
    public string Status =>
        _cancel.IsCancellationRequested ? $"Stopping the export of {FileName}..."
        : Volatile.Read(ref _current) is { } current ? $"Copying {current} ({Math.Min(Volatile.Read(ref _songsDone) + 1, _songs.Count)} of {_songs.Count}) to {Root.TrimEnd('\\', '/')}"
        : $"Getting {FileName} ready to copy to {Root.TrimEnd('\\', '/')}...";

    /// <summary>
    /// Stops it at the next chance; what's been copied stays on the drive.
    /// </summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>
    /// Waits for <see cref="Run"/> to finish, for up to <paramref name="timeout"/>, as the app closes.
    /// </summary>
    public bool Wait(TimeSpan timeout) => _finished.Wait(timeout);

    /// <summary>
    /// <paramref name="name"/> fit to name a file: what can't be in one replaced, and no full stops or spaces on the
    /// end, as Windows won't keep them.
    /// </summary>
    public static string SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim().TrimEnd('.', ' ');
        return safe.Length > 0 ? safe : "Export";
    }

    /// <summary>
    /// Copies the songs. Safe on any thread. Whatever goes wrong comes back in the result.
    /// </summary>
    public Result Run()
    {
        try
        {
            return Copy();
        }
        catch (OperationCanceledException)
        {
            return new Result([], [], true, null);
        }
        catch (Exception e)
        {
            // Something unforeseen mustn't leave the export showing for good.
            return new Result([], [], false, e.Message);
        }
        finally
        {
            _finished.Set();
        }
    }

    /// <summary>
    /// A song and where it goes: to be copied from <see cref="From"/>, or, without it, there already.
    /// </summary>
    sealed record Planned(Song Song, string Path, FileInfo? From);

    Result Copy()
    {
        var failed = new List<string>();
        var plan = Plan(failed);
        var bytes = plan.Sum(item => item.From?.Length ?? 0);
        Interlocked.Exchange(ref _bytesTotal, bytes);

        var free = _freeSpace(Root);
        if (bytes > free)
            return new Result([], failed, false, $"it needs {Size(bytes)}, and the drive has {Size(free)} free");

        var exported = new List<Exported>(plan.Count);
        var buffer = new byte[BUFFER_BYTES];
        foreach (var item in plan)
        {
            if (_cancel.IsCancellationRequested)
                return new Result(exported, failed, true, null);
            if (item.From is { } from)
            {
                Volatile.Write(ref _current, item.Song.Title);
                var before = Interlocked.Read(ref _bytesDone);
                try
                {
                    Directory.CreateDirectory(Folder);
                    CopyFile(from, item.Path, buffer);
                }
                catch (OperationCanceledException)
                {
                    return new Result(exported, failed, true, null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    // A full or vanished drive stops everything; a song that won't copy is only itself.
                    if (IsDiskFull(e) || !Directory.Exists(Root))
                        return new Result(exported, failed, false, e.Message);
                    failed.Add(item.Song.Path);
                    Interlocked.Exchange(ref _bytesDone, before + from.Length);
                    Interlocked.Increment(ref _songsDone);
                    continue;
                }
            }
            var info = new FileInfo(item.Path);
            exported.Add(new Exported(item.Song, item.Path, info.Length, info.LastWriteTimeUtc));
            Interlocked.Increment(ref _songsDone);
        }
        return new Result(exported, failed, false, null);
    }

    /// <summary>
    /// Where each song goes: a name of its own in the folder (the next free one, where two share a name), or the one
    /// it had if it's been copied there already; or where it is, if it's on the drive.
    /// </summary>
    List<Planned> Plan(List<string> failed)
    {
        var plan = new List<Planned>(_songs.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in _songs)
        {
            _cancel.Token.ThrowIfCancellationRequested();
            try
            {
                var from = new FileInfo(song.Path);
                if (!from.Exists)
                    failed.Add(song.Path);
                else if (song.Path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                    plan.Add(new Planned(song, song.Path, null));
                else
                {
                    var (path, there) = Destination(from, taken);
                    plan.Add(new Planned(song, path, there ? null : from));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                failed.Add(song.Path);
            }
        }
        return plan;
    }

    (string Path, bool There) Destination(FileInfo from, HashSet<string> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(from.Name);
        for (var i = 1; ; i++)
        {
            var path = Path.Combine(Folder, (i == 1 ? stem : $"{stem} ({i})") + from.Extension);
            if (taken.Contains(path))
                continue;
            var there = new FileInfo(path);
            var same = there.Exists && there.Length == from.Length && (there.LastWriteTimeUtc - from.LastWriteTimeUtc).Duration() <= SameTime;
            if (there.Exists && !same)
                continue;
            taken.Add(path);
            return (path, same);
        }
    }

    void CopyFile(FileInfo from, string path, byte[] buffer)
    {
        var partial = path + PARTIAL_EXTENSION;
        try
        {
            using (var source = new FileStream(from.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            // Written through, so what the bar says is copied is on the drive, for a drive that's pulled out.
            using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    _cancel.Token.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    Interlocked.Add(ref _bytesDone, read);
                }
            }
            // An even second, which every drive can keep, so the file's time reads the same when it's plugged back in.
            var ticks = from.LastWriteTimeUtc.Ticks;
            File.SetLastWriteTimeUtc(partial, new DateTime(ticks - ticks % (2 * TimeSpan.TicksPerSecond), DateTimeKind.Utc));
            File.Move(partial, path);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next export writes over it.
        }
    }

    static bool IsDiskFull(Exception e)
    {
        const int ERROR_HANDLE_DISK_FULL = 39;
        const int ERROR_DISK_FULL = 112;
        return e is IOException && (e.HResult & 0xFFFF) is ERROR_HANDLE_DISK_FULL or ERROR_DISK_FULL;
    }

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>
    /// Tells <paramref name="database"/> all it knows about each song about the copy on the drive: its analysis, and
    /// its hot cues, memory cues, cue point and fixed grid unless the copy has its own there from an earlier export. A song
    /// that stayed where it was is known already. UI thread only; doesn't save.
    /// </summary>
    public static void Record(LibraryDatabase database, IEnumerable<Exported> songs)
    {
        foreach (var (source, path, size, modified) in songs)
        {
            if (string.Equals(source.Path, path, StringComparison.OrdinalIgnoreCase))
                continue;
            var copy = new Song(path, size, modified, source.Title, source.Artist, source.Album) { HasTags = source.HasTags };
            if (database.TryGetAnalysed(source, out var analysis, out var error))
            {
                if (analysis is not null)
                    database.Set(copy, analysis);
                else if (error is not null)
                    database.SetFailed(copy, error);
            }
            if (database.GetHotcues(path).All(cue => cue is null) && database.GetHotcues(source.Path) is { Count: > 0 } hotcues)
                database.SetHotcues(path, hotcues);
            if (database.GetMemoryCues(path).Count == 0 && database.GetMemoryCues(source.Path) is { Count: > 0 } memoryCues)
                database.SetMemoryCues(path, memoryCues);
            if (database.GetHotcueDetails(path).Count == 0 && database.GetHotcueDetails(source.Path) is { Count: > 0 } details)
                database.SetHotcueDetails(path, details);
            if (database.GetMemoryLoops(path).Count == 0 && database.GetMemoryLoops(source.Path) is { Count: > 0 } loops)
                database.SetMemoryLoops(path, loops);
            if (database.GetCuePoint(path) is null && database.GetCuePoint(source.Path) is { } cuePoint)
                database.SetCuePoint(path, cuePoint);
            if (!database.HasGridEdit(path) && database.GetGridEdit(source.Path, analysis?.Grid.Duration ?? 0) is { } edit)
                database.SetGridEdit(path, edit);
        }
    }
}
