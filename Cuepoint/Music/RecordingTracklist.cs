using System.Globalization;
using System.Text;

namespace Cuepoint.Music;

/// <summary>
/// A song in a recording's tracklist: how far into the whole recording it starts, and who and what it is.
/// </summary>
sealed record RecordedTrack(TimeSpan At, string Artist, string Title)
{
    /// <summary>
    /// What it's called in a tracklist: "Artist - Title", or the title alone without an artist.
    /// </summary>
    public string Name => Artist.Length == 0 ? Title : $"{Artist} - {Title}";
}

/// <summary>
/// A recording's tracklist, kept as a cue sheet beside each of its files and as text beside the first: written as a
/// recording stops, and read back and written again when it's edited. A recording of more than one file (over four
/// hours) carries on in "Mix ... (2).wav" and so on, each file as long as <see cref="Audio.MixRecorder.FileLength"/>.
/// </summary>
static class RecordingTracklist
{
    static string Quoted(string text) => $"\"{text.Replace('"', '\'')}\"";

    /// <summary>
    /// A cue sheet for the WAV file <paramref name="wavName"/>, recorded on <paramref name="date"/>, marking where each
    /// of <paramref name="tracks"/> starts in it (times from the file's start).
    /// </summary>
    public static string CueSheet(IReadOnlyList<RecordedTrack> tracks, string wavName, DateTime date)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"REM DATE {date:yyyy-MM-dd}\n");
        text.Append("REM COMMENT \"Recorded with Cuepoint\"\n");
        text.Append(CultureInfo.InvariantCulture, $"TITLE {Quoted(Path.GetFileNameWithoutExtension(wavName))}\n");
        text.Append(CultureInfo.InvariantCulture, $"FILE {Quoted(wavName)} WAVE\n");
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            text.Append(CultureInfo.InvariantCulture, $"  TRACK {i + 1:00} AUDIO\n");
            text.Append(CultureInfo.InvariantCulture, $"    TITLE {Quoted(track.Title)}\n");
            if (track.Artist.Length > 0)
                text.Append(CultureInfo.InvariantCulture, $"    PERFORMER {Quoted(track.Artist)}\n");
            text.Append(CultureInfo.InvariantCulture, $"    INDEX 01 {PlayHistory.CueTime(track.At)}\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// A recording's tracklist as text, each song with how far into the recording it starts, to paste under a mix
    /// put online.
    /// </summary>
    public static string Text(IReadOnlyList<RecordedTrack> tracks, string title)
    {
        var text = new StringBuilder(title).Append("\n\n");
        var hours = tracks.Count > 0 && tracks[^1].At.TotalHours >= 1;
        foreach (var track in tracks)
            text.Append(CultureInfo.InvariantCulture, $"{Clock(track.At, hours)}  {track.Name}\n");
        return text.ToString();
    }

    /// <summary>
    /// A time in a recording as it's written in a tracklist: "4:05", or "1:04:05" when <paramref name="hours"/>.
    /// </summary>
    public static string Clock(TimeSpan at, bool hours) =>
        hours ? $"{(int)at.TotalHours}:{at.Minutes:00}:{at.Seconds:00}" : $"{(int)at.TotalMinutes}:{at.Seconds:00}";

    /// <summary>
    /// A time typed into a tracklist: "4:05", "64:05", "1:04:05", seconds alone, with a fraction of a second or
    /// without. Null if it isn't one.
    /// </summary>
    public static TimeSpan? ParseClock(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3 || parts.Any(part => part.Length == 0))
            return null;
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            return null;
        if (parts.Length > 1 && seconds >= 60)
            return null;
        var whole = 0L;
        foreach (var part in parts[..^1])
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return null;
            whole = whole * 60 + value;
        }
        // Minutes past an hour only make sense with hours in front.
        if (parts.Length == 3 && long.Parse(parts[1], CultureInfo.InvariantCulture) >= 60)
            return null;
        return TimeSpan.FromSeconds(whole * 60 + seconds);
    }

    /// <summary>
    /// The files of the recording that starts with <paramref name="firstWav"/>, in order: it, then "(2)", "(3)" and
    /// on, as long as there are more.
    /// </summary>
    public static IReadOnlyList<string> Files(string firstWav)
    {
        var files = new List<string> { firstWav };
        var folder = Path.GetDirectoryName(firstWav) ?? "";
        var name = Path.GetFileNameWithoutExtension(firstWav);
        for (var i = 2; File.Exists(Path.Combine(folder, $"{name} ({i}).wav")); i++)
            files.Add(Path.Combine(folder, $"{name} ({i}).wav"));
        return files;
    }

    /// <summary>
    /// The recording's first file, from any of its files or cue sheets: "Mix ... (2).cue" gives "Mix ....wav".
    /// </summary>
    public static string FirstFile(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(')') && name.LastIndexOf(" (", StringComparison.Ordinal) is var open and > 0 && int.TryParse(name[(open + 2)..^1], out _))
            name = name[..open];
        return Path.Combine(folder, name + ".wav");
    }

    /// <summary>
    /// Reads the tracklist of the recording that starts with <paramref name="firstWav"/> back from its files' cue
    /// sheets, each <paramref name="fileLength"/> long, as one list through the whole recording, and the day it was
    /// recorded. A song carried over from one file into the next is there once.
    /// </summary>
    public static (IReadOnlyList<RecordedTrack> Tracks, DateTime Date) Read(string firstWav, TimeSpan fileLength)
    {
        var tracks = new List<RecordedTrack>();
        DateTime? date = null;
        var files = Files(firstWav);
        for (var i = 0; i < files.Count; i++)
        {
            var cue = Path.ChangeExtension(files[i], ".cue");
            if (!File.Exists(cue))
                continue;
            var (fileTracks, fileDate) = ParseCueSheet(File.ReadAllText(cue));
            date ??= fileDate;
            foreach (var track in fileTracks)
            {
                var at = track with { At = track.At + fileLength * i };
                // The song playing as a file starts is there at its start again.
                if (track.At == TimeSpan.Zero && i > 0 && tracks.Count > 0 && tracks[^1].Artist == track.Artist && tracks[^1].Title == track.Title)
                    continue;
                tracks.Add(at);
            }
        }
        return (tracks, date ?? File.GetCreationTime(firstWav).Date);
    }

    /// <summary>
    /// The tracks a cue sheet marks, with the day in its REM DATE, if it has one.
    /// </summary>
    internal static (IReadOnlyList<RecordedTrack> Tracks, DateTime? Date) ParseCueSheet(string text)
    {
        var tracks = new List<RecordedTrack>();
        DateTime? date = null;
        string? title = null, artist = null;
        var inTrack = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("REM DATE ", StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParseExact(line[9..].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                date = day;
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                (inTrack, title, artist) = (true, null, null);
            else if (inTrack && line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                title = Unquoted(line[6..]);
            else if (inTrack && line.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
                artist = Unquoted(line[10..]);
            else if (inTrack && line.StartsWith("INDEX 01 ", StringComparison.OrdinalIgnoreCase) && CueTimeOf(line[9..].Trim()) is { } at)
                tracks.Add(new RecordedTrack(at, artist ?? "", title ?? ""));
        }
        return (tracks, date);
    }

    static string Unquoted(string text)
    {
        text = text.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }

    /// <summary>
    /// A cue sheet's "mm:ss:ff" time, 75 frames to the second.
    /// </summary>
    static TimeSpan? CueTimeOf(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 3 || !parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return null;
        var (minutes, seconds, frames) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
        return TimeSpan.FromSeconds((minutes * 60 + seconds) * 75 + frames) / 75;
    }

    /// <summary>
    /// The tracks heard in one file of a recording, from <paramref name="from"/> until <paramref name="to"/>, with
    /// times from the file's start; the song still playing as it starts is there from its start.
    /// </summary>
    public static IReadOnlyList<RecordedTrack> InFile(IReadOnlyList<RecordedTrack> tracks, TimeSpan from, TimeSpan to)
    {
        var inside = tracks.Where(track => track.At >= from && track.At < to).Select(track => track with { At = track.At - from }).ToList();
        if (tracks.LastOrDefault(track => track.At < from) is { } playing && (inside.Count == 0 || inside[0].At > TimeSpan.Zero))
            inside.Insert(0, playing with { At = TimeSpan.Zero });
        return inside;
    }

    /// <summary>
    /// Writes <paramref name="tracks"/> (in order through the whole recording) as the tracklist of the recording
    /// that starts with <paramref name="firstWav"/>: a cue sheet beside each of its files, each
    /// <paramref name="fileLength"/> long, and the text beside the first. Returns why it couldn't, if it couldn't.
    /// </summary>
    public static string? Write(string firstWav, IReadOnlyList<RecordedTrack> tracks, DateTime date, TimeSpan fileLength)
    {
        var ordered = tracks.OrderBy(track => track.At).ToList();
        var failed = PlayHistory.Save(Path.ChangeExtension(firstWav, ".txt"), Text(ordered, Path.GetFileNameWithoutExtension(firstWav)), HistoryFormat.Text);
        var files = Files(firstWav);
        for (var i = 0; i < files.Count; i++)
        {
            var from = fileLength * i;
            var to = i + 1 < files.Count ? fileLength * (i + 1) : TimeSpan.MaxValue;
            var inFile = InFile(ordered, from, to);
            var cue = Path.ChangeExtension(files[i], ".cue");
            if (inFile.Count > 0)
                failed ??= PlayHistory.SaveCueSheet(cue, CueSheet(inFile, Path.GetFileName(files[i]), date));
            else if (File.Exists(cue))
            {
                try
                {
                    File.Delete(cue);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed ??= e.Message;
                }
            }
        }
        return failed;
    }
}
