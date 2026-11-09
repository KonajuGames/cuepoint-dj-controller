using Brutality.Audio;
using Brutality.Audio.Analysis;
using Brutality.Music.Imports;

namespace Brutality.Music;

enum LibraryNodeKind
{
    /// <summary>
    /// The library's own folder, every song in it.
    /// </summary>
    Library,

    /// <summary>
    /// A heading that only groups the nodes under it.
    /// </summary>
    Group,

    Drive,
    Folder,

    /// <summary>
    /// Songs gathered from anywhere by what's happened, such as what's been played.
    /// </summary>
    List,

    /// <summary>
    /// The heading over the smart playlists, where new ones are made.
    /// </summary>
    SmartPlaylists,

    /// <summary>
    /// The heading over the virtual folders, where new ones are made.
    /// </summary>
    VirtualFolders,

    /// <summary>
    /// Songs put together by hand, in an order, saved as a playlist: see <see cref="Music.VirtualFolder"/>.
    /// </summary>
    VirtualFolder,

    /// <summary>
    /// The library's songs that match a test, worked out afresh as songs are analysed and decks change.
    /// </summary>
    Filter,

    /// <summary>
    /// A playlist from other DJ software, a Rekordbox playlist or a Serato crate: its songs, in its order, those whose
    /// files are here.
    /// </summary>
    Imported,
}

/// <summary>
/// One entry in the browser's folder list: what it's called, what's under it (worked out when first asked, since a
/// drive has a great many folders), and the songs it shows when chosen. A folder's or a drive's songs, and what's
/// under it, can be looked for in the background, so a big folder or a slow drive never holds the screen up (see
/// <see cref="LoadSongs"/> and <see cref="LoadChildren"/>). UI thread only.
/// </summary>
sealed class LibraryNode
{
    readonly Func<IEnumerable<LibraryNode>>? _children;
    readonly Func<IReadOnlyList<Song>>? _songs;
    List<LibraryNode>? _loadedChildren;
    IReadOnlyList<Song>? _folderSongs;

    // Whether the songs kept are out of date: a list that's changed keeps showing them until its new ones are in.
    bool _songsStale;

    // How many times each has been asked for in the background, so a look begun before a refresh is passed over.
    int _songLoads;
    int _childLoads;

    readonly string _name;

    /// <summary>
    /// What it's called: for a virtual folder, whatever the folder is called now.
    /// </summary>
    public string Name => Folder?.DisplayName ?? Smart?.Name ?? _name;

    public LibraryNodeKind Kind { get; }

    /// <summary>
    /// The virtual folder, for one.
    /// </summary>
    public VirtualFolder? Folder { get; init; }

    /// <summary>
    /// The saved search, for a smart playlist.
    /// </summary>
    public SmartPlaylist? Smart { get; init; }

    /// <summary>
    /// The folder on disk, for a drive or folder.
    /// </summary>
    public string? Path { get; init; }

    public LibraryNode? Parent { get; private set; }

    /// <summary>
    /// Looks for a folder's or drive's songs in the background, then hands them on, on the UI thread; without it,
    /// they're looked for there and then.
    /// </summary>
    public Action<Action<IReadOnlyList<Song>>>? LoadSongs { get; init; }

    /// <summary>
    /// The paths of the songs a virtual folder or imported playlist lists, whether their files are there or not.
    /// </summary>
    public Func<IReadOnlyList<string>>? SongPaths { get; init; }

    /// <summary>
    /// Whether it's a list that's looked for again each time it's listed, as its songs and their files can change.
    /// </summary>
    public bool IsLoadedList => SongPaths is not null && LoadSongs is not null;

    /// <summary>
    /// Looks for what's under it in the background, then hands it on, on the UI thread, for
    /// <see cref="ChildrenInBackground"/>.
    /// </summary>
    public Action<Action<IReadOnlyList<LibraryNode>>>? LoadChildren { get; init; }

    /// <summary>
    /// Whether its songs are being looked for in the background, so there are none to show yet.
    /// </summary>
    public bool IsLoadingSongs { get; private set; }

    /// <summary>
    /// Whether what's under it is being looked for in the background.
    /// </summary>
    public bool IsLoadingChildren { get; private set; }

    /// <summary>
    /// Its songs have come in from the background, to list.
    /// </summary>
    public event Action<LibraryNode>? SongsLoaded;

    public LibraryNode(
        string name,
        LibraryNodeKind kind,
        Func<IReadOnlyList<Song>>? songs = null,
        Func<IEnumerable<LibraryNode>>? children = null
    )
    {
        _name = name;
        Kind = kind;
        _songs = songs;
        _children = children;
    }

