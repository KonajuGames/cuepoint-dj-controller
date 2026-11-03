using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Brutality.Audio;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// One audio file in the library. Starts with details guessed from its folder and file name, then gets its real
/// tags once the library's background reader reaches it. Only changed on the UI thread.
/// </summary>
sealed class Song
{
    public string Path { get; }

    /// <summary>
    /// The file's size and when it was last written, as of the scan: if either changes, an analysis saved for it is
    /// out of date.
    /// </summary>
    public long Size { get; }

    public DateTime Modified { get; }

    public string Title { get; internal set; }
    public string Artist { get; internal set; }
    public string Album { get; internal set; }
    public TimeSpan? Duration { get; internal set; }

    /// <summary>
    /// Whether <see cref="Title"/> and the rest come from the file's tags yet, rather than its name.
    /// </summary>
    public bool HasTags { get; internal set; }

    /// <summary>
    /// Whether it's under the library's folder, rather than found browsing elsewhere.
    /// </summary>
    public bool InLibrary { get; internal set; }

    /// <summary>
    /// Its beat grid, loudness and key, once analysed.
    /// </summary>
    public TrackAnalysis? Analysis { get; internal set; }

    /// <summary>
    /// Why it couldn't be analysed, if it couldn't; it isn't tried again until the file changes.
    /// </summary>
    public string? AnalysisError { get; internal set; }

    internal Song(string path, long size, DateTime modified, string title, string artist, string album)
    {
        Path = path;
        Size = size;
        Modified = modified;
        Title = title;
        Artist = artist;
        Album = album;
    }
}

/// <summary>
/// The audio files in a folder, and in its subfolders too if asked. Scanning the folder is quick, so it happens up front; reading every file's tags
/// takes a while, so that happens on a worker thread, and <see cref="ApplyReadTags"/> brings the results onto the UI
/// thread a frame at a time. Songs in other folders can be had too, with <see cref="SongsIn"/>, for browsing; their
/// tags are read ahead of the library's.
/// </summary>
sealed partial class MusicLibrary : IDisposable
{
    /// <summary>
    /// What a deck can decode: SDL_mixer's formats, plus what Media Foundation adds on Windows.
    /// </summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(
        [".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".wma"],
        StringComparer.OrdinalIgnoreCase
    );

    public string Root { get; private set; } = "";

    /// <summary>
    /// Whether the library takes in the songs in <see cref="Root"/>'s subfolders too, rather than only those directly
    /// in it.
    /// </summary>
    public bool Recursive { get; private set; }

    /// <summary>
    /// Every song, in folder order: by artist folder, then album, then track.
    /// </summary>
    public IReadOnlyList<Song> Songs { get; private set; } = [];

    /// <summary>
    /// Why the folder couldn't be scanned, if it couldn't.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// How many of the library's songs have their tags so far.
    /// </summary>
    public int TaggedCount { get; private set; }

    public bool IsReadingTags => TaggedCount < Songs.Count && _reader is { IsCompleted: false };

    /// <summary>
    /// A song's tags were read, and are on it now; raised from <see cref="ApplyReadTags"/>.
    /// </summary>
    public event Action<Song>? Tagged;

    /// <summary>
    /// Every song known, in the library or not, by path. UI thread only.
    /// </summary>
    readonly Dictionary<string, Song> _byPath = new(StringComparer.OrdinalIgnoreCase);

    // Songs waiting for their tags: those found browsing first, since someone's looking at them.
    readonly ConcurrentQueue<Song> _browsedToTag = new();
    readonly ConcurrentQueue<Song> _libraryToTag = new();
    readonly SemaphoreSlim _toTag = new(0);

    readonly ConcurrentQueue<(Song Song, SongTags Tags)> _readTags = new();
    readonly CancellationTokenSource _stop = new();
    readonly Task? _reader;

    /// <summary>
    /// The library's folder as last listed, on a thread of its own: its audio files, which of the songs known before
    /// have gone, and why it couldn't be listed, if it couldn't.
    /// </summary>
    sealed record Listing(string Root, List<FileInfo> Files, HashSet<string> Gone, string? Error);

    // The listing under way in the background, if any, and what to do once it's taken in.
    Task<Listing>? _listing;
    Action? _listed;

    /// <summary>
    /// Whether the library's folder is being looked through in the background (see <see cref="BeginScan"/>).
    /// </summary>
    public bool IsScanning => _listing is not null;

    /// <summary>
    /// Folders being looked through in the background for the folder list, to take in a frame at a time.
    /// </summary>
    public BackgroundWork Background { get; } = new();

