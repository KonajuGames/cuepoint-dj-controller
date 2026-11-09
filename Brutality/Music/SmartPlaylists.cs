namespace Brutality.Music;

/// <summary>
/// A saved search, listing whichever songs match it now.
/// </summary>
sealed class SmartPlaylist
{
    public string Name { get; set; } = "";

    /// <summary>
    /// What it looks for, written as in the song list's search box.
    /// </summary>
    public string Search { get; set; } = "";
}

/// <summary>
/// The smart playlists, kept in the app's settings. Each looks through the library and every other song the database
/// knows about, on any drive that's plugged in, whether or not its folder has been opened this run. Those other songs
/// are looked for in the background the first time one's listed, and now and then while one is. UI thread only.
/// </summary>
sealed class SmartPlaylists(
    List<SmartPlaylist> lists,
    MusicLibrary library,
    LibraryDatabase database,
    LibraryAnalyser analyser,
    Func<IEnumerable<string>>? driveRoots = null
)
{
    /// <summary>
    /// How long before the songs known to the database are looked for again.
    /// </summary>
    static readonly TimeSpan KnownRefresh = TimeSpan.FromMinutes(1);

    readonly Func<IEnumerable<string>> _driveRoots = driveRoots ?? (() => VirtualFolders.ReadyDrives().Select(drive => drive.Root));

    IReadOnlyList<Song> _known = [];
    DateTime _knownAt = DateTime.MinValue;
    bool _loading;

    public IReadOnlyList<SmartPlaylist> All => lists;

    /// <summary>
    /// One was made, renamed, changed or deleted.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Why <paramref name="name"/> can't name a smart playlist (other than <paramref name="except"/>), or null if it
    /// can.
    /// </summary>
    public string? CheckName(string name, SmartPlaylist? except = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Give it a name";
        return lists.Any(list => list != except && string.Equals(list.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ? $"There's already a smart playlist called {trimmed}"
            : null;
    }

    /// <summary>
    /// Why <paramref name="search"/> won't do for a smart playlist, or null if it will: it has to ask for something.
    /// </summary>
    public static string? CheckSearch(string search) => SongQuery.Parse(search).IsEmpty ? "Say what it should find, such as rating:4 120-126" : null;

    public SmartPlaylist Add(string name, string search)
    {
        var list = new SmartPlaylist { Name = name.Trim(), Search = search.Trim() };
        lists.Add(list);
        lists.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        Changed?.Invoke();
        return list;
    }

    public void Rename(SmartPlaylist list, string name)
    {
        list.Name = name.Trim();
        lists.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        Changed?.Invoke();
    }

    public void SetSearch(SmartPlaylist list, string search)
    {
        list.Search = search.Trim();
        Changed?.Invoke();
    }

    public void Delete(SmartPlaylist list)
    {
        lists.Remove(list);
        Changed?.Invoke();
    }

    /// <summary>
    /// The songs that match <paramref name="list"/> now, of those found so far.
    /// </summary>
    public IReadOnlyList<Song> SongsIn(SmartPlaylist list)
    {
        LoadKnown();
        analyser.LookUp(library.Songs);
        var query = SongQuery.Parse(list.Search);
        return library.Songs.Concat(_known).Distinct().Where(query.Matches).ToList();
    }

    void LoadKnown()
    {
        if (_loading || DateTime.UtcNow - _knownAt < KnownRefresh)
            return;
        _loading = true;
        database.OpenDriveFiles(_driveRoots());
        library.LoadSongsAt(
            database.KnownPaths(),
            songs =>
            {
                analyser.LookUp(songs);
                (_known, _knownAt, _loading) = (songs, DateTime.UtcNow, false);
            }
        );
    }
}