    /// <summary>
    /// Whether it may have children: always for a heading or a folder not yet looked in, otherwise whether it has.
    /// </summary>
    public bool CanExpand => _children is not null && (_loadedChildren is null || _loadedChildren.Count > 0);

    /// <summary>
    /// What's under it, looked for there and then if it hasn't been, as finding a folder by its path wants.
    /// </summary>
    public IReadOnlyList<LibraryNode> Children
    {
        get
        {
            if (_loadedChildren is null)
                TakeChildren(_children?.Invoke().ToList() ?? []);
            return _loadedChildren!;
        }
    }

    /// <summary>
    /// What's under it, as the folder list shows it: looked for in the background the first time, and none meanwhile
    /// (see <see cref="IsLoadingChildren"/>).
    /// </summary>
    public IReadOnlyList<LibraryNode> ChildrenInBackground
    {
        get
        {
            if (_loadedChildren is not null || LoadChildren is not { } load)
                return Children;
            if (!IsLoadingChildren)
            {
                IsLoadingChildren = true;
                var asked = ++_childLoads;
                load(children =>
                {
                    // Passed over if it's been refreshed since, or looked for there and then meanwhile.
                    if (asked != _childLoads || _loadedChildren is not null)
                        return;
                    IsLoadingChildren = false;
                    TakeChildren([.. children]);
                });
            }
            return [];
        }
    }

    void TakeChildren(List<LibraryNode> children)
    {
        _loadedChildren = children;
        IsLoadingChildren = false;
        foreach (var child in children)
            child.Parent = this;
    }

    /// <summary>
    /// Forgets its children, to be asked for again next time, after some have been added or taken away.
    /// </summary>
    public void Refresh()
    {
        _loadedChildren = null;
        IsLoadingChildren = false;
        _childLoads++;
    }

    /// <summary>
    /// Forgets a folder's or drive's songs, to be read again next time, after how it's listed has changed.
    /// </summary>
    public void RefreshSongs()
    {
        if (!IsLoadedList)
            _folderSongs = null;
        _songsStale = true;
        IsLoadingSongs = false;
        _songLoads++;
    }