    /// <param name="scan">Whether to look through the folder now, as a test wants; or not, leaving it empty until
    /// <see cref="BeginScan"/>, as the app does, to look through it in the background.</param>
    public MusicLibrary(string root, bool recursive = false, bool scan = true)
    {
        if (OperatingSystem.IsWindows())
            _reader = StartReadingTags(_stop.Token);
        if (scan)
            Scan(root, recursive);
        else
            (Root, Recursive) = (root, recursive);
    }

    /// <summary>
    /// Makes <paramref name="root"/> the library's folder, scanning it now, and its subfolders too if
    /// <paramref name="recursive"/>; or scans it again, after it's changed. Songs already known keep their tags,
    /// unless their files have changed since, when they're new songs, to read and analyse afresh; songs whose files
    /// have gone are forgotten. The old folder's songs are still there to browse to, just no longer in the library.
    /// Waits for the folder to be looked through: see <see cref="BeginScan"/> for not. A scan under way is
    /// overtaken. UI thread only.
    /// </summary>
    public void Scan(string root, bool recursive = false)
    {
        (_listing, _listed) = (null, null);
        (Root, Recursive) = (root, recursive);
        TakeIn(List(root, recursive, Songs.Select(song => song.Path).ToList()));
    }

    /// <summary>
    /// <see cref="Scan"/>, but looking through the folder on a thread of its own, so a big library doesn't hold
    /// anything up: <see cref="Root"/> and <see cref="Recursive"/> are the new ones straight away, and the songs the
    /// old ones until <see cref="ApplyScan"/> takes the new ones in, then does <paramref name="scanned"/>. One begun
    /// before it's taken in is overtaken, its songs never taken in. UI thread only.
    /// </summary>
    public void BeginScan(string root, bool recursive = false, Action? scanned = null)
    {
        (Root, Recursive) = (root, recursive);
        var known = Songs.Select(song => song.Path).ToList();
        _listed = scanned;
        _listing = Task.Run(() => List(root, recursive, known));
    }

    /// <summary>
    /// Takes in the songs <see cref="BeginScan"/> found, if it's finished, and does what was to be done then. Call
    /// once a frame from the UI thread; returns whether it took them in.
    /// </summary>
    public bool ApplyScan()
    {
        if (_listing is not { IsCompleted: true } listing)
            return false;
        var listed = _listed;
        (_listing, _listed) = (null, null);
        TakeIn(listing.GetAwaiter().GetResult());
        listed?.Invoke();
        return true;
    }

