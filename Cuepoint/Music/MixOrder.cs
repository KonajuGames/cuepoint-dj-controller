namespace Cuepoint.Music;

/// <summary>
/// An order to put Auto-Mix's songs still to come in.
/// </summary>
enum SongOrder
{
    /// <summary>Each followed by the one that mixes best after it (see <see cref="MixOrder.Arrange"/>).</summary>
    Mixing,
    Title,
    Artist,
    /// <summary>Slowest first.</summary>
    Tempo,
    /// <summary>Round the Camelot wheel from 1A.</summary>
    Key,
    /// <summary>Calmest first.</summary>
    Energy,
    Shuffle,
}

/// <summary>
/// Puts songs in an order to mix them in: each followed by the one that mixes best after it, as
/// <see cref="Suggestions"/> scores them (a key that mixes, a tempo in reach, an energy a step up or down), so a set
/// walks round the Camelot wheel and builds. Where nothing left mixes in key, the closest tempo comes next.
/// </summary>
static class MixOrder
{
    /// <summary>
    /// <paramref name="songs"/> in an order to mix, after <paramref name="after"/> if a song's playing already, or else
    /// from the calmest (the lowest energy, then the slowest). Songs not analysed yet go last, in the order they were.
    /// </summary>
    public static IReadOnlyList<Song> Arrange(IReadOnlyList<Song> songs, Song? after = null)
    {
        var left = songs.Where(song => song.Analysis is not null).ToList();
        var order = new List<Song>(songs.Count);
        var current = after is { Analysis: not null } ? after : null;
        if (current is null && left.Count > 0)
        {
            current = left.OrderBy(song => song.Analysis!.Energy ?? 5).ThenBy(song => song.Analysis!.Bpm).First();
            order.Add(current);
            left.Remove(current);
        }

        while (left.Count > 0)
        {
            var analysis = current!.Analysis!;
            var playing = new Playing(current.Path, analysis.Key, analysis.Bpm, analysis.Energy);
            var next =
                left.Select(song => Suggestions.Score(playing, song)).OfType<Suggestion>().MaxBy(suggestion => suggestion.Score)?.Song
                // Nothing mixes in key: the nearest tempo, then the nearest energy.
                ?? left.MinBy(song => (Math.Round(TempoDistance(analysis.Bpm, song.Analysis!.Bpm), 3), Math.Abs((song.Analysis.Energy ?? 5) - (analysis.Energy ?? 5))))!;
            order.Add(next);
            left.Remove(next);
            current = next;
        }

        order.AddRange(songs.Where(song => song.Analysis is null));
        return order;
    }

    /// <summary>
    /// <paramref name="songs"/> in the order <paramref name="how"/> says, after <paramref name="after"/> if a song's
    /// playing already (which only ordering for mixing minds). Songs with nothing to order them by go last, and those
    /// that tie keep the order they were in.
    /// </summary>
    public static IReadOnlyList<Song> Sort(IReadOnlyList<Song> songs, SongOrder how, Song? after = null, Random? random = null) =>
        how switch
        {
            SongOrder.Mixing => Arrange(songs, after),
            SongOrder.Title => ByText(songs, song => song.Title),
            SongOrder.Artist => ByText(songs, song => song.Artist),
            SongOrder.Tempo => By(songs, song => song.Analysis?.Bpm),
            SongOrder.Key => By(songs, song => song.Analysis is { } analysis ? analysis.Key.CamelotNumber * 2 + (analysis.Key.IsMinor ? 0 : 1) : null),
            SongOrder.Energy => By(songs, song => song.Analysis?.Energy),
            SongOrder.Shuffle => songs.OrderBy(_ => (random ?? Random.Shared).Next()).ToList(),
            _ => songs,
        };

    /// <summary>
    /// What to call each order, in the list to choose one.
    /// </summary>
    public static string Name(SongOrder how) =>
        how switch
        {
            SongOrder.Mixing => "For mixing",
            SongOrder.Shuffle => "Shuffle",
            _ => $"By {how.ToString().ToLowerInvariant()}",
        };

    static IReadOnlyList<Song> ByText(IReadOnlyList<Song> songs, Func<Song, string?> text) =>
        songs.OrderBy(song => string.IsNullOrWhiteSpace(text(song))).ThenBy(text, StringComparer.CurrentCultureIgnoreCase).ToList();

    static IReadOnlyList<Song> By(IReadOnlyList<Song> songs, Func<Song, double?> value) =>
        songs.OrderBy(song => value(song) is null).ThenBy(value).ToList();

    /// <summary>
    /// How far apart two tempos are, as a share, in half or double time if that's nearer.
    /// </summary>
    static double TempoDistance(double bpm, double next)
    {
        var ratio = next / bpm;
        return new[] { ratio, ratio * 2, ratio / 2 }.Select(r => Math.Abs(Math.Log(r))).Min();
    }
}