    /// <summary>
    /// Forgets the songs of this folder and every folder below it already looked in that's under
    /// <paramref name="folder"/>, after what's there has changed. Folders not yet looked in are left unlooked in.
    /// </summary>
    public void RefreshSongsUnder(string folder)
    {
        if (Path is { } path && LibraryFile.WithSeparator(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            RefreshSongs();
        foreach (var child in _loadedChildren ?? [])
            child.RefreshSongsUnder(folder);
    }

    /// <summary>
    /// Whether choosing it shows songs; a heading doesn't.
    /// </summary>
    public bool HasSongs => _songs is not null;

    /// <summary>
    /// Whether its songs change by themselves (as songs are analysed, or played), so a list showing them should
    /// ask again now and then. A folder's are read once.
    /// </summary>
    public bool IsLive => Kind is LibraryNodeKind.Filter or LibraryNodeKind.List;

    /// <summary>
    /// The songs to list when it's chosen. A folder's or drive's are read once, in the background where it can be
    /// (none meanwhile, and <see cref="SongsLoaded"/> once they're in); the rest are asked for each time, since they
    /// change (a virtual folder edited, say).
    /// </summary>
    public IReadOnlyList<Song> Songs
    {
        get
        {
            if (_songs is null)
                return [];
            if (Kind is not (LibraryNodeKind.Folder or LibraryNodeKind.Drive) && !IsLoadedList)
                return _songs();
            if (_folderSongs is not null && !_songsStale)
                return _folderSongs;
            if (LoadSongs is not { } load)
            {
                _songsStale = false;
                return _folderSongs = _songs();
            }
            if (!IsLoadingSongs)
            {
                IsLoadingSongs = true;
                var asked = ++_songLoads;
                load(songs =>
                {
                    if (asked != _songLoads)
                        return;
                    (_folderSongs, IsLoadingSongs, _songsStale) = (songs, false, false);
                    SongsLoaded?.Invoke(this);
                });
            }
            return _folderSongs ?? [];
        }
    }
}

/// <summary>
/// Builds the browser's folder list: the library, the computer's drives and their folders, the virtual folders, a
/// few lists, and filters over the library.
/// </summary>
static class LibraryTree
{
    /// <summary>
    /// How far a song's tempo can be from a deck's to count as compatible, either way, allowing for half and double
    /// time.
    /// </summary>
    const double COMPATIBLE_TEMPO = 0.06;

    const int RECENTLY_ADDED = 100;

    /// <param name="isRecursive">Whether a folder lists the songs in its subfolders too, by its path.</param>
    /// <param name="imports">Other DJ software's playlists, under a node of their own for each that has any.</param>
    /// <param name="smart">The saved searches, under a heading of their own.</param>
    /// <param name="duplicates">The songs last found to have exact copies, for a list of them.</param>
    public static List<LibraryNode> Build(
        MusicLibrary library,
        IReadOnlyList<Deck> decks,
        IReadOnlyList<Song> history,
        VirtualFolders virtualFolders,
        Func<string, bool> isRecursive,
        ImportedLibraries? imports = null,
        SmartPlaylists? smart = null,
        Func<IReadOnlyList<Song>>? duplicates = null
    )
    {
        // The library's songs as they are when asked for, since its folder can be changed.
        IReadOnlyList<Song> Where(Func<Song, bool> test) => library.Songs.Where(test).ToList();

        // One node for each virtual folder for as long as it lasts, so a renamed one stays selected and open.
        var folderNodes = new Dictionary<VirtualFolder, LibraryNode>();
        IEnumerable<LibraryNode> VirtualFolderNodes()
        {
            foreach (var gone in folderNodes.Keys.Except(virtualFolders.All).ToList())
                folderNodes.Remove(gone);
            foreach (var folder in virtualFolders.All)
            {
                if (!folderNodes.TryGetValue(folder, out var node))
                {
                    node = new LibraryNode(folder.Name, LibraryNodeKind.VirtualFolder, () => virtualFolders.SongsIn(folder))
                    {
                        Folder = folder,
                        SongPaths = () => folder.Paths,
                        LoadSongs = done => library.LoadSongsAt(folder.Paths, done),
                    };
                    folderNodes[folder] = node;
                }
                yield return node;
            }
        }

        // One node for each smart playlist too, so a renamed one stays selected.
        var smartNodes = new Dictionary<SmartPlaylist, LibraryNode>();
        IEnumerable<LibraryNode> SmartNodes()
        {
            foreach (var list in smart!.All)
            {
                if (!smartNodes.TryGetValue(list, out var node))
                    smartNodes[list] = node = new LibraryNode(list.Name, LibraryNodeKind.Filter, () => smart.SongsIn(list))
                    {
                        Smart = list,
                        SongPaths = () => smart.SongsIn(list).Select(song => song.Path).ToList(),
                    };
                yield return node;
            }
        }

        var nodes = new List<LibraryNode>
        {
            new("Music library", LibraryNodeKind.Library, () => library.Songs),
            // The drives are asked whether they're ready in the background: a network drive gone quiet can take a while.
            new("Local drives", LibraryNodeKind.Group, children: () => Drives(library, isRecursive))
            {
                LoadChildren = done => library.Background.Start(() => Drives(library, isRecursive).ToList(), done, () => done([])),
            },
            new("Virtual folders", LibraryNodeKind.VirtualFolders, children: VirtualFolderNodes) { Path = virtualFolders.Directory },
            new(
                "Lists",
                LibraryNodeKind.Group,
                children: () =>
                [
                    new("History", LibraryNodeKind.List, () => history.Reverse().Distinct().ToList()),
                    new(
                        "Recently added",
                        LibraryNodeKind.List,
                        () => library.Songs.OrderByDescending(song => song.Modified).Take(RECENTLY_ADDED).ToList()
                    ),
                    new("Duplicates", LibraryNodeKind.List, () => duplicates?.Invoke() ?? []),
                ]
            ),
            new(
                "Filters",
                LibraryNodeKind.Group,
                children: () =>
                [
                    .. decks.Select(deck => new LibraryNode(
                        $"Compatible with deck {deck.Name}",
                        LibraryNodeKind.Filter,
                        () => Where(song => IsCompatible(song, deck))
                    )),
                    new(
                        "Tempo",
                        LibraryNodeKind.Group,
                        children: () =>
                        [
                            TempoFilter("Under 100 BPM", 0, 100),
                            TempoFilter("100 to 115 BPM", 100, 115),
                            TempoFilter("115 to 125 BPM", 115, 125),
                            TempoFilter("125 to 135 BPM", 125, 135),
                            TempoFilter("135 BPM and up", 135, double.MaxValue),
                        ]
                    ),
                    new(
                        "Variable tempo",
                        LibraryNodeKind.Filter,
                        () => Where(song => song.Analysis?.Grid.IsVariable == true)
                    ),
                    new("Not analysed yet", LibraryNodeKind.Filter, () => Where(song => song.Analysis is null)),
                ]
            ),
        };
        if (smart is not null)
            nodes.Insert(nodes.FindIndex(node => node.Name == "Lists"), new("Smart playlists", LibraryNodeKind.SmartPlaylists, children: SmartNodes));
        // Other DJ software's playlists, after Cuepoint's own.
        nodes.InsertRange(nodes.FindIndex(node => node.Kind == LibraryNodeKind.VirtualFolders) + 1, ImportedNodes(library, imports));
        return nodes;

        LibraryNode TempoFilter(string name, double from, double to) =>
            new(name, LibraryNodeKind.Filter, () => Where(song => song.Analysis is { } a && a.Bpm >= from && a.Bpm < to));
    }

    /// <summary>
    /// A "Rekordbox" node, with each collection under it and its playlists under that, and a "Serato" node, with its
    /// crates, for each that has anything.
    /// </summary>
    public static IEnumerable<LibraryNode> ImportedNodes(MusicLibrary library, ImportedLibraries? imports)
    {
        if (imports is null)
            yield break;

        if (imports.Rekordbox.Count > 0)
            yield return RekordboxRoot(library, imports);

        if (imports.Serato.Count > 0)
        {
            // The crates straight under "Serato", or under each drive's name if there's more than one drive's.
            var drives = imports.Serato;
            yield return new(
                "Serato",
                LibraryNodeKind.Group,
                children: () =>
                    drives.Count == 1
                        ? CrateNodes(library, drives[0].Crates, [])
                        : drives.Select(drive => new LibraryNode(drive.Drive, LibraryNodeKind.Group, children: () => CrateNodes(library, drive.Crates, [])))
            );
        }
    }

    /// <summary>
    /// The "Rekordbox" node, with each collection under it as it is when it's opened (or refreshed, after one's
    /// imported or changed), and its playlists under that.
    /// </summary>
    public static LibraryNode RekordboxRoot(MusicLibrary library, ImportedLibraries imports)
    {
        // Each collection by its file's name, and its folder's too where two share a name.
        string Name(RekordboxCollection collection)
        {
            var name = Path.GetFileName(collection.FilePath);
            var shared = imports.Rekordbox.Count(other => string.Equals(Path.GetFileName(other.FilePath), name, StringComparison.OrdinalIgnoreCase)) > 1;
            return shared ? $"{name} ({Path.GetFileName(Path.GetDirectoryName(collection.FilePath))})" : name;
        }
        return new(
            "Rekordbox",
            LibraryNodeKind.Group,
            children: () =>
                imports.Rekordbox.Select(collection => new LibraryNode(
                    Name(collection),
                    LibraryNodeKind.Group,
                    children: () => collection.Playlists.Select(node => RekordboxNode(library, node))
                )).ToList()
        );
    }

    static LibraryNode RekordboxNode(MusicLibrary library, RekordboxNode node) =>
        node.IsFolder
            ? new(node.Name, LibraryNodeKind.Group, children: () => node.Children.Select(child => RekordboxNode(library, child)))
            : new(node.Name, LibraryNodeKind.Imported, () => SongsAt(library, node.Paths))
            {
                SongPaths = () => node.Paths,
                LoadSongs = done => library.LoadSongsAt(node.Paths, done),
            };

    /// <summary>
    /// The crates directly inside the crate at <paramref name="parents"/> (the top, for none), in order. One with
    /// crates inside it opens to them; one known only by the crates inside it has no songs of its own.
    /// </summary>
    static IEnumerable<LibraryNode> CrateNodes(MusicLibrary library, IReadOnlyList<SeratoCrate> crates, string[] parents)
    {
        bool Under(SeratoCrate crate, string[] path) => crate.Parents.Count >= path.Length && crate.Parents.Take(path.Length).SequenceEqual(path);
        var names = crates
            .Where(crate => Under(crate, parents))
            .Select(crate => crate.Parents.Count == parents.Length ? crate.Name : crate.Parents[parents.Length])
            .Distinct()
            .ToList();
        foreach (var name in names)
        {
            string[] path = [.. parents, name];
            var own = crates.FirstOrDefault(crate => crate.Parents.Count == parents.Length && Under(crate, parents) && crate.Name == name);
            var inside = crates.Any(crate => Under(crate, path));
            yield return new LibraryNode(
                name,
                own is null ? LibraryNodeKind.Group : LibraryNodeKind.Imported,
                own is null ? null : () => SongsAt(library, own.Paths),
                inside ? () => CrateNodes(library, crates, path) : null
            )
            {
                SongPaths = own is null ? null : () => own.Paths,
                LoadSongs = own is null ? null : done => library.LoadSongsAt(own.Paths, done),
            };
        }
    }

    /// <summary>
    /// The songs at <paramref name="paths"/>, in order, those whose files are here.
    /// </summary>
    static IReadOnlyList<Song> SongsAt(MusicLibrary library, IEnumerable<string> paths) => paths.Select(library.SongAt).OfType<Song>().ToList();

    /// <summary>
    /// Whether <paramref name="song"/> mixes with what's coming out of <paramref name="deck"/>: a key that mixes in
    /// harmony, and a tempo close to the deck's where it's playing now, both as its tempo fader has moved them.
    /// </summary>
    public static bool IsCompatible(Song song, Deck deck)
    {
        if (song.Analysis is not { } analysis || deck.Bpm is not { } deckBpm || deck.Key is not { } deckKey)
            return false;
        if (string.Equals(song.Path, deck.FilePath, StringComparison.OrdinalIgnoreCase))
            return false;

        var ratio = analysis.Bpm / deckBpm;
        var closest = new[] { ratio, ratio * 2, ratio / 2 }.MinBy(r => Math.Abs(Math.Log(r)));
        return Math.Abs(closest - 1) <= COMPATIBLE_TEMPO && analysis.Key.MixesWith(deckKey);
    }

    /// <summary>
    /// The names from the top of the list down to <paramref name="node"/>, to find it again by.
    /// </summary>
    public static IReadOnlyList<string> PathOf(LibraryNode node)
    {
        var names = new List<string>();
        for (var at = node; at is not null; at = at.Parent)
            names.Insert(0, at.Name);
        return names;
    }

    /// <summary>
    /// The node at the end of <paramref name="path"/> (from <see cref="PathOf"/>) among <paramref name="roots"/>,
    /// looking only along the way; null if it isn't there now (a drive taken out, or a folder gone). A virtual folder on
    /// a removable drive is still found when the drive comes back under another letter, as long as it's the only one by
    /// that name.
    /// </summary>
    public static LibraryNode? Find(IReadOnlyList<LibraryNode> roots, IReadOnlyList<string> path)
    {
        LibraryNode? found = null;
        var nodes = roots;
        foreach (var name in path)
        {
            found = nodes.FirstOrDefault(node => node.Name == name);
            if (found is null)
            {
                var moved = nodes.Where(node => node.Folder is { Drive: not null } folder && name.StartsWith(folder.Name + " (", StringComparison.Ordinal)).Take(2).ToList();
                if (moved.Count != 1)
                    return null;
                found = moved[0];
            }
            nodes = found.Children;
        }
        return found;
    }

    static IEnumerable<LibraryNode> Drives(MusicLibrary library, Func<string, bool> isRecursive)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            string label;
            try
            {
                ready = drive.IsReady;
                label = ready && drive.VolumeLabel.Length > 0 ? $"{drive.Name.TrimEnd('\\')} {drive.VolumeLabel}" : drive.Name.TrimEnd('\\');
            }
            catch (IOException)
            {
                continue;
            }
            if (!ready)
                continue;
            yield return Folder(library, isRecursive, drive.RootDirectory.FullName, label, LibraryNodeKind.Drive);
        }
    }

