using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// What's been learnt about the library's songs (see <see cref="LibraryFile"/>), kept with the songs: songs on the
/// system drive in the app's own library.json, as ever, and songs on any other drive in a library.json in a Cuepoint
/// folder at that drive's root, with their paths relative to it. A removable drive takes its songs' analyses, hotcues
/// and fixed grids with it, under whatever letter it gets next. A drive's file, and its folder, are only made once
/// there's something to keep about a song on it; a drive that can't be written to has its songs kept in the app's
/// own file instead.
///
/// Opening it moves anything the app's own file has about songs on drives that are plugged in onto those drives.
/// Only used from the UI thread.
/// </summary>
sealed class LibraryDatabase
{
    /// <summary>
    /// Bump this when analysis changes enough that earlier results should be redone.
    /// </summary>
    public const int ANALYSIS_VERSION = 4;

    /// <summary>
    /// Where a drive's file goes, from its root.
    /// </summary>
    public const string DRIVE_FOLDER = "Cuepoint";

    public const string FILE_NAME = "library.json";

    readonly LibraryFile _system;
    readonly string _systemRoot;
    readonly Func<string, string?> _rootOf;
    readonly Dictionary<string, LibraryFile> _drives = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _unwritable = new(StringComparer.OrdinalIgnoreCase);

    LibraryDatabase(LibraryFile system, string systemRoot, Func<string, string?> rootOf)
    {
        _system = system;
        _systemRoot = Normal(systemRoot);
        _rootOf = rootOf;
        LoadError = system.LoadError is { } error ? $"{system.FilePath}: {error}" : null;
    }

    /// <summary>
    /// The app's own file, for songs on the system drive.
    /// </summary>
    public string FilePath => _system.FilePath;

    /// <summary>
    /// Why a file couldn't be read, if one was there but couldn't be, with its path; it starts empty instead.
    /// </summary>
    public string? LoadError { get; private set; }

    public int Count => _system.Count + _drives.Values.Sum(file => file.Count);

    /// <summary>
    /// Whether any of its files has changes not yet saved.
    /// </summary>
    public bool Dirty => _system.Dirty || _drives.Values.Any(file => file.Dirty);

    /// <summary>
    /// The drives' own files in use, by path.
    /// </summary>
    public IEnumerable<string> DriveFiles => _drives.Values.Select(file => file.FilePath);

    /// <summary>
    /// Opens the app's own file at <paramref name="filePath"/>, then any other drive's as its songs come up, moving
    /// what the app's own file has about songs on other drives (that are plugged in) onto them. The system drive is
    /// <paramref name="systemRoot"/>, by default the one Windows is on; <paramref name="rootOf"/> gives the drive a
    /// path is on (its root, as "F:\"), by default from the path itself.
    /// </summary>
    public static LibraryDatabase Load(string filePath, string? systemRoot = null, Func<string, string?>? rootOf = null)
    {
        var database = new LibraryDatabase(
            LibraryFile.Load(filePath),
            systemRoot ?? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            rootOf ?? (path => Path.GetPathRoot(Path.GetFullPath(path)))
        );
        database.MoveToDrives();
        return database;
    }

    static string Normal(string root) => LibraryFile.WithSeparator(root);

    /// <summary>
    /// The root of the drive <paramref name="path"/> is on, if it's not the system drive; null if it is.
    /// </summary>
    string? OtherDrive(string path)
    {
        if (_rootOf(path) is not { Length: > 0 } root)
            return null;
        root = Normal(root);
        return string.Equals(root, _systemRoot, StringComparison.OrdinalIgnoreCase) ? null : root;
    }

    /// <summary>
    /// The file a song's kept in: its drive's, or the app's own for the system drive, a drive that isn't plugged in,
    /// or one that can't be written to.
    /// </summary>
    LibraryFile FileFor(string path)
    {
        if (OtherDrive(path) is not { } root || _unwritable.Contains(root))
            return _system;
        if (_drives.TryGetValue(root, out var file))
            return file;
        if (!Directory.Exists(root))
            return _system;

        file = LibraryFile.Load(Path.Combine(root, DRIVE_FOLDER, FILE_NAME), root);
        if (file.LoadError is { } error)
            LoadError ??= $"{file.FilePath}: {error}";
        _drives[root] = file;
        return file;
    }

