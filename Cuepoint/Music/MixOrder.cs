namespace Cuepoint.Music;

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
    /// How far apart two tempos are, as a share, in half or double time if that's nearer.
    /// </summary>
    static double TempoDistance(double bpm, double next)
    {
        var ratio = next / bpm;
        return new[] { ratio, ratio * 2, ratio / 2 }.Select(r => Math.Abs(Math.Log(r))).Min();
    }
}
