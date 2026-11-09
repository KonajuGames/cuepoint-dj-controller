using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// One library.json: what's been learnt about some of the library's songs (see <see cref="LibraryDatabase"/>, which
/// keeps one for each drive). Each song's analysis, or why it couldn't be analysed, with the file size and time it was
/// made from so a changed file gets analysed again, and its title, artist and album from its tags, so they're known
/// before the tags are read again. A beat grid is stored as its markers, each a beat time and the
/// tempo from there on. Hotcues, memory cues, and beat grids fixed by hand, are kept apart from the analysis, since they're the
/// DJ's own work: analysing again, or a newer analysis, leaves them be, and a song's fixed grid is given in place of
/// the analysed one wherever its analysis is.
///
/// A file with a <see cref="Root"/> (one at the root of a drive) keeps its songs' paths relative to it, so the drive
/// can come back under another letter, or on another computer, and its songs still be found. Only used from the UI
/// thread.
/// </summary>
sealed class LibraryFile
{
    const int ANALYSIS_VERSION = LibraryDatabase.ANALYSIS_VERSION;

    const int FILE_VERSION = 1;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Paths stay readable, with their ampersands and accents, rather than escaped for embedding in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }

    /// <summary>
    /// The folder its songs' paths are kept relative to, ending in a separator; null if they're kept whole.
    /// </summary>
    public string? Root { get; }

    /// <summary>
    /// Why the file couldn't be read, if it was there but couldn't be; it starts empty instead.
    /// </summary>
    public string? LoadError { get; }

    public int Count => _tracks.Count;

    /// <summary>
    /// Whether anything's changed since it was read or last saved.
    /// </summary>
    public bool Dirty { get; private set; }

    /// <summary>
    /// Whether it knows nothing: no analyses, hotcues, memory cues or fixed grids.
    /// </summary>
    public bool IsEmpty => _tracks.Count == 0 && _hotcues.Count == 0 && _memoryCues.Count == 0 && _grids.Count == 0;

    /// <summary>
    /// Every song it knows anything about, by its full path.
    /// </summary>
    public IEnumerable<string> Paths =>
        _tracks.Keys.Concat(_hotcues.Keys).Concat(_memoryCues.Keys).Concat(_grids.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, TrackRecord> _tracks;
    readonly Dictionary<string, double?[]> _hotcues;
    readonly Dictionary<string, double[]> _memoryCues;
    readonly Dictionary<string, GridRecord> _grids;

    LibraryFile(
        string filePath,
        string? root,
        Dictionary<string, TrackRecord> tracks,
        Dictionary<string, double?[]> hotcues,
        Dictionary<string, double[]> memoryCues,
        Dictionary<string, GridRecord> grids,
        string? loadError
    )
    {
        FilePath = filePath;
        Root = root;
        _tracks = Whole(tracks);
        _hotcues = Whole(hotcues);
        _memoryCues = Whole(memoryCues);
        _grids = Whole(grids);
        LoadError = loadError;
    }

    /// <summary>
    /// A folder's full path ending in exactly one separator: "F:\" for a drive's root, "C:\Music\" for a folder.
    /// </summary>
    public static string WithSeparator(string folder)
    {
        var full = Path.GetFullPath(folder);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Records keyed by full path, from keys as they're kept in the file. A file found keeping whole paths under its
    /// root (written before they were kept relative) is marked to be written again, relative.
    /// </summary>
    Dictionary<string, T> Whole<T>(Dictionary<string, T> kept)
    {
        var whole = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, record) in kept)
        {
            if (Root is not null && Path.IsPathRooted(path) && path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                Dirty = true;
            whole[Root is null || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path))] = record;
        }
        return whole;
    }

    /// <summary>
    /// Records keyed as they're kept in the file: relative to its root, if it has one and they're under it.
    /// </summary>
    Dictionary<string, T> Kept<T>(Dictionary<string, T> whole)
    {
        if (Root is null)
            return whole;
        var kept = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, record) in whole)
            kept[path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? path[Root.Length..] : path] = record;
        return kept;
    }

    /// <summary>
    /// Reads the file at <paramref name="filePath"/>, or starts an empty one if there isn't one yet or it can't be
    /// read (see <see cref="LoadError"/>). With a <paramref name="root"/>, its songs' paths are relative to it.
    /// Nothing is written until <see cref="Save"/>.
    /// </summary>
    public static LibraryFile Load(string filePath, string? root = null)
    {
        filePath = Path.GetFullPath(filePath);
        if (root is not null)
            root = WithSeparator(root);
        if (!File.Exists(filePath))
            return new LibraryFile(filePath, root, [], [], [], [], null);

        try
        {
            using var stream = File.OpenRead(filePath);
            var file = JsonSerializer.Deserialize<DatabaseFile>(stream, JsonOptions);
            if (file is null || file.Version != FILE_VERSION)
                return new LibraryFile(filePath, root, [], [], [], [], $"Unrecognised database version {file?.Version}");
            return new LibraryFile(filePath, root, file.Tracks ?? [], file.Hotcues ?? [], file.MemoryCues ?? [], file.Grids ?? [], null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LibraryFile(filePath, root, [], [], [], [], e.Message);
        }
    }

    /// <summary>
    /// Moves what it knows about the songs <paramref name="which"/> picks into <paramref name="other"/>; where the
    /// other already knows something, it's kept unless <paramref name="overwrite"/>. Returns how many songs moved.
    /// </summary>
    public int MoveTo(LibraryFile other, Func<string, bool> which, bool overwrite)
    {
        var moving = Paths.Where(which).ToList();
        void Move<T>(Dictionary<string, T> from, Dictionary<string, T> to)
        {
            foreach (var path in moving)
            {
                if (from.Remove(path, out var record) && (overwrite || !to.ContainsKey(path)))
                    to[path] = record;
            }
        }
        Move(_tracks, other._tracks);
        Move(_hotcues, other._hotcues);
        Move(_memoryCues, other._memoryCues);
        Move(_grids, other._grids);
        if (moving.Count > 0)
            Dirty = other.Dirty = true;
        return moving.Count;
    }

    /// <summary>
    /// What's known about <paramref name="song"/>, if it was found from the file as it is now with the current
    /// analysis: its analysis (with its beat grid as fixed by hand, if it was), or the error analysing it gave.
    /// </summary>
    public bool TryGet(Song song, out TrackAnalysis? analysis, out string? error)
    {
        var found = TryGetAnalysed(song, out analysis, out error);
        if (analysis is not null)
            analysis = WithEdits(song.Path, analysis);
        return found;
    }

    /// <summary>
    /// <see cref="TryGet"/>, but with the beat grid as analysed, whether or not it was fixed by hand since.
    /// </summary>
    public bool TryGetAnalysed(Song song, out TrackAnalysis? analysis, out string? error)
    {
        (analysis, error) = (null, null);
        if (
            !_tracks.TryGetValue(song.Path, out var record)
            || record.Size != song.Size
            || record.Modified != song.Modified
            || record.AnalysisVersion != ANALYSIS_VERSION
        )
            return false;

        if (record.Error is { } recorded)
        {
            error = recorded;
            return true;
        }
        if (MusicalKey.Parse(record.Key) is not { } key || record.Grid is not { Count: > 0 } markers)
            return false;
        try
        {
            var grid = new BeatGrid(markers.Select(marker => new GridMarker(marker.Time, marker.Bpm)).ToList(), record.Duration)
            {
                DownbeatOffset = record.Downbeat,
            };
            analysis = new TrackAnalysis(grid, record.Loudness, record.Peak, key);
            return true;
        }
        catch (ArgumentException)
        {
            // Markers out of order: edited by hand, perhaps. Analysing again puts it right.
            return false;
        }
    }

    /// <summary>
    /// The title, artist and album saved for <paramref name="song"/> from its tags, if the file hasn't changed since;
    /// any of them can be null where the tags didn't say.
    /// </summary>
    public bool TryGetNames(Song song, out string? title, out string? artist, out string? album)
    {
        (title, artist, album) = (null, null, null);
        if (!_tracks.TryGetValue(song.Path, out var record) || record.Size != song.Size || record.Modified != song.Modified)
            return false;
        (title, artist, album) = (record.Title, record.Artist, record.Album);
        return title is not null || artist is not null || album is not null;
    }

    /// <summary>
    /// Keeps <paramref name="song"/>'s title, artist and album, just read from its tags, with its analysis, if it has
    /// one for the file as it is.
    /// </summary>
    public void SetNames(Song song)
    {
        if (!_tracks.TryGetValue(song.Path, out var record) || record.Size != song.Size || record.Modified != song.Modified)
            return;
        var names = Names(song, record);
        if (names == (record.Title, record.Artist, record.Album))
            return;
        (record.Title, record.Artist, record.Album) = names;
        Dirty = true;
    }

    /// <summary>
    /// What to keep of <paramref name="song"/>'s title, artist and album: its own once its tags are read, otherwise
    /// whatever <paramref name="kept"/> had, rather than names guessed from the file's path.
    /// </summary>
    static (string? Title, string? Artist, string? Album) Names(Song song, TrackRecord? kept)
    {
        static string? OrNull(string text) => text.Length > 0 ? text : null;
        return song.HasTags ? (OrNull(song.Title), OrNull(song.Artist), OrNull(song.Album)) : (kept?.Title, kept?.Artist, kept?.Album);
    }

    public void Set(Song song, TrackAnalysis analysis)
    {
        Dirty = true;
        var (title, artist, album) = Names(song, _tracks.GetValueOrDefault(song.Path));
        _tracks[song.Path] = new TrackRecord
        {
            Title = title,
            Artist = artist,
            Album = album,
            Size = song.Size,
            Modified = song.Modified,
            AnalysisVersion = ANALYSIS_VERSION,
            Bpm = analysis.Bpm,
            Duration = Math.Round(analysis.Grid.Duration, 3),
            Downbeat = analysis.Grid.DownbeatOffset,
            Grid = analysis.Grid.Markers.Select(marker => new MarkerRecord { Time = Math.Round(marker.Time, 4), Bpm = marker.Bpm }).ToList(),
            Loudness = Math.Round(analysis.Loudness, 2),
            Peak = analysis.Peak,
            Key = analysis.Key.Name,
        };
    }

    /// <summary>
    /// The hotcues saved for the song at <paramref name="path"/>, in seconds; empty if there are none.
    /// </summary>
    public IReadOnlyList<TimeSpan?> GetHotcues(string path) =>
        _hotcues.TryGetValue(Path.GetFullPath(path), out var seconds)
            ? seconds.Select(at => at is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null).ToList()
            : [];

    /// <summary>
    /// Keeps <paramref name="hotcues"/> for the song at <paramref name="path"/>, or forgets them if none is set.
    /// </summary>
    public void SetHotcues(string path, IReadOnlyList<TimeSpan?> hotcues)
    {
        Dirty = true;
        path = Path.GetFullPath(path);
        if (hotcues.All(at => at is null))
            _hotcues.Remove(path);
        else
            _hotcues[path] = hotcues.Select(at => at is { } t ? Math.Round(t.TotalSeconds, 3) : (double?)null).ToArray();
    }

    /// <summary>
    /// The song at <paramref name="path"/>'s memory cues, in order through it; none if it has none.
    /// </summary>
    public IReadOnlyList<TimeSpan> GetMemoryCues(string path) =>
        _memoryCues.TryGetValue(Path.GetFullPath(path), out var seconds) ? seconds.Select(TimeSpan.FromSeconds).ToList() : [];

    /// <summary>
    /// Keeps <paramref name="cues"/> as the song at <paramref name="path"/>'s memory cues, in order, or forgets them
    /// if there are none.
    /// </summary>
    public void SetMemoryCues(string path, IReadOnlyList<TimeSpan> cues)
    {
        Dirty = true;
        path = Path.GetFullPath(path);
        if (cues.Count == 0)
            _memoryCues.Remove(path);
        else
            _memoryCues[path] = cues.Select(at => Math.Round(at.TotalSeconds, 3)).Distinct().Order().ToArray();
    }

    /// <summary>
    /// <paramref name="analysis"/> of the song at <paramref name="path"/>, with its beat grid as fixed by hand if it
    /// was.
    /// </summary>
    public TrackAnalysis WithEdits(string path, TrackAnalysis analysis) =>
        GetGridEdit(path, analysis.Grid.Duration) is { } edit ? analysis with { Grid = edit.Grid } : analysis;

    /// <summary>
    /// The beat grid fixed by hand for the song at <paramref name="path"/>, with its pins, if it was;
    /// <paramref name="duration"/> is the song's length, for the grid. Null if it wasn't, or what's saved won't make a
    /// grid.
    /// </summary>
    public GridEdit? GetGridEdit(string path, double duration)
    {
        if (!_grids.TryGetValue(Path.GetFullPath(path), out var record) || record.Markers is not { Count: > 0 } markers)
            return null;
        try
        {
            var grid = new BeatGrid(markers.Select(marker => new GridMarker(marker.Time, marker.Bpm)).ToList(), duration)
            {
                DownbeatOffset = record.Downbeat,
            };
            return new GridEdit(grid, record.Pins ?? []);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the beat grid fixed by hand for the song at <paramref name="path"/>, or forgets it (going back to the
    /// analysed one) with null.
    /// </summary>
    public void SetGridEdit(string path, GridEdit? edit)
    {
        Dirty = true;
        path = Path.GetFullPath(path);
        if (edit is null)
            _grids.Remove(path);
        else
            _grids[path] = new GridRecord
            {
                Markers = edit.Grid.Markers.Select(marker => new MarkerRecord { Time = Math.Round(marker.Time, 5), Bpm = Math.Round(marker.Bpm, 4) }).ToList(),
                Downbeat = edit.Grid.DownbeatOffset,
                Pins = edit.Pins.Select(pin => Math.Round(pin, 5)).ToArray(),
            };
    }

    public bool HasGridEdit(string path) => _grids.ContainsKey(Path.GetFullPath(path));

    public void SetFailed(Song song, string error)
    {
        Dirty = true;
        var (title, artist, album) = Names(song, _tracks.GetValueOrDefault(song.Path));
        _tracks[song.Path] = new TrackRecord
        {
            Title = title,
            Artist = artist,
            Album = album,
            Size = song.Size,
            Modified = song.Modified,
            AnalysisVersion = ANALYSIS_VERSION,
            Error = error,
        };
    }

    /// <summary>
    /// Writes the file out, making its folder if need be. Written to a temporary file then swapped in, so a crash or
    /// full disk mid-write leaves the last good copy rather than half a file. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if it can't.
    /// </summary>
    public void Save() => Snapshot()();

    /// <summary>
    /// What's in it now, and how to write that out, which can be done on another thread while this carries on being
    /// changed. Counts as saved from here on; if the writing throws, <see cref="MarkDirty"/> says it isn't.
    /// </summary>
    public Action Snapshot()
    {
        var contents = new DatabaseFile
        {
            Version = FILE_VERSION,
            Tracks = Copy(_tracks),
            Hotcues = Copy(_hotcues),
            MemoryCues = _memoryCues.Count > 0 ? Copy(_memoryCues) : null,
            Grids = _grids.Count > 0 ? Copy(_grids) : null,
        };
        Dirty = false;
        return () => Write(contents);
    }

    public void MarkDirty() => Dirty = true;

    Dictionary<string, T> Copy<T>(Dictionary<string, T> whole) => new(Kept(whole), StringComparer.OrdinalIgnoreCase);

    void Write(DatabaseFile contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, contents, JsonOptions);
        File.Move(temporary, FilePath, overwrite: true);
    }

    sealed class DatabaseFile
    {
        public int Version { get; set; }
        public Dictionary<string, TrackRecord>? Tracks { get; set; }

        /// <summary>
        /// Each song's hotcues in seconds, null where one isn't set; songs without any are left out.
        /// </summary>
        public Dictionary<string, double?[]>? Hotcues { get; set; }

        /// <summary>
        /// Each song's memory cues in seconds, in order; songs without any are left out.
        /// </summary>
        public Dictionary<string, double[]>? MemoryCues { get; set; }

        /// <summary>
        /// Each song's beat grid as fixed by hand; songs left as analysed are left out.
        /// </summary>
        public Dictionary<string, GridRecord>? Grids { get; set; }
    }

    sealed class GridRecord
    {
        public List<MarkerRecord>? Markers { get; set; }
        public int Downbeat { get; set; }

        /// <summary>
        /// Where beats were pinned by hand, in seconds.
        /// </summary>
        public double[]? Pins { get; set; }
    }

    sealed class TrackRecord
    {
        /// <summary>
        /// The song's title, artist and album from its tags; missing where they don't say, or haven't been read.
        /// </summary>
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }

        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public int AnalysisVersion { get; set; }
        /// <summary>
        /// The main tempo, for anyone reading the file; the grid is what's used.
        /// </summary>
        public double Bpm { get; set; }

        public double Duration { get; set; }
        public List<MarkerRecord>? Grid { get; set; }

        /// <summary>
        /// Which beat of the grid's counting starts a bar, from 0 to 3.
        /// </summary>
        public int Downbeat { get; set; }
        public double Loudness { get; set; }
        public float Peak { get; set; }
        public string? Key { get; set; }
        public string? Error { get; set; }
    }

    sealed class MarkerRecord
    {
        public double Time { get; set; }
        public double Bpm { get; set; }
    }
}
