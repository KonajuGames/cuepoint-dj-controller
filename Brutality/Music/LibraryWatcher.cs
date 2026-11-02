using System.Diagnostics;

namespace Brutality.Music;

/// <summary>
/// Watches the library's folder for songs added, changed, renamed or taken away, so the library can be scanned again
/// without starting the app again. Changes come in bursts (an album copied in, a song's tags edited), so it says
/// there's been a change only once they've stopped for a moment (see <see cref="TakeChanges"/>). It passes over the
/// files the app writes there itself, its library file and virtual folders on a drive, and anything
/// <c>ignore</c> says to. Windows says what's changed on a thread of its own; asking is safe from any thread.
/// </summary>
sealed class LibraryWatcher : IDisposable
{
    /// <summary>
    /// The files the app keeps beside songs, which mustn't set off a scan every time it saves them.
    /// </summary>
    static readonly HashSet<string> AppFiles = new([".json", ".tmp", ".m3u8", ".log"], StringComparer.OrdinalIgnoreCase);

    readonly FileSystemWatcher _watcher;
    readonly Func<string, bool>? _ignore;

    // When the last change came, from Stopwatch; 0 when none has come since the last that were taken.
    long _changedAt;

    public string Folder { get; }

    LibraryWatcher(string folder, bool recursive, Func<string, bool>? ignore)
    {
        Folder = folder;
        _ignore = ignore;
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            // Big enough for a whole album copied in at once, so none is missed; if some are, it's said all the same.
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, _) => Changed();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Watches <paramref name="folder"/>, and its subfolders too if <paramref name="recursive"/>, passing over paths
    /// <paramref name="ignore"/> says to. Null if it can't be watched: it isn't there, say, or it's on a drive that
    /// can't say what's changed.
    /// </summary>
    public static LibraryWatcher? Start(string folder, bool recursive, Func<string, bool>? ignore = null)
    {
        try
        {
            return new LibraryWatcher(folder, recursive, ignore);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (Matters(e.FullPath))
            Changed();
    }

    void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (Matters(e.FullPath) || Matters(e.OldFullPath))
            Changed();
    }

    void Changed() => Interlocked.Exchange(ref _changedAt, Stopwatch.GetTimestamp());

    /// <summary>
    /// Whether a change at <paramref name="path"/> could change the library: a song, or a folder, which can hold
    /// them. Something gone can't be looked at, so anything gone counts but the app's own files.
    /// </summary>
    bool Matters(string path)
    {
        if (_ignore?.Invoke(path) == true)
            return false;
        var extension = Path.GetExtension(path);
        if (MusicLibrary.Extensions.Contains(extension))
            return true;
        if (Directory.Exists(path))
            return true;
        return !File.Exists(path) && !AppFiles.Contains(extension);
    }

    /// <summary>
    /// Whether anything's changed since this last said so, and has since been still for <paramref name="quiet"/>; if
    /// so, the change is taken, and it says no again until the next.
    /// </summary>
    public bool TakeChanges(TimeSpan quiet)
    {
        var at = Volatile.Read(ref _changedAt);
        if (at == 0 || Stopwatch.GetElapsedTime(at) < quiet)
            return false;
        return Interlocked.CompareExchange(ref _changedAt, 0, at) == at;
    }

    public void Dispose() => _watcher.Dispose();
}
