using System.Security.Cryptography;

namespace Brutality.Music;

/// <summary>
/// Finding songs that are in the library twice, and songs whose files have moved. Nothing here deletes or moves a
/// file: the copies are only listed, and a moved song only has what's known about it follow it to its new place.
/// </summary>
static class LibraryRepair
{
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
            var root = Path.GetPathRoot(path) ?? "";
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
