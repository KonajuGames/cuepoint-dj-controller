namespace Cuepoint.Music;

/// <summary>
/// What could trip up a set played from a list: songs whose files have gone, songs on a drive that isn't plugged in,
/// and songs not analysed yet, which can't be beat-matched or found by tempo or key.
/// </summary>
sealed record SetCheck(int Songs, int Missing, int Unplugged, int NotAnalysed)
{
    /// <summary>
    /// Checks the list of <paramref name="paths"/>, of which <paramref name="found"/> are the songs there, looked up in
    /// the library so their analyses are on them. <paramref name="driveThere"/> says whether a path's drive is plugged
    /// in, to tell a song that's gone from one that's only out of reach.
    /// </summary>
    public static SetCheck Of(IReadOnlyList<string> paths, IReadOnlyList<Song> found, Func<string, bool> driveThere)
    {
        var there = found.Select(song => song.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var away = paths.Where(path => !there.Contains(path)).ToList();
        var unplugged = away.Count(path => !driveThere(path));
        return new(paths.Count, away.Count - unplugged, unplugged, found.Count(song => song.Analysis is null));
    }

    /// <summary>
    /// Whether a drive's plugged in, for <see cref="Of"/>: whether the root of <paramref name="path"/> is there.
    /// </summary>
    public static bool DriveThere(string path) => StorageDrives.RootOf(path) is { } root && Directory.Exists(root);

    public bool AllGood => Missing == 0 && Unplugged == 0 && NotAnalysed == 0;

    /// <summary>
    /// What's wrong, as a sentence, or that nothing is.
    /// </summary>
    public string Summary
    {
        get
        {
            if (AllGood)
                return Songs == 1 ? "The song is here and analysed." : $"All {Songs} songs are here and analysed.";
            var problems = new List<string>();
            if (Missing > 0)
                problems.Add($"{Count(Missing)} can't be found");
            if (Unplugged > 0)
                problems.Add($"{Count(Unplugged)} {(Unplugged == 1 ? "is" : "are")} on a drive that isn't plugged in");
            if (NotAnalysed > 0)
                problems.Add($"{Count(NotAnalysed)} {(NotAnalysed == 1 ? "isn't" : "aren't")} analysed yet");
            var list = problems.Count == 1 ? problems[0] : string.Join(", ", problems.Take(problems.Count - 1)) + " and " + problems[^1];
            return $"Of {Count(Songs)}, {list}.";
        }
    }

    static string Count(int songs) => songs == 1 ? "1 song" : $"{songs} songs";
}
