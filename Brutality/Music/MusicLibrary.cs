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
/// thread a frame at a time.
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
    /// How many songs have their tags so far.
    /// </summary>
    public int TaggedCount { get; private set; }

    public bool IsReadingTags => TaggedCount < Songs.Count && _reader is { IsCompleted: false };

    readonly Dictionary<string, Song> _byPath = new(StringComparer.OrdinalIgnoreCase);
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
            _byPath = Songs.ToDictionary(song => song.Path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Songs = [];
            Error = e.Message;
        }

        if (OperatingSystem.IsWindows() && Songs.Count > 0)
            _reader = StartReadingTags(_stop.Token);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    Task StartReadingTags(CancellationToken stop) => Task.Run(() => ReadAllTags(stop));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    void ReadAllTags(CancellationToken stop)
    {
        foreach (var song in Songs)
        {
            if (stop.IsCancellationRequested)
                return;
            _readTags.Enqueue((song, WindowsTags.Read(song.Path)));
        }
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
        }
        TaggedCount += applied;
        return applied;
    }

    /// <summary>
    /// The song at <paramref name="path"/>, if it's in the library.
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
    }
}