    /// <summary>
    /// A folder or drive, its songs and its folders looked for in the background, so a big one, a recursive one or a
    /// slow drive doesn't hold the screen up.
    /// </summary>
    public static LibraryNode Folder(MusicLibrary library, Func<string, bool> isRecursive, string path, string name, LibraryNodeKind kind) =>
        new(name, kind, () => library.SongsIn(path, isRecursive(path)), () => Subfolders(library, isRecursive, path))
        {
            Path = path,
            LoadSongs = done =>
            {
                // Whether it's recursive is asked now, on the UI thread; the files are listed in the background.
                var recursive = isRecursive(path);
                library.Background.Start(() => MusicLibrary.ListFolder(path, recursive), files => done(library.SongsIn(path, files, recursive)), () => done([]));
            },
            LoadChildren = done => library.Background.Start(() => Subfolders(library, isRecursive, path).ToList(), done, () => done([])),
        };

    /// <summary>
    /// The folders in <paramref name="path"/> that can be opened, leaving out hidden and system ones.
    /// </summary>
    static IEnumerable<LibraryNode> Subfolders(MusicLibrary library, Func<string, bool> isRecursive, string path)
    {
        List<DirectoryInfo> folders;
        try
        {
            folders = new DirectoryInfo(path)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
        return folders.Select(folder => Folder(library, isRecursive, folder.FullName, folder.Name, LibraryNodeKind.Folder));
    }
}
