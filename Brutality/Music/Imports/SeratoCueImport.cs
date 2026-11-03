namespace Brutality.Music.Imports;

/// <summary>
/// Serato's hot cues for many songs at once, as chosen from a menu: <see cref="Read"/> looks in their files (on any
/// thread, as many files take a while), then <see cref="Take"/> gives them to the songs with none here (on the UI
/// thread). Songs with hot cues of their own here keep them.
/// </summary>
static class SeratoCueImport
{
    /// <summary>
    /// The songs among <paramref name="paths"/> whose files have Serato hot cues, with them. Safe on any thread.
    /// </summary>
    public static IReadOnlyList<(string Path, IReadOnlyList<TimeSpan?> Cues)> Read(IEnumerable<string> paths) =>
        paths.Select(path => (Path: path, Cues: SeratoMarkers.ReadHotcues(path))).Where(read => read.Cues is not null).Select(read => (read.Path, read.Cues!)).ToList();

    /// <summary>
    /// Gives each of <paramref name="found"/> its hot cues in <paramref name="database"/>, unless it has some there
    /// already. How many got them, and how many kept their own. Doesn't save.
    /// </summary>
    public static (int Brought, int Kept) Take(LibraryDatabase database, IEnumerable<(string Path, IReadOnlyList<TimeSpan?> Cues)> found)
    {
        var (brought, kept) = (0, 0);
        foreach (var (path, cues) in found)
        {
            if (database.GetHotcues(path).Any(cue => cue is not null))
            {
                kept++;
                continue;
            }
            database.SetHotcues(path, cues);
            brought++;
        }
        return (brought, kept);
    }
}
