using System.Text;

namespace Brutality.Music;

/// <summary>
/// A list of songs kept in the order they were put in, saved as a playlist file of their paths. A song is in it at
/// most once. Songs whose files have gone stay in the file, so they come back if the file does, but aren't listed.
/// </summary>
sealed class VirtualFolder
{
    readonly List<string> _paths;

    internal VirtualFolder(string name, string filePath, List<string> paths, string? drive)
    {
        Name = name;
        FilePath = filePath;
        _paths = paths;
        Drive = drive;
    }

    public string Name { get; internal set; }

    /// <summary>
    /// The root of the drive it's stored on (as "F:\"), in a Cuepoint folder there; null when it's in the app's own
    /// folder on the system drive.
    /// </summary>
    public string? Drive { get; internal set; }

    /// <summary>
    /// Its name, with the drive it's on if that isn't the system drive, as "Warm up (F:)": two drives can each have
    /// one of a name.
    /// </summary>
    public string DisplayName => Drive is { } drive ? $"{Name} ({drive.TrimEnd('\\', '/')})" : Name;

    /// <summary>
    /// The playlist file it's saved in.
    /// </summary>
    public string FilePath { get; internal set; }

    /// <summary>
    /// Every song's path, in order, including any whose files are missing.
    /// </summary>
    public IReadOnlyList<string> Paths => _paths;

    internal List<string> EditablePaths => _paths;

