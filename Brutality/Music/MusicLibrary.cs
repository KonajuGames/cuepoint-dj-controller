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

    /// <summary>
    /// Whether every word of <paramref name="query"/> appears in the title, artist or album, ignoring case.
    /// </summary>
    public bool Matches(string query)
    {
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (
                !Title.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !Artist.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !Album.Contains(word, StringComparison.OrdinalIgnoreCase)
            )
                return false;
        }
        return true;
    }
}

/// <summary>
/// The audio files under a folder. Scanning the folder is quick, so it happens up front; reading every file's tags
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

    public string Root { get; }

    /// <summary>
    /// Every song, in folder order: by artist folder, then album, then track.
    /// </summary>
    public IReadOnlyList<Song> Songs { get; }

    /// <summary>
    /// Why the folder couldn't be scanned, if it couldn't.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// How many of the library's songs have their tags so far.
    /// </summary>
    public int TaggedCount { get; private set; }

    public bool IsReadingTags => TaggedCount < Songs.Count && _reader is { IsCompleted: false };

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

    public MusicLibrary(string root)
    {
        Root = root;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            // FileInfos from enumerating already carry the size and time, so this costs no more than listing names.
            Songs = new DirectoryInfo(root)
                .EnumerateFiles("*", options)
                .Where(file => Extensions.Contains(file.Extension))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(file => Guess(root, file))
                .ToList();
            foreach (var song in Songs)
            {
                song.InLibrary = true;
                _byPath[song.Path] = song;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Songs = [];
            Error = e.Message;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var song in Songs)
                _libraryToTag.Enqueue(song);
            if (Songs.Count > 0)
                _toTag.Release(Songs.Count);
            _reader = StartReadingTags(_stop.Token);
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
    /// The audio files directly in <paramref name="folder"/>, in name order: the library's own songs where they're
    /// in it, and new ones (named from the file until their tags arrive) where they aren't. Empty if the folder can't
    /// be read. UI thread only.
    /// </summary>
    public IReadOnlyList<Song> SongsIn(string folder)
    {
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder)
                .EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true })
                .Where(file => Extensions.Contains(file.Extension))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        var songs = new List<Song>(files.Count);
        foreach (var file in files)
        {
            if (!_byPath.TryGetValue(file.FullName, out var song))
            {
                song = Guess(file.DirectoryName ?? folder, file);
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
        }
        return applied;
    }

    /// <summary>
    /// The song at <paramref name="path"/>, if it's in the library or has been browsed to.
    /// </summary>
    public Song? Find(string path) => _byPath.GetValueOrDefault(System.IO.Path.GetFullPath(path));

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
