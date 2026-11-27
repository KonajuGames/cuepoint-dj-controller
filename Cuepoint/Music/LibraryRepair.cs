using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cuepoint.Music;

/// <summary>
/// Finding songs that are in the library twice, and songs whose files have moved. Nothing here deletes or moves a
/// file: the copies are only listed, and a moved song only has what's known about it follow it to its new place.
/// </summary>
static partial class LibraryRepair
{
    /// <summary>
    /// How far apart two songs' lengths can be and still be the same recording: enough for a different encode or
    /// rip, not for a radio edit beside an extended mix.
    /// </summary>
    public const double SIMILAR_SECONDS = 3;

    /// <summary>
    /// <paramref name="songs"/> that look like the same recording as one or more of the others, in groups, each in
    /// path order: the same artist and title, ignoring case, punctuation, a leading track number, "feat." credits
    /// and anything in brackets ("Remastered 2011"), and lengths within <see cref="SIMILAR_SECONDS"/>. Copies tagged or
    /// encoded differently are found, as exact copies are too. A song whose length isn't known yet goes by its name.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Song>> FindSimilar(IEnumerable<Song> songs)
    {
        var groups = new List<IReadOnlyList<Song>>();
        foreach (var named in songs.GroupBy(song => (Normal(song.Artist), Normal(song.Title))).Where(group => group.Key.Item2.Length > 0 && group.Count() > 1))
        {
            // Strung out by length, a song joins the run before it if it's close enough to the last one's.
            var runs = new List<List<Song>>();
            foreach (var song in named.OrderBy(song => song.Duration ?? TimeSpan.Zero))
            {
                var last = runs.LastOrDefault()?.Last();
                if (last is not null && (last.Duration is not { } a || song.Duration is not { } b || (b - a).TotalSeconds <= SIMILAR_SECONDS))
                    runs[^1].Add(song);
                else
                    runs.Add([song]);
            }
            groups.AddRange(runs.Where(run => run.Count > 1).Select(run => (IReadOnlyList<Song>)run.OrderBy(song => song.Path, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return groups.OrderBy(group => group[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"^\d{1,3}[\s.\-_]+")]
    private static partial Regex TrackNumber();

    /// <summary>
    /// A name as <see cref="FindSimilar"/> compares it: without a leading track number, "feat." credits or anything in
    /// brackets, and only its letters and digits, in lower case.
    /// </summary>
    internal static string Normal(string name)
    {
        var text = Featuring().Replace(Bracketed().Replace(TrackNumber().Replace(name.Trim(), ""), ""), "");
        var kept = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
                kept.Append(char.ToLowerInvariant(c));
        }
        return kept.ToString();
    }

    /// <summary>
    /// A song's file gone from <paramref name="From"/> and found at <paramref name="To"/>, with that file's time.
    /// </summary>
    public sealed record Move(string From, string To, DateTime Modified);

    /// <summary>
    /// What a look for moved songs found: where to move them, how many were missing in all, and how many could be
    /// any of several files, so were left.
    /// </summary>
    public sealed record MovedSongs(IReadOnlyList<Move> Moves, int Missing, int Ambiguous);

    // macOS leaves a hidden "._" file beside each song on a drive it's used, all alike and none of them music.
    static bool IsMacCompanion(FileInfo file) => file.Name.StartsWith("._", StringComparison.Ordinal);

    /// <summary>
    /// The paths of <paramref name="files"/> that are exact copies of each other, in groups, those in path order. Only
    /// files of the same size are read, so most never are. A song tagged or encoded differently isn't an exact copy.
    /// Safe on any thread.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> FindCopies(IReadOnlyList<FileInfo> files) =>
        files
            .Where(file => !IsMacCompanion(file))
            .GroupBy(file => file.Length)
            .Where(sized => sized.Count() > 1)
            .SelectMany(sized => sized.Select(file => (file.FullName, Hash: HashOf(file))).Where(file => file.Hash is not null).GroupBy(file => file.Hash))
            .Where(copies => copies.Count() > 1)
            .Select(copies => (IReadOnlyList<string>)copies.Select(file => file.FullName).Order(StringComparer.OrdinalIgnoreCase).ToList())
            .OrderBy(copies => copies[0], StringComparer.OrdinalIgnoreCase)
            .ToList();

    static string? HashOf(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the songs in <paramref name="known"/> whose files are gone have got to among <paramref name="files"/>: the
    /// one file with the same name and size. Songs on a drive that isn't plugged in aren't gone. Safe on any thread.
    /// </summary>
    public static MovedSongs FindMoved(IReadOnlyList<(string Path, long Size)> known, IReadOnlyList<FileInfo> files)
    {
        var byName = files.Where(file => !IsMacCompanion(file)).ToLookup(file => file.Name, StringComparer.OrdinalIgnoreCase);
        var plugged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool IsPluggedIn(string path)
        {
            var root = StorageDrives.RootOf(path) ?? "";
            if (!plugged.TryGetValue(root, out var there))
                plugged[root] = there = Directory.Exists(root);
            return there;
        }

        var (missing, ambiguous) = (0, 0);
        var found = new List<Move>();
        foreach (var (path, size) in known)
        {
            if (File.Exists(path) || !IsPluggedIn(path))
                continue;
            missing++;
            var candidates = byName[Path.GetFileName(path)].Where(file => file.Length == size).Take(2).ToList();
            if (candidates.Count > 1)
                ambiguous++;
            else if (candidates.Count == 1)
                found.Add(new Move(path, candidates[0].FullName, candidates[0].LastWriteTimeUtc));
        }

        // Two songs gone that both match the one file can't both be it.
        var moves = found.GroupBy(move => move.To, StringComparer.OrdinalIgnoreCase).Where(same => same.Count() == 1).Select(same => same.First()).ToList();
        return new MovedSongs(moves, missing, ambiguous + found.Count - moves.Count);
    }

    /// <summary>
    /// Moves each song's file in <paramref name="paths"/> into <paramref name="folder"/>, on another drive or not, never
    /// over a file there already, and says where each went, for <see cref="Apply"/> to take what's known about it
    /// there; and why any couldn't be moved. A song in the folder already is left. Safe on any thread.
    /// </summary>
    public static (IReadOnlyList<Move> Moves, IReadOnlyList<string> Problems) MoveFiles(IReadOnlyList<string> paths, string folder)
    {
        var moves = new List<Move>();
        var problems = new List<string>();
        foreach (var path in paths)
        {
            var to = Path.Combine(folder, Path.GetFileName(path));
            if (string.Equals(Path.GetFullPath(to), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(to))
            {
                problems.Add($"{Path.GetFileName(path)} is in {folder} already");
                continue;
            }
            try
            {
                File.Move(path, to);
                moves.Add(new Move(path, to, File.GetLastWriteTimeUtc(to)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }
        return (moves, problems);
    }

    /// <summary>
    /// What's known about each of <paramref name="others"/>, copies of <paramref name="kept"/> about to be thrown
    /// away, as moves onto it, for <see cref="Apply"/>: their cues, notes and places in virtual folders come to it,
    /// where it hasn't its own, the first copy's first.
    /// </summary>
    public static IReadOnlyList<Move> OntoKept(Song kept, IEnumerable<string> others) =>
        others.Where(other => !string.Equals(other, kept.Path, StringComparison.OrdinalIgnoreCase)).Select(other => new Move(other, kept.Path, kept.Modified)).ToList();

    /// <summary>
    /// Takes what's known about each song to its new place, and points the virtual folders there too. Doesn't save the
    /// database. Returns why a virtual folder couldn't be saved, if one couldn't. UI thread only.
    /// </summary>
    public static string? Apply(IReadOnlyList<Move> moves, LibraryDatabase database, MusicLibrary library, LibraryAnalyser analyser, VirtualFolders virtualFolders)
    {
        foreach (var move in moves)
        {
            database.Relocate(move.From, move.To, move.Modified);
            if (library.Find(move.To) is { } song)
                analyser.LookAgain(song);
        }
        return virtualFolders.Relocate(moves.ToDictionary(move => move.From, move => move.To, StringComparer.OrdinalIgnoreCase));
    }
}