    /// <summary>
    /// Moves what the app's own file has about songs on other drives that are plugged in onto those drives' files,
    /// and saves, so it's only in one place. What a drive's file already has wins: it went with the drive.
    /// </summary>
    void MoveToDrives()
    {
        var moved = 0;
        foreach (var group in _system.Paths.GroupBy(path => OtherDrive(path) ?? "", StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (group.Key == "" || FileFor(group.First()) is not { } file || file == _system)
                continue;
            var root = group.Key;
            moved += _system.MoveTo(file, path => string.Equals(OtherDrive(path), root, StringComparison.OrdinalIgnoreCase), overwrite: false);
        }
        // Saved if anything moved, or a drive's file wants writing again (kept from before paths were relative).
        if (moved == 0 && !_drives.Values.Any(file => file.Dirty))
            return;
        try
        {
            Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still moved in memory; saved with the next change.
            LoadError ??= $"{_system.FilePath}: {e.Message}";
        }
    }

    public bool TryGet(Song song, out TrackAnalysis? analysis, out string? error) => FileFor(song.Path).TryGet(song, out analysis, out error);

    public bool TryGetAnalysed(Song song, out TrackAnalysis? analysis, out string? error) =>
        FileFor(song.Path).TryGetAnalysed(song, out analysis, out error);

    public void Set(Song song, TrackAnalysis analysis) => FileFor(song.Path).Set(song, analysis);

    public void SetFailed(Song song, string error) => FileFor(song.Path).SetFailed(song, error);

    public bool TryGetNames(Song song, out string? title, out string? artist, out string? album) =>
        FileFor(song.Path).TryGetNames(song, out title, out artist, out album);

    public void SetNames(Song song) => FileFor(song.Path).SetNames(song);

    public IReadOnlyList<TimeSpan?> GetHotcues(string path) => FileFor(path).GetHotcues(path);

    public void SetHotcues(string path, IReadOnlyList<TimeSpan?> hotcues) => FileFor(path).SetHotcues(path, hotcues);

    public IReadOnlyList<TimeSpan> GetMemoryCues(string path) => FileFor(path).GetMemoryCues(path);

    public void SetMemoryCues(string path, IReadOnlyList<TimeSpan> cues) => FileFor(path).SetMemoryCues(path, cues);

    public TimeSpan? GetCuePoint(string path) => FileFor(path).GetCuePoint(path);

    public void SetCuePoint(string path, TimeSpan cuePoint) => FileFor(path).SetCuePoint(path, cuePoint);

    public TrackAnalysis WithEdits(string path, TrackAnalysis analysis) => FileFor(path).WithEdits(path, analysis);

    public GridEdit? GetGridEdit(string path, double duration) => FileFor(path).GetGridEdit(path, duration);

    public void SetGridEdit(string path, GridEdit? edit) => FileFor(path).SetGridEdit(path, edit);

    public bool HasGridEdit(string path) => FileFor(path).HasGridEdit(path);

    // Files being written on another thread, each with its drive (null for the app's own) and what went wrong.
    Task<List<(string? Root, LibraryFile File, Exception? Error)>>? _saving;

    /// <summary>
    /// Whether a save started by <see cref="BeginSave"/> hasn't been finished with yet.
    /// </summary>
    public bool IsSaving => _saving is not null;

    /// <summary>
    /// Starts writing out whatever's changed on another thread, as <see cref="Save"/> would, so a big library doesn't
    /// hold up the screen. Does nothing if one's going already. Call <see cref="FinishSave"/> once a frame after it.
    /// </summary>
    public void BeginSave()
    {
        if (_saving is not null)
            return;
        var jobs = new List<(string? Root, LibraryFile File, Action Write)>();
        foreach (var (root, file) in _drives)
        {
            if (file.Dirty && (!file.IsEmpty || File.Exists(file.FilePath)))
                jobs.Add((root, file, file.Snapshot()));
        }
        if (_system.Dirty || !File.Exists(_system.FilePath))
            jobs.Add((null, _system, _system.Snapshot()));
        if (jobs.Count == 0)
            return;
        _saving = Task.Run(() =>
        {
            var results = new List<(string?, LibraryFile, Exception?)>(jobs.Count);
            foreach (var (root, file, write) in jobs)
            {
                Exception? error = null;
                try
                {
                    write();
                }
                catch (Exception e)
                {
                    error = e;
                }
                results.Add((root, file, error));
            }
            return results;
        });
    }

    /// <summary>
    /// Takes in a save started by <see cref="BeginSave"/>, if it's done, as <see cref="Save"/> deals with its files;
    /// returns whether it was, and in <paramref name="error"/> why the app's own file couldn't be written, if it
    /// couldn't.
    /// </summary>
    public bool FinishSave(out string? error)
    {
        error = null;
        if (_saving is not { IsCompleted: true } saving)
            return false;
        _saving = null;
        foreach (var (root, file, problem) in saving.Result)
        {
            if (problem is null)
                continue;
            if (root is null)
            {
                file.MarkDirty();
                error = problem.Message;
            }
            else
            {
                // A read-only drive, or one taken out mid-set: its songs are kept with the app's instead.
                _unwritable.Add(root);
                file.MoveTo(_system, _ => true, overwrite: true);
                _drives.Remove(root);
            }
        }
        return true;
    }

    /// <summary>
    /// Makes sure the drive at <paramref name="root"/> has its own file, even with nothing to keep in it yet, after
    /// <see cref="Save"/>. Returns whether it has.
    /// </summary>
    public bool EnsureDriveFile(string root)
    {
        var path = Path.Combine(Normal(root), DRIVE_FOLDER, FILE_NAME);
        if (File.Exists(path))
            return true;
        var file = FileFor(Path.Combine(Normal(root), FILE_NAME));
        if (file == _system)
            return false;
        try
        {
            file.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return File.Exists(path);
    }

    /// <summary>
    /// Writes out whatever's changed: each drive's file (made, with its folder, only once there's something in it),
    /// then the app's own. A drive that can't be written to has its songs moved into the app's own file, and kept
    /// there from then on. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the app's
    /// own file can't be written.
    /// </summary>
    public void Save()
    {
        // One going already is finished first, so the two can't write over each other, or the older land last.
        _saving?.Wait();
        FinishSave(out _);
        foreach (var (root, file) in _drives.ToList())
        {
            if (!file.Dirty)
                continue;
            if (file.IsEmpty && !File.Exists(file.FilePath))
                continue;
            try
            {
                file.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A read-only drive, or one taken out mid-set: its songs are kept with the app's instead.
                _unwritable.Add(root);
                file.MoveTo(_system, _ => true, overwrite: true);
                _drives.Remove(root);
            }
        }
        if (_system.Dirty || !File.Exists(_system.FilePath))
            _system.Save();
    }
}
