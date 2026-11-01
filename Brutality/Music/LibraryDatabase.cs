using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// What's been learnt about the library's songs, kept between runs in a JSON file: each song's analysis, or why it
/// couldn't be analysed, with the file size and time it was made from so a changed file gets analysed again. A beat
/// grid is stored as its markers, each a beat time and the tempo from there on. Hotcues are kept apart from the
/// analysis, since they're the DJ's own work: analysing again, or a newer analysis, leaves them be. Only used from
/// the UI thread.
/// </summary>
sealed class LibraryDatabase
{
    /// <summary>
    /// Bump this when analysis changes enough that earlier results should be redone.
    /// </summary>
    public const int ANALYSIS_VERSION = 3;

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
    /// Why the file couldn't be read, if it was there but couldn't be; the database starts empty instead.
    /// </summary>
    public string? LoadError { get; }

    public int Count => _tracks.Count;

    readonly Dictionary<string, TrackRecord> _tracks;
    readonly Dictionary<string, double?[]> _hotcues;

    LibraryDatabase(
        string filePath,
        Dictionary<string, TrackRecord> tracks,
        Dictionary<string, double?[]> hotcues,
        string? loadError
    )
    {
        FilePath = filePath;
        _tracks = new Dictionary<string, TrackRecord>(tracks, StringComparer.OrdinalIgnoreCase);
        _hotcues = new Dictionary<string, double?[]>(hotcues, StringComparer.OrdinalIgnoreCase);
        LoadError = loadError;
    }

    /// <summary>
    /// Reads the database at <paramref name="filePath"/>, or starts an empty one if there isn't one yet or it can't
    /// be read (see <see cref="LoadError"/>). Nothing is written until <see cref="Save"/>.
    /// </summary>
    public static LibraryDatabase Load(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath))
            return new LibraryDatabase(filePath, [], [], null);

        try
        {
            using var stream = File.OpenRead(filePath);
            var file = JsonSerializer.Deserialize<DatabaseFile>(stream, JsonOptions);
            if (file is null || file.Version != FILE_VERSION)
                return new LibraryDatabase(filePath, [], [], $"Unrecognised database version {file?.Version}");
            return new LibraryDatabase(filePath, file.Tracks ?? [], file.Hotcues ?? [], null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LibraryDatabase(filePath, [], [], e.Message);
        }
    }

    /// <summary>
    /// What's known about <paramref name="song"/>, if it was found from the file as it is now with the current
    /// analysis: its analysis, or the error analysing it gave.
    /// </summary>
    public bool TryGet(Song song, out TrackAnalysis? analysis, out string? error)
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

    public void Set(Song song, TrackAnalysis analysis) =>
        _tracks[song.Path] = new TrackRecord
        {
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
        path = Path.GetFullPath(path);
        if (hotcues.All(at => at is null))
            _hotcues.Remove(path);
        else
            _hotcues[path] = hotcues.Select(at => at is { } t ? Math.Round(t.TotalSeconds, 3) : (double?)null).ToArray();
    }

    public void SetFailed(Song song, string error) =>
        _tracks[song.Path] = new TrackRecord
        {
            Size = song.Size,
            Modified = song.Modified,
            AnalysisVersion = ANALYSIS_VERSION,
            Error = error,
        };

    /// <summary>
    /// Writes the database out. Written to a temporary file then swapped in, so a crash or full disk mid-write
    /// leaves the last good copy rather than half a file. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if it can't.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, new DatabaseFile { Version = FILE_VERSION, Tracks = _tracks, Hotcues = _hotcues }, JsonOptions);
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
    }

    sealed class TrackRecord
    {
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
