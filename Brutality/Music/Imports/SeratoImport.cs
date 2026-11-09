using Brutality.Audio.Analysis;

namespace Brutality.Music.Imports;

/// <summary>
/// Serato's hot cues and beat grids for songs, from their own files: <see cref="Read"/> looks in them (on any thread,
/// as many files take a while), then <see cref="Take"/> gives each song what it has none of here (on the UI thread).
/// A song's own hot cues here, or a grid fixed here, are kept. Only grids locked in Serato come (see
/// <see cref="SeratoMarkers.ReadGrid"/>): the rest are Serato's own analysis, no better than this app's.
/// </summary>
static class SeratoImport
{
    /// <summary>
    /// What Serato saved in a song's file: its hot cues, its beat grid, or both.
    /// </summary>
    public sealed record Found(string Path, IReadOnlyList<TimeSpan?>? Hotcues, BeatGrid? Grid);

    /// <summary>
    /// What came in: how many songs got hot cues and how many a beat grid, those that had their own of what Serato
    /// had, and the songs given a grid, to use it straight away.
    /// </summary>
    public sealed record Taken(int Hotcues, int Grids, int Kept, IReadOnlyList<string> Gridded)
    {
        /// <summary>
        /// What came in, as "hot cues for 3 songs and beat grids for 2 songs"; null if nothing did.
        /// </summary>
        public string? News(Func<int, string> songs)
        {
            var news = new List<string>();
            if (Hotcues > 0)
                news.Add($"hot cues for {songs(Hotcues)}");
            if (Grids > 0)
                news.Add($"beat grids for {songs(Grids)}");
            return news.Count == 0 ? null : string.Join(" and ", news);
        }
    }

    /// <summary>
    /// The songs among <paramref name="paths"/> whose files have Serato hot cues or a beat grid, with them. Safe on
    /// any thread.
    /// </summary>
    public static IReadOnlyList<Found> Read(IEnumerable<string> paths) =>
        paths
            .Select(path => (Path: path, Read: SeratoMarkers.ReadAll(path)))
            .Select(read => new Found(read.Path, read.Read.Hotcues, read.Read.Grid))
            .Where(found => found.Hotcues is not null || found.Grid is not null)
            .ToList();

    /// <summary>
    /// Gives each of <paramref name="found"/> its hot cues, unless it has some in <paramref name="database"/> already,
    /// and its beat grid, unless one's been fixed there. Doesn't save.
    /// </summary>
    public static Taken Take(LibraryDatabase database, IEnumerable<Found> found)
    {
        var (hotcues, grids, kept) = (0, 0, 0);
        var gridded = new List<string>();
        foreach (var (path, cues, grid) in found)
        {
            var keptOwn = false;
            if (cues is not null)
            {
                if (database.GetHotcues(path).Any(cue => cue is not null))
                    keptOwn = true;
                else
                {
                    database.SetHotcues(path, cues);
                    hotcues++;
                }
            }
            if (grid is not null)
            {
                if (database.HasGridEdit(path))
                    keptOwn = true;
                else
                {
                    database.SetGridEdit(path, AsEdit(grid));
                    gridded.Add(path);
                    grids++;
                }
            }
            if (keptOwn)
                kept++;
        }
        return new Taken(hotcues, grids, kept, gridded);
    }

    /// <summary>
    /// A grid from other DJ software as one fixed by hand here: where its tempo changes, each change pinned.
    /// </summary>
    public static GridEdit AsEdit(BeatGrid grid) => new(grid, grid.IsVariable ? grid.Markers.Select(marker => marker.Time).ToList() : []);
}
