using System.Text;

namespace Brutality.Music;

/// <summary>
/// A list of songs kept in the order they were put in, saved as a playlist file of their paths. A song is in it at
/// most once. Songs whose files have gone stay in the file, so they come back if the file does, but aren't listed.
/// </summary>
sealed class VirtualFolder
{
    readonly List<string> _paths;

    internal VirtualFolder(string name, string filePath, List<string> paths)
    {
        Name = name;
        FilePath = filePath;
        _paths = paths;
    }

    public string Name { get; internal set; }

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
/// The virtual folders: every playlist (.m3u8) in one folder on disk, one file each, named for the virtual folder. A
/// playlist written elsewhere and copied in shows up as one too. Every change is saved straight away. UI thread only.
/// </summary>
sealed class VirtualFolders
{
    public const string EXTENSION = ".m3u8";

    readonly MusicLibrary _library;
    readonly List<VirtualFolder> _folders = [];

    // Songs found for paths outside the library, so a folder of them doesn't look on disk each time it's listed.
    readonly Dictionary<string, Song> _found = new(StringComparer.OrdinalIgnoreCase);

    public VirtualFolders(MusicLibrary library, string directory)
    {
        _library = library;
        Directory = directory;
        Load();
    }

    /// <summary>
    /// Where the playlist files are kept.
    /// </summary>
    public string Directory { get; }

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
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.Exists(Directory) ? System.IO.Directory.EnumerateFiles(Directory, "*" + EXTENSION).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = [];
        }
        foreach (var file in files)
            _folders.Add(new VirtualFolder(Path.GetFileNameWithoutExtension(file), file, ReadPlaylist(file)));
        Sort();
    }

    void Sort() => _folders.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));

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
    /// Writes a folder's playlist to a temporary file then swaps it in. Returns why it couldn't, if it couldn't.
    /// </summary>
    static string? Save(VirtualFolder folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(folder.FilePath)!);
            var text = new StringBuilder("#EXTM3U\n");
            foreach (var path in folder.Paths)
                text.Append(path).Append('\n');
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
    /// Why <paramref name="name"/> can't name a virtual folder (other than <paramref name="except"/>), or null if it
    /// can: it has to be a file name, and not one already taken.
    /// </summary>
    public string? CheckName(string name, VirtualFolder? except = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Give it a name";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.EndsWith('.'))
            return "A name can't have \\ / : * ? \" < > | in it, or end with a full stop";
        if (_folders.Any(folder => folder != except && string.Equals(folder.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"There's already a virtual folder called {trimmed}";
        return null;
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
        var folder = new VirtualFolder(name, Path.Combine(Directory, name + EXTENSION), []);
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
        var path = Path.Combine(Directory, name + EXTENSION);
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

    string? Saved(VirtualFolder folder)
    {
        var error = Save(folder);
        Changed?.Invoke(folder);
        return error;
    }
}
