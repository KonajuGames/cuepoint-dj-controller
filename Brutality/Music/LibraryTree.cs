using Brutality.Audio;
using Brutality.Audio.Analysis;

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
}

/// <summary>
/// One entry in the browser's folder list: what it's called, what's under it (worked out when first asked, since a
/// drive has a great many folders), and the songs it shows when chosen. UI thread only.
/// </summary>
sealed class LibraryNode
{
    readonly Func<IEnumerable<LibraryNode>>? _children;
    readonly Func<IReadOnlyList<Song>>? _songs;
    List<LibraryNode>? _loadedChildren;
    IReadOnlyList<Song>? _folderSongs;

    readonly string _name;

    /// <summary>
    /// What it's called: for a virtual folder, whatever the folder is called now.
    /// </summary>
    public string Name => Folder?.DisplayName ?? _name;

    public LibraryNodeKind Kind { get; }

    /// <summary>
    /// The virtual folder, for one.
    /// </summary>
    public VirtualFolder? Folder { get; init; }

    /// <summary>
    /// The folder on disk, for a drive or folder.
    /// </summary>
    public string? Path { get; init; }

    public LibraryNode? Parent { get; private set; }

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

    public IReadOnlyList<LibraryNode> Children
    {
        get
        {
            if (_loadedChildren is null)
            {
                _loadedChildren = _children?.Invoke().ToList() ?? [];
                foreach (var child in _loadedChildren)
                    child.Parent = this;
            }
            return _loadedChildren;
        }
    }

    /// <summary>
    /// Forgets its children, to be asked for again next time, after some have been added or taken away.
    /// </summary>
    public void Refresh() => _loadedChildren = null;

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
    /// The songs to list when it's chosen. A folder's or drive's are read once; the rest are asked for each time,
    /// since they change (a virtual folder edited, say).
    /// </summary>
    public IReadOnlyList<Song> Songs =>
        _songs is null ? []
        : Kind is LibraryNodeKind.Folder or LibraryNodeKind.Drive ? _folderSongs ??= _songs()
        : _songs();
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

    public static List<LibraryNode> Build(MusicLibrary library, IReadOnlyList<Deck> decks, IReadOnlyList<Song> history, VirtualFolders virtualFolders)
    {
        var songs = library.Songs;
        IReadOnlyList<Song> Where(Func<Song, bool> test) => songs.Where(test).ToList();

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
                    node = new LibraryNode(folder.Name, LibraryNodeKind.VirtualFolder, () => virtualFolders.SongsIn(folder)) { Folder = folder };
                    folderNodes[folder] = node;
                }
                yield return node;
            }
        }

        var nodes = new List<LibraryNode>
        {
            new("Music library", LibraryNodeKind.Library, () => songs) { Path = library.Root },
            new("Local drives", LibraryNodeKind.Group, children: () => Drives(library)),
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
                        () => songs.OrderByDescending(song => song.Modified).Take(RECENTLY_ADDED).ToList()
                    ),
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
        return nodes;

        LibraryNode TempoFilter(string name, double from, double to) =>
            new(name, LibraryNodeKind.Filter, () => Where(song => song.Analysis is { } a && a.Bpm >= from && a.Bpm < to));
    }

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
    /// looking only along the way; null if it isn't there now (a drive taken out, or a folder gone).
    /// </summary>
    public static LibraryNode? Find(IReadOnlyList<LibraryNode> roots, IReadOnlyList<string> path)
    {
        LibraryNode? found = null;
        var nodes = roots;
        foreach (var name in path)
        {
            found = nodes.FirstOrDefault(node => node.Name == name);
            if (found is null)
                return null;
            nodes = found.Children;
        }
        return found;
    }

    static IEnumerable<LibraryNode> Drives(MusicLibrary library)
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
            yield return Folder(library, drive.RootDirectory.FullName, label, LibraryNodeKind.Drive);
        }
    }

    static LibraryNode Folder(MusicLibrary library, string path, string name, LibraryNodeKind kind) =>
        new(name, kind, () => library.SongsIn(path), () => Subfolders(library, path)) { Path = path };

    /// <summary>
    /// The folders in <paramref name="path"/> that can be opened, leaving out hidden and system ones.
    /// </summary>
    static IEnumerable<LibraryNode> Subfolders(MusicLibrary library, string path)
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
        return folders.Select(folder => Folder(library, folder.FullName, folder.Name, LibraryNodeKind.Folder));
    }
}