    public bool Contains(string path) => _paths.Contains(path, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A drive a virtual folder can be stored on: its root (as "F:\"), what to call it, and whether it can be written to.
/// </summary>
sealed record StorageDrive(string Root, string Label, bool Writable);

/// <summary>
/// The virtual folders: every playlist (.m3u8) in the app's own folder, and in a Cuepoint\VirtualFolders folder on
/// any drive, one file each, named for the virtual folder. A folder can be moved to any drive that can be written
/// to, so one of songs on a removable drive goes with it: songs on the drive it's stored on are written relative to
/// it, so they're found under whatever letter the drive gets. A playlist written elsewhere and copied in shows up as
/// one too. Every change is saved straight away. UI thread only.
/// </summary>
sealed class VirtualFolders
{
    public const string EXTENSION = ".m3u8";

    /// <summary>
    /// Where a drive's virtual folders are kept, from its root.
    /// </summary>
    public static readonly string DriveFolder = Path.Combine("Cuepoint", "VirtualFolders");

    readonly MusicLibrary _library;
    readonly List<VirtualFolder> _folders = [];
    readonly Func<IReadOnlyList<StorageDrive>> _drives;
    readonly string _systemRoot;

    // Songs found for paths outside the library, so a folder of them doesn't look on disk each time it's listed.
    readonly Dictionary<string, Song> _found = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="directory">The app's own folder for them, on the system drive.</param>
    /// <param name="drives">The drives there are now, the system drive among them; by default, every one Windows
    /// has ready.</param>
    /// <param name="systemRoot">The system drive, whose virtual folders are kept in <paramref name="directory"/>;
    /// by default, the one Windows is on.</param>
    public VirtualFolders(MusicLibrary library, string directory, Func<IReadOnlyList<StorageDrive>>? drives = null, string? systemRoot = null)
    {
        _library = library;
        Directory = directory;
        _drives = drives ?? (() => ReadyDrives());
        _systemRoot = LibraryFile.WithSeparator(systemRoot ?? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
        Load();
    }

    /// <summary>
    /// The app's own folder for virtual folders, on the system drive.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Every drive Windows has ready that could hold music: fixed, removable and network ones. Each is writable
    /// unless <paramref name="readOnly"/> says it isn't.
    /// </summary>
    public static IReadOnlyList<StorageDrive> ReadyDrives(Func<string, bool>? readOnly = null)
    {
        var drives = new List<StorageDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                    continue;
                var root = drive.RootDirectory.FullName;
                var letter = root.TrimEnd('\\', '/');
                var label = drive.VolumeLabel.Length > 0 ? $"{letter} {drive.VolumeLabel}" : letter;
                drives.Add(new StorageDrive(root, label, readOnly?.Invoke(root) != true));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A drive going away as it's asked about.
            }
        }
        return drives;
    }

    /// <summary>
    /// The drives a virtual folder can be moved to: those that can be written to, the system drive first.
    /// </summary>
    public IReadOnlyList<StorageDrive> WritableDrives() =>
        _drives().Where(drive => drive.Writable).OrderBy(drive => IsSystem(drive.Root) ? 0 : 1).ThenBy(drive => drive.Root, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Whether <paramref name="folder"/> is kept on the drive at <paramref name="root"/>.
    /// </summary>
    public bool IsOn(VirtualFolder folder, string root) =>
        folder.Drive is { } drive ? string.Equals(drive, LibraryFile.WithSeparator(root), StringComparison.OrdinalIgnoreCase) : IsSystem(root);

    bool IsSystem(string root) => string.Equals(LibraryFile.WithSeparator(root), _systemRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the virtual folders on <paramref name="drive"/> (null for the app's own) are kept.
    /// </summary>
    string FolderFor(string? drive) => drive is null ? Directory : Path.Combine(drive, DriveFolder);

    /// <summary>
    /// The virtual folders, by name.
    /// </summary>
    public IReadOnlyList<VirtualFolder> All => _folders;

    /// <summary>
    /// A virtual folder was made, renamed or deleted, or its songs changed (then it's the one passed).
    /// </summary>
    public event Action<VirtualFolder?>? Changed;

    void Load()
    {
        LoadFrom(null);
        foreach (var drive in _drives().Where(drive => !IsSystem(drive.Root)))
            LoadFrom(LibraryFile.WithSeparator(drive.Root));
        Sort();
    }

    /// <summary>
    /// Adds the virtual folders kept on <paramref name="drive"/> (null for the app's own folder) that aren't known
    /// yet. Returns how many.
    /// </summary>
    int LoadFrom(string? drive)
    {
        var folder = FolderFor(drive);
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.Exists(folder) ? System.IO.Directory.EnumerateFiles(folder, "*" + EXTENSION).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = [];
        }
        var added = 0;
        foreach (var file in files)
        {
            if (_folders.Any(known => string.Equals(known.FilePath, file, StringComparison.OrdinalIgnoreCase)))
                continue;
            _folders.Add(new VirtualFolder(Path.GetFileNameWithoutExtension(file), file, ReadPlaylist(file), drive));
            added++;
        }
        return added;
    }

    /// <summary>
    /// Looks again for drives come and gone: the virtual folders on a drive just plugged in are added, and those on
    /// one taken out go. Returns whether anything changed, having raised <see cref="Changed"/> if so.
    /// </summary>
    public bool Rescan()
    {
        var present = _drives().Where(drive => !IsSystem(drive.Root)).Select(drive => LibraryFile.WithSeparator(drive.Root)).ToList();
        var gone = _folders.RemoveAll(folder =>
            folder.Drive is { } drive && (!present.Contains(drive, StringComparer.OrdinalIgnoreCase) || !File.Exists(folder.FilePath))
        );
        var added = present.Sum(LoadFrom);
        if (gone == 0 && added == 0)
            return false;
        Sort();
        Changed?.Invoke(null);
        return true;
    }

    void Sort() => _folders.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName, b.DisplayName));

    /// <summary>
    /// The paths in a playlist, in order: each line that isn't blank or a comment, relative ones taken from the
    /// playlist's own folder.
    /// </summary>
    static List<string> ReadPlaylist(string file)
    {
        var paths = new List<string>();
        try
        {
            var folder = Path.GetDirectoryName(file)!;
            foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                var path = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(folder, line));
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return paths;
    }

    /// <summary>
    /// Writes a folder's playlist to a temporary file then swaps it in. Returns why it couldn't, if it couldn't. Kept
    /// on a drive, its songs on that drive are written relative to the playlist, so they're found under any letter.
    /// </summary>
    static string? Save(VirtualFolder folder)
    {
        try
        {
            var directory = Path.GetDirectoryName(folder.FilePath)!;
            System.IO.Directory.CreateDirectory(directory);
            var text = new StringBuilder("#EXTM3U\n");
            foreach (var path in folder.Paths)
            {
                var onDrive = folder.Drive is { } drive && path.StartsWith(drive, StringComparison.OrdinalIgnoreCase);
                text.Append(onDrive ? Path.GetRelativePath(directory, path) : path).Append('\n');
            }
            var temporary = folder.FilePath + ".tmp";
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            File.Move(temporary, folder.FilePath, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// The songs in <paramref name="folder"/> whose files are there, in order.
    /// </summary>
    public IReadOnlyList<Song> SongsIn(VirtualFolder folder)
    {
        var songs = new List<Song>(folder.Paths.Count);
        foreach (var path in folder.Paths)
        {
            // The library can still hold a song whose file has since gone.
            if (Find(path) is { } song && File.Exists(song.Path))
                songs.Add(song);
        }
        return songs;
    }

    /// <summary>
    /// The song at <paramref name="path"/>: the library's, or one browsed to by looking in its folder as the folder
    /// list does; null if the file isn't there.
    /// </summary>
    Song? Find(string path)
    {
        if (_library.Find(path) is { } known)
            return known;
        if (_found.TryGetValue(path, out var found))
            return found;
        if (!File.Exists(path) || Path.GetDirectoryName(path) is not { } directory)
            return null;
        _library.SongsIn(directory);
        if (_library.Find(path) is { } song)
            _found[path] = song;
        return _library.Find(path);
    }

    /// <summary>
    /// Why <paramref name="name"/> can't name a virtual folder (other than <paramref name="except"/>, and where
    /// <paramref name="except"/> is kept), or null if it can: it has to be a file name, and not one already taken
    /// there.
    /// </summary>
    public string? CheckName(string name, VirtualFolder? except = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Give it a name";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.EndsWith('.'))
            return "A name can't have \\ / : * ? \" < > | in it, or end with a full stop";
        return NameTaken(trimmed, except, except?.Drive);
    }

    /// <summary>
    /// Why <paramref name="name"/> is taken on <paramref name="drive"/> (null for the app's own folder) by a virtual
    /// folder other than <paramref name="except"/>, or null if it isn't.
    /// </summary>
    string? NameTaken(string name, VirtualFolder? except, string? drive)
    {
        if (!_folders.Any(folder => folder != except && string.Equals(folder.Drive, drive, StringComparison.OrdinalIgnoreCase) && string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)))
            return null;
        return drive is null ? $"There's already a virtual folder called {name}" : $"There's already a virtual folder called {name} on {drive.TrimEnd('\\', '/')}";
    }

    /// <summary>
    /// A name for a new virtual folder that isn't taken: "New folder", or "New folder 2" and so on.
    /// </summary>
    public string UnusedName(string stem = "New folder")
    {
        var name = stem;
        for (var i = 2; CheckName(name) is not null; i++)
            name = $"{stem} {i}";
        return name;
    }

    /// <summary>
    /// Makes an empty virtual folder, saved straight away. Throws <see cref="InvalidOperationException"/> with the
    /// reason if the name won't do or the file can't be written.
    /// </summary>
    public VirtualFolder Create(string name)
    {
        if (CheckName(name) is { } problem)
            throw new InvalidOperationException(problem);
        name = name.Trim();
        var folder = new VirtualFolder(name, Path.Combine(Directory, name + EXTENSION), [], null);
        if (Save(folder) is { } error)
            throw new InvalidOperationException(error);
        _folders.Add(folder);
        Sort();
        Changed?.Invoke(null);
        return folder;
    }

    /// <summary>
    /// Renames a virtual folder and its file. Throws <see cref="InvalidOperationException"/> with the reason if the
    /// name won't do or the file can't be renamed.
    /// </summary>
    public void Rename(VirtualFolder folder, string name)
    {
        if (CheckName(name, folder) is { } problem)
            throw new InvalidOperationException(problem);
        name = name.Trim();
        if (name == folder.Name)
            return;
        var path = Path.Combine(FolderFor(folder.Drive), name + EXTENSION);
        try
        {
            // A change of case only is still a move on Windows, which File.Move does in place.
            File.Move(folder.FilePath, path);
        }
        catch (FileNotFoundException)
        {
            // Its file went from under it; writing it afresh under the new name keeps what it had.
            folder.FilePath = path;
            if (Save(folder) is { } error)
                throw new InvalidOperationException(error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(e.Message);
        }
        folder.Name = name;
        folder.FilePath = path;
        Sort();
        Changed?.Invoke(null);
    }

    /// <summary>
    /// Moves a virtual folder's playlist to a Cuepoint\VirtualFolders folder on another drive (by its root), or back
    /// to the app's own folder for the system drive. It's written there first, its songs on that drive relative to
    /// it, and only then taken from where it was. Throws <see cref="InvalidOperationException"/> with the reason if
    /// the drive has one of the name already, or it can't be written.
    /// </summary>
    public void MoveToDrive(VirtualFolder folder, string root)
    {
        string? drive = IsSystem(root) ? null : LibraryFile.WithSeparator(root);
        if (string.Equals(drive, folder.Drive, StringComparison.OrdinalIgnoreCase))
            return;
        if (NameTaken(folder.Name, folder, drive) is { } problem)
            throw new InvalidOperationException(problem);
        var path = Path.Combine(FolderFor(drive), folder.Name + EXTENSION);
        if (File.Exists(path))
            throw new InvalidOperationException($"There's already a playlist called {folder.Name} there");

        var (oldPath, oldDrive) = (folder.FilePath, folder.Drive);
        (folder.FilePath, folder.Drive) = (path, drive);
        if (Save(folder) is { } error)
        {
            (folder.FilePath, folder.Drive) = (oldPath, oldDrive);
            throw new InvalidOperationException(error);
        }
        try
        {
            File.Delete(oldPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Written to its new place, but the old copy's still there; it'll show up again until it's removed.
        }
        Sort();
        Changed?.Invoke(null);
    }

    /// <summary>
    /// Whether the drive at <paramref name="root"/> has a playlist of its own called <paramref name="name"/>.
    /// </summary>
    public bool IsOnDrive(string name, string root) => File.Exists(Path.Combine(FolderFor(LibraryFile.WithSeparator(root)), name + EXTENSION));

    /// <summary>
    /// Writes a playlist of <paramref name="paths"/> called <paramref name="name"/> to the drive at
    /// <paramref name="root"/>, those on the drive relative to it, as a virtual folder there: a new one, or in place
    /// of what the one of that name had. Returns why it couldn't, if it couldn't.
    /// </summary>
    public string? WriteToDrive(string name, string root, IReadOnlyList<string> paths)
    {
        var drive = LibraryFile.WithSeparator(root);
        var path = Path.Combine(FolderFor(drive), name + EXTENSION);
        var known = _folders.FirstOrDefault(folder => string.Equals(folder.FilePath, path, StringComparison.OrdinalIgnoreCase));
        var written = new VirtualFolder(name, path, [.. paths], drive);
        if (Save(written) is { } error)
            return error;
        if (known is not null)
        {
            known.EditablePaths.Clear();
            known.EditablePaths.AddRange(paths);
        }
        else
            _folders.Add(written);
        Sort();
        Changed?.Invoke(null);
        return null;
    }

    /// <summary>
    /// Deletes a virtual folder, its playlist file going to the Recycle Bin where there is one. Its songs' own files
    /// aren't touched. Throws <see cref="InvalidOperationException"/> with the reason if the file can't go.
    /// </summary>
    public void Delete(VirtualFolder folder)
    {
        if (File.Exists(folder.FilePath) && RemoveFile(folder.FilePath) is { } error)
            throw new InvalidOperationException(error);
        _folders.Remove(folder);
        Changed?.Invoke(null);
    }

    /// <summary>
    /// How a file is deleted: to the Recycle Bin on Windows, for any slip to be undone. Returns why it couldn't.
    /// </summary>
    public Func<string, string?> RemoveFile { get; set; } = path =>
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    };

    /// <summary>
    /// Adds songs to the end of a virtual folder, leaving out any it has already. Returns how many went in, and why
    /// it couldn't be saved, if it couldn't.
    /// </summary>
    public (int Added, string? Error) Add(VirtualFolder folder, IEnumerable<Song> songs)
    {
        var added = 0;
        foreach (var song in songs)
        {
            if (folder.Contains(song.Path))
                continue;
            folder.EditablePaths.Add(song.Path);
            added++;
        }
        return (added, added > 0 ? Saved(folder) : null);
    }

    /// <summary>
    /// Takes songs out of a virtual folder. Returns why it couldn't be saved, if it couldn't.
    /// </summary>
    public string? Remove(VirtualFolder folder, IEnumerable<Song> songs)
    {
        var paths = songs.Select(song => song.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return folder.EditablePaths.RemoveAll(paths.Contains) > 0 ? Saved(folder) : null;
    }

    /// <summary>
    /// Moves songs in a virtual folder to just before the one listed at <paramref name="before"/> among those whose
    /// files are there (or to the end, at their count), keeping their order. Songs whose files are missing keep their
    /// places among the rest. Returns why it couldn't be saved, if it couldn't.
    /// </summary>
    public string? Move(VirtualFolder folder, IReadOnlyCollection<Song> songs, int before)
    {
        var listed = SongsIn(folder).ToList();
        var moving = songs.Select(song => song.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        before = Math.Clamp(before, 0, listed.Count);

        // Where the moved songs go among the rest: before the first song at or after the drop that isn't moving.
        var anchor = listed.Skip(before).FirstOrDefault(song => !moving.Contains(song.Path));
        var moved = listed.Where(song => moving.Contains(song.Path)).ToList();
        var reordered = listed.Where(song => !moving.Contains(song.Path)).ToList();
        var at = anchor is null ? reordered.Count : reordered.IndexOf(anchor);
        reordered.InsertRange(at, moved);
        if (reordered.SequenceEqual(listed))
            return null;

        // The listed songs take the listed songs' slots, in their new order; missing ones stay where they were.
        var slots = folder.Paths.Select((path, i) => (path, i)).Where(entry => listed.Any(song => string.Equals(song.Path, entry.path, StringComparison.OrdinalIgnoreCase))).Select(entry => entry.i).ToList();
        for (var i = 0; i < slots.Count; i++)
            folder.EditablePaths[slots[i]] = reordered[i].Path;
        return Saved(folder);
    }

    /// <summary>
    /// Points every virtual folder listing a song in <paramref name="moved"/> (by where it was) at where it is now,
    /// keeping its place. Returns why one couldn't be saved, if one couldn't.
    /// </summary>
    public string? Relocate(IReadOnlyDictionary<string, string> moved)
    {
        string? problem = null;
        foreach (var folder in _folders)
        {
            if (!folder.Paths.Any(moved.ContainsKey))
                continue;
            var paths = folder.Paths.Select(path => moved.GetValueOrDefault(path, path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            folder.EditablePaths.Clear();
            folder.EditablePaths.AddRange(paths);
            problem ??= Saved(folder);
        }
        return problem;
    }

    string? Saved(VirtualFolder folder)
    {
        var error = Save(folder);
        Changed?.Invoke(folder);
        return error;
    }
}
