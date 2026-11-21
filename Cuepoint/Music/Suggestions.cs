using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Music;

/// <summary>
/// What's playing, to suggest the next song for: its key and tempo as heard (after key shift and the tempo fader),
/// and its energy if it's known.
/// </summary>
sealed record Playing(string Path, MusicalKey Key, double Bpm, int? Energy);

/// <summary>
/// A song that would mix well next, how well from 0 to 1, and why, in a few words.
/// </summary>
sealed record Suggestion(Song Song, double Score, string Why);

/// <summary>
/// The songs that would mix well after what's playing, best first: in a key that mixes, at a tempo the tempo fader
/// can reach, and at an energy a step up or down, as a DJ builds a set. Songs whose keys clash, or whose tempos are out
/// of reach, aren't suggested at all.
/// </summary>
static class Suggestions
{
    /// <summary>
    /// How far a song's tempo can be from what's playing, as a share, and still be suggested: within the usual ±8% of
    /// a tempo fader.
    /// </summary>
    public const double TEMPO_REACH = 0.08;

    // How much each counts, out of 1.
    const double KEY_WEIGHT = 0.45;
    const double TEMPO_WEIGHT = 0.35;
    const double ENERGY_WEIGHT = 0.2;

    /// <summary>
    /// The best <paramref name="count"/> of <paramref name="songs"/> to play after <paramref name="playing"/>, leaving
    /// out any <paramref name="skip"/> says to (played already, or on a deck).
    /// </summary>
    public static IReadOnlyList<Suggestion> For(Playing playing, IEnumerable<Song> songs, Func<Song, bool> skip, int count = 40) =>
        songs
            .Where(song => !string.Equals(song.Path, playing.Path, StringComparison.OrdinalIgnoreCase) && !skip(song))
            .Select(song => Score(playing, song))
            .OfType<Suggestion>()
            .OrderByDescending(suggestion => suggestion.Score)
            .ThenBy(suggestion => suggestion.Song.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(count)
            .ToList();

    /// <summary>
    /// How well <paramref name="song"/> would follow <paramref name="playing"/>; null if it wouldn't at all, or isn't
    /// analysed.
    /// </summary>
    public static Suggestion? Score(Playing playing, Song song)
    {
        if (song.Analysis is not { } analysis)
            return null;
        if (KeyScore(playing.Key, analysis.Key) is not (> 0 and var key))
            return null;
        if (TempoScore(playing.Bpm, analysis.Bpm) is not (>= 0 and var tempo))
            return null;
        var energy = EnergyScore(playing.Energy, analysis.Energy);
        var score = KEY_WEIGHT * key + TEMPO_WEIGHT * tempo + ENERGY_WEIGHT * energy;
        return new Suggestion(song, score, Why(playing, analysis));
    }

    /// <summary>
    /// How well <paramref name="next"/> follows <paramref name="key"/> on the Camelot wheel: the same key best, then a
    /// step round either way or to its relative major or minor, then two steps (an energy boost); 0 for a clash.
    /// </summary>
    public static double KeyScore(MusicalKey key, MusicalKey next)
    {
        if (key == next)
            return 1;
        var apart = Math.Abs(key.CamelotNumber - next.CamelotNumber);
        apart = Math.Min(apart, 12 - apart);
        if (key.IsMinor != next.IsMinor)
            return apart == 0 ? 0.85 : 0;
        return apart switch
        {
            1 => 0.85,
            2 => 0.4,
            _ => 0,
        };
    }

    /// <summary>
    /// How close <paramref name="bpm"/> is to <paramref name="playingBpm"/>, in half or double time if that's closer:
    /// 1 at the same tempo down to 0 at <see cref="TEMPO_REACH"/> away; below 0 past it.
    /// </summary>
    public static double TempoScore(double playingBpm, double bpm)
    {
        if (playingBpm <= 0 || bpm <= 0)
            return -1;
        var ratio = bpm / playingBpm;
        var closest = new[] { ratio, ratio * 2, ratio / 2 }.MinBy(r => Math.Abs(Math.Log(r)));
        return 1 - Math.Abs(closest - 1) / TEMPO_REACH;
    }

    /// <summary>
    /// How well a song of energy <paramref name="next"/> follows one of <paramref name="energy"/>: a step up best, as
    /// a set builds, then the same, then a step down, and less the further either way. Middling when one isn't known.
    /// </summary>
    public static double EnergyScore(int? energy, int? next)
    {
        if (energy is not { } from || next is not { } to)
            return 0.6;
        return (to - from) switch
        {
            1 => 1,
            0 => 0.95,
            -1 => 0.85,
            2 => 0.6,
            -2 => 0.5,
            _ => 0.15,
        };
    }

    static string Why(Playing playing, TrackAnalysis next)
    {
        var parts = new List<string> { $"{playing.Key.Camelot} → {next.Key.Camelot}" };
        var ratio = next.Bpm / playing.Bpm;
        var closest = new[] { ratio, ratio * 2, ratio / 2 }.MinBy(r => Math.Abs(Math.Log(r)));
        parts.Add(Math.Abs(closest - 1) < 0.0005 ? "same tempo" : $"{(closest - 1) * 100:+0.0;-0.0}% tempo");
        if (playing.Energy is { } from && next.Energy is { } to)
            parts.Add($"energy {from} → {to}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// What to suggest for, from <paramref name="decks"/>: the song playing with the most left to play, as it's the one
    /// to mix out of next; or with none playing, the loaded one with the most left. Null if none's analysed.
    /// </summary>
    public static Playing? From(IEnumerable<Deck> decks)
    {
        var loaded = decks.Where(deck => deck.IsLoaded && deck.FilePath is not null && deck.Key is not null && deck.Bpm is not null).ToList();
        var deck = (loaded.Any(deck => deck.IsPlaying) ? loaded.Where(deck => deck.IsPlaying) : loaded).MaxBy(deck => deck.Duration - deck.Position);
        return deck is null ? null : new Playing(deck.FilePath!, deck.Key!.Value, deck.Bpm!.Value, deck.Analysis?.Energy);
    }
}
