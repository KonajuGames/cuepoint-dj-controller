using System.Globalization;
using System.Text;

namespace Brutality.Music;

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
