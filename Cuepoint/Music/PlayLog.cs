using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cuepoint.Music;

/// <summary>
/// A song played in a set, and when it started.
/// </summary>
sealed record PlayedSong(DateTime Started, string Path);

/// <summary>
/// One session's set: when Cuepoint was started for it, and every song played out in it, in order.
/// </summary>
sealed record PlayedSet(DateTime Started, IReadOnlyList<PlayedSong> Songs)
{
    /// <summary>
    /// What to call it in the folder list: its day and time, and how many songs.
    /// </summary>
    [JsonIgnore]
    public string Name =>
        string.Create(CultureInfo.CurrentCulture, $"{Started:ddd d MMM yyyy, HH:mm} ({Songs.Count} {(Songs.Count == 1 ? "song" : "songs")})");
}

/// <summary>
/// Every set played, kept between runs: a file for each session in a folder of its own, written as each song counts as
/// played (see <see cref="SessionPlays"/>), so a crash loses nothing. Sessions with nothing played aren't kept.
/// </summary>
sealed class PlayLog
{
    readonly string _folder;
    readonly List<PlayedSet> _past;
    readonly Dictionary<string, DateTime> _lastPlayed = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Reads the sets kept in <paramref name="folder"/>; a file that can't be read is passed over.
    /// </summary>
    public PlayLog(string folder)
    {
        _folder = folder;
        _past = [];
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<PlayedSet>(File.ReadAllText(file)) is { Songs.Count: > 0 } set)
                        _past.Add(set);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Half written, or not one of ours: passed over.
                }
            }
        }
        _past.Sort((a, b) => b.Started.CompareTo(a.Started));
        foreach (var song in _past.SelectMany(set => set.Songs))
            Played(song);
    }

    /// <summary>
    /// The sets kept from before this session, newest first.
    /// </summary>
    public IReadOnlyList<PlayedSet> Past => _past;

    /// <summary>
    /// When the song at <paramref name="path"/> was last played, in any set kept or this one so far; null if never.
    /// </summary>
    public DateTime? LastPlayed(string path) => _lastPlayed.TryGetValue(path, out var when) ? when : null;

    void Played(PlayedSong song)
    {
        if (!_lastPlayed.TryGetValue(song.Path, out var when) || song.Started > when)
            _lastPlayed[song.Path] = song.Started;
    }

    /// <summary>
    /// Keeps this session's set, begun at <paramref name="started"/>, as it is now, over what was kept of it before; with
    /// nothing in it, it's no longer kept. Returns why it couldn't be saved, if it couldn't.
    /// </summary>
    public string? Keep(DateTime started, IReadOnlyList<(DateTime Started, string Path)> history)
    {
        var path = Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"{started:yyyy-MM-dd HH-mm-ss}.json"));
        var set = new PlayedSet(started, history.Select(entry => new PlayedSong(entry.Started, entry.Path)).ToList());
        foreach (var song in set.Songs)
            Played(song);
        try
        {
            // Every song marked as not played: nothing to keep.
            if (history.Count == 0)
            {
                File.Delete(path);
                return null;
            }
            Directory.CreateDirectory(_folder);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(set, Options));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