    /// <summary>
    /// Lists the audio files in <paramref name="root"/>, in path order, and which of <paramref name="known"/> have
    /// gone. Touches nothing of the library's, so it's safe on any thread.
    /// </summary>
    static Listing List(string root, bool recursive, IReadOnlyList<string> known)
    {
        List<FileInfo> files;
        string? error = null;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
            // FileInfos from enumerating already carry the size and time, so this costs no more than listing names.
            files = new DirectoryInfo(root)
                .EnumerateFiles("*", options)
                .Where(file => Extensions.Contains(file.Extension))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            files = [];
            error = e.Message;
        }
        var found = files.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = known.Where(path => !found.Contains(path) && !File.Exists(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Listing(root, files, gone, error);
    }

    /// <summary>
    /// Makes the library the songs in <paramref name="listing"/>, keeping the songs known already whose files haven't
    /// changed, and queues those without tags for reading.
    /// </summary>
    void TakeIn(Listing listing)
    {
        var before = Songs;
        foreach (var song in before)
            song.InLibrary = false;
        _libraryToTag.Clear();

        Error = listing.Error;
        var songs = listing
            .Files.Select(file =>
                _byPath.GetValueOrDefault(file.FullName) is { } known && known.Size == file.Length && known.Modified == file.LastWriteTimeUtc
                    ? known
                    : Guess(listing.Root, file)
            )
            .ToList();

        foreach (var song in songs)
        {
            song.InLibrary = true;
            _byPath[song.Path] = song;
        }
        foreach (var gone in before)
        {
            if (!gone.InLibrary && listing.Gone.Contains(gone.Path) && _byPath.GetValueOrDefault(gone.Path) == gone)
                _byPath.Remove(gone.Path);
        }
        Songs = songs;
        TaggedCount = songs.Count(song => song.HasTags);

        // Songs browsed to and waiting for tags may be in the new library too, so they're read twice; that's harmless.
        if (_reader is not null)
        {
            var untagged = 0;
            foreach (var song in songs.Where(song => !song.HasTags))
            {
                _libraryToTag.Enqueue(song);
                untagged++;
            }
            if (untagged > 0)
                _toTag.Release(untagged);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    Task StartReadingTags(CancellationToken stop) => Task.Run(() => ReadTags(stop));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    void ReadTags(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                _toTag.Wait(stop);
                if (_browsedToTag.TryDequeue(out var song) || _libraryToTag.TryDequeue(out song))
                    _readTags.Enqueue((song, WindowsTags.Read(song.Path)));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// The audio files directly in <paramref name="folder"/>, or anywhere under it if <paramref name="recursive"/>, in
    /// path order: the library's own songs where they're in it, and new ones (named from the file until their tags
    /// arrive) where they aren't. Empty if the folder can't be read. UI thread only.
    /// </summary>
    public IReadOnlyList<Song> SongsIn(string folder, bool recursive = false) => SongsIn(folder, ListFolder(folder, recursive), recursive);

    /// <summary>
    /// The audio files directly in <paramref name="folder"/>, or anywhere under it if <paramref name="recursive"/>, in
    /// path order; none if it can't be read. Touches nothing of the library's, so it's safe on any thread, to look
    /// through a big folder in the background and take its songs in with <see cref="SongsIn(string, IReadOnlyList{FileInfo}, bool)"/>.
    /// </summary>
    public static IReadOnlyList<FileInfo> ListFolder(string folder, bool recursive = false)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true })
                .Where(file => Extensions.Contains(file.Extension))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// The songs for <paramref name="files"/>, listed from <paramref name="folder"/>, as <see cref="SongsIn(string, bool)"/>
    /// makes them. UI thread only.
    /// </summary>
    public IReadOnlyList<Song> SongsIn(string folder, IReadOnlyList<FileInfo> files, bool recursive = false)
    {
        var songs = new List<Song>(files.Count);
        foreach (var file in files)
        {
            if (!_byPath.TryGetValue(file.FullName, out var song))
            {
                // Found from further up, the folders on the way down name the artist and album, as in the library.
                song = Guess(recursive ? folder : file.DirectoryName ?? folder, file);
                _byPath[song.Path] = song;
                if (OperatingSystem.IsWindows())
                {
                    _browsedToTag.Enqueue(song);
                    _toTag.Release();
                }
            }
            songs.Add(song);
        }
        return songs;
    }

    /// <summary>
    /// Puts tags read since the last call onto their songs. Call once a frame from the UI thread; returns how many
    /// songs changed.
    /// </summary>
    public int ApplyReadTags()
    {
        var applied = 0;
        while (_readTags.TryDequeue(out var read))
        {
            var (song, tags) = read;
            if (tags.Title is { } title)
                song.Title = title;
            if (tags.Artist is { } artist)
                song.Artist = artist;
            if (tags.Album is { } album)
                song.Album = album;
            song.Duration = tags.Duration;
            song.HasTags = true;
            applied++;
            if (song.InLibrary)
                TaggedCount++;
            Tagged?.Invoke(song);
        }
        return applied;
    }

    /// <summary>
    /// The song at <paramref name="path"/>, if it's in the library or has been browsed to.
    /// </summary>
    public Song? Find(string path) => _byPath.GetValueOrDefault(System.IO.Path.GetFullPath(path));

    /// <summary>
    /// The song at <paramref name="path"/>, wherever it is: one known already, or else found by looking in its folder,
    /// as browsing there would. Null if there's no such file, or it isn't one a deck can play. UI thread only.
    /// </summary>
    public Song? SongAt(string path)
    {
        if (Find(path) is { } known)
            return known;
        if (!File.Exists(path) || System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is not { } folder)
            return null;
        SongsIn(folder);
        return Find(path);
    }

    /// <summary>
    /// A first guess from where the file is, for the list to show before its tags arrive. Assumes the common
    /// "Artist/Album/01 Title.ext" layout, skipping folders like "_CDs" that group rather than name an artist.
    /// </summary>
    static Song Guess(string root, FileInfo file)
    {
        var path = file.FullName;
        var folders = System.IO.Path.GetRelativePath(root, System.IO.Path.GetDirectoryName(path) ?? root)
            .Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Where(folder => folder is not ("." or "") && !folder.StartsWith('_'))
            .ToList();

        var artist = folders.Count > 0 ? folders[0] : "";
        var album = folders.Count > 1 ? folders[^1] : "";

        // "01 Title", "01. Title", "01 - Title", and "Artist - Title" all come down to "Title".
        var title = LeadingTrackNumber().Replace(System.IO.Path.GetFileNameWithoutExtension(path), "");
        if (artist.Length > 0 && title.StartsWith(artist + " - ", StringComparison.OrdinalIgnoreCase))
            title = title[(artist.Length + 3)..];

        return new Song(path, file.Length, file.LastWriteTimeUtc, title, artist, album);
    }

    [GeneratedRegex(@"^\d{1,3}\s*[.\-_)]?\s+")]
    private static partial Regex LeadingTrackNumber();

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _reader?.Wait();
        }
        catch (AggregateException)
        {
            // Stopping is all that matters here.
        }
        _stop.Dispose();
        _toTag.Dispose();
    }
}
