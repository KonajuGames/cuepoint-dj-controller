using System.Globalization;
using System.Text;

namespace Cuepoint.Music;

/// <summary>
/// How a play history is written out: as a playlist, to load the set again anywhere, or as text, to read or send on.
/// </summary>
enum HistoryFormat
{
    /// <summary>
    /// An extended M3U playlist in UTF-8: each song's length, artist and title, then its file's path.
    /// </summary>
    M3u,

    /// <summary>
    /// Plain text: the day, then each song with the time it started, as a set list.
    /// </summary>
    Text,
}

/// <summary>
/// Writes the songs played in a session, in order, as a playlist or a set list.
/// </summary>
static class PlayHistory
{
    /// <summary>
    /// The history in <paramref name="format"/>. <paramref name="songAt"/> gives a song's details by its path, where
    /// they're known; without them, a song goes by its file's name.
    /// </summary>
    public static string Write(IReadOnlyList<(DateTime Started, string Path)> history, HistoryFormat format, Func<string, Song?> songAt) =>
        format == HistoryFormat.M3u ? M3u(history, songAt) : Text(history, songAt);

    static string Name(string path, Song? song) =>
        song is null || song.Title.Length == 0 ? Path.GetFileNameWithoutExtension(path)
        : song.Artist.Length == 0 ? song.Title
        : $"{song.Artist} - {song.Title}";

    static string M3u(IReadOnlyList<(DateTime Started, string Path)> history, Func<string, Song?> songAt)
    {
        var text = new StringBuilder("#EXTM3U\n");
        if (history.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $"#PLAYLIST:Played {history[0].Started:yyyy-MM-dd HH:mm}\n");
        foreach (var (_, path) in history)
        {
            var song = songAt(path);
            // How long it is, in whole seconds; -1 where that's not known.
            var seconds = song?.Duration is { } length ? (int)Math.Round(length.TotalSeconds) : -1;
            text.Append(CultureInfo.InvariantCulture, $"#EXTINF:{seconds},{Name(path, song)}\n{path}\n");
        }
        return text.ToString();
    }

    static string Text(IReadOnlyList<(DateTime Started, string Path)> history, Func<string, Song?> songAt)
    {
        var text = new StringBuilder();
        // A heading for each day, as a set can run past midnight.
        DateTime? day = null;
        foreach (var (started, path) in history)
        {
            if (day != started.Date)
            {
                if (day is not null)
                    text.Append('\n');
                day = started.Date;
                text.Append(CultureInfo.CurrentCulture, $"Played on {started:dddd d MMMM yyyy}\n\n");
            }
            text.Append(CultureInfo.InvariantCulture, $"{started:HH:mm}  {Name(path, songAt(path))}\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// The songs heard in a recording that started at <paramref name="started"/>, from <paramref name="from"/> into it
    /// until <paramref name="to"/>, with how far into that stretch each starts. A song already playing as the stretch
    /// begins (the last to start before it, if its length says it isn't over by then) is there from the start.
    /// </summary>
    public static IReadOnlyList<(TimeSpan At, string Path)> Recorded(
        IReadOnlyList<(DateTime Started, string Path)> history,
        DateTime started,
        TimeSpan from,
        TimeSpan to,
        Func<string, Song?> songAt
    )
    {
        var tracks = new List<(TimeSpan At, string Path)>();
        (DateTime Started, string Path)? playing = null;
        // In the order they started, which isn't quite the history's: a song goes in once it's played ten seconds.
        foreach (var entry in history.OrderBy(entry => entry.Started))
        {
            var at = entry.Started - started;
            if (at < from)
                playing = entry;
            else if (at < to)
                tracks.Add((at - from, entry.Path));
        }
        if (playing is { } before && (tracks.Count == 0 || tracks[0].At > TimeSpan.Zero))
        {
            if (songAt(before.Path)?.Duration is not { } length || before.Started + length > started + from)
                tracks.Insert(0, (TimeSpan.Zero, before.Path));
        }
        return tracks;
    }

    /// <summary>
    /// A cue sheet for the WAV file <paramref name="wavName"/>, a recording made at <paramref name="started"/>, marking
    /// where each of <paramref name="tracks"/> starts in it, so players and mix sites show the tracklist and can skip
    /// through it.
    /// </summary>
    public static string CueSheet(IReadOnlyList<(TimeSpan At, string Path)> tracks, string wavName, DateTime started, Func<string, Song?> songAt) =>
        RecordingTracklist.CueSheet(Tracks(tracks, songAt), wavName, started);

    /// <summary>
    /// What's known of each song played, for a tracklist: its artist, and its title or else its file's name.
    /// </summary>
    static IReadOnlyList<RecordedTrack> Tracks(IReadOnlyList<(TimeSpan At, string Path)> tracks, Func<string, Song?> songAt) =>
        tracks
            .Select(track =>
            {
                var song = songAt(track.Path);
                return new RecordedTrack(track.At, song?.Artist ?? "", song is { Title.Length: > 0 } ? song.Title : Path.GetFileNameWithoutExtension(track.Path));
            })
            .ToList();

    /// <summary>
    /// A cue sheet's time: minutes, seconds and frames, 75 to the second, as on a CD.
    /// </summary>
    internal static string CueTime(TimeSpan at)
    {
        var frames = (long)Math.Floor(Math.Max(0, at.TotalSeconds) * 75);
        return string.Create(CultureInfo.InvariantCulture, $"{frames / 75 / 60:00}:{frames / 75 % 60:00}:{frames % 75:00}");
    }

    /// <summary>
    /// A recording's tracklist as text, each song with how far into the recording it starts, to paste under a mix
    /// put online.
    /// </summary>
    public static string Tracklist(IReadOnlyList<(TimeSpan At, string Path)> tracks, string title, Func<string, Song?> songAt) =>
        RecordingTracklist.Text(Tracks(tracks, songAt), title);

    /// <summary>
    /// Writes a cue sheet to <paramref name="path"/>, in UTF-8 with a byte order mark, so players that read it in the
    /// system's code page otherwise still show accented names right, and Windows' line endings. Returns why it
    /// couldn't, if it couldn't.
    /// </summary>
    public static string? SaveCueSheet(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text.Replace("\n", "\r\n"), new UTF8Encoding(true));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/>, in UTF-8 without a byte order mark, with Windows'
    /// line endings for a text file so Notepad and the rest show it right. Returns why it couldn't, if it couldn't.
    /// </summary>
    public static string? Save(string path, string text, HistoryFormat format)
    {
        try
        {
            File.WriteAllText(path, format == HistoryFormat.Text ? text.Replace("\n", "\r\n") : text, new UTF8Encoding(false));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
