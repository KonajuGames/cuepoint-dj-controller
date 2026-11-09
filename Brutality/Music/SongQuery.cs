using System.Globalization;
using System.Text.RegularExpressions;
using Brutality.Audio.Analysis;

namespace Brutality.Music;

/// <summary>
/// What's typed in the song list's search box: words to find in the title, artist or album (every one somewhere), and
/// besides them, a tempo and a key. A tempo is a range, "120-126" (BPM, either way round, with or without "bpm"), or
/// one, "124bpm", give or take half a beat a minute. A key is on the Camelot wheel: "8A" for that key alone, or "~8A"
/// for every key that mixes with it (itself, a step round the wheel either way, and its relative major or minor).
/// Only songs analysed can be found by tempo or key. "rating:4" finds songs rated 4 or more, "color:red" those tagged
/// that colour, and "comment:word" those with the word in their comment; words on their own look in comments too.
/// Anything in double quotes is text to find as it's written, spaces
/// and all, so "80-90" finds those characters rather than a tempo; a quote left open runs to the end.
/// </summary>
sealed partial record SongQuery(
    IReadOnlyList<string> Words,
    double? MinBpm,
    double? MaxBpm,
    MusicalKey? Key,
    bool Compatible,
    int MinRating = 0,
    string? Color = null,
    IReadOnlyList<string>? Comments = null
)
{
    /// <summary>
    /// How far either side of a single tempo a song's can be and still be found.
    /// </summary>
    const double ONE_BPM_TOLERANCE = 0.5;

    public static readonly SongQuery Everything = new([], null, null, null, false);

    /// <summary>
    /// Whether it asks for anything; a search of nothing lists every song.
    /// </summary>
    public bool IsEmpty => Words.Count == 0 && !NeedsAnalysis && !NeedsNotes;

    /// <summary>
    /// Whether it asks for a tempo or key, which only an analysed song has.
    /// </summary>
    public bool NeedsAnalysis => MinBpm is not null || Key is not null;

    /// <summary>
    /// Whether it asks for a rating, colour or comment.
    /// </summary>
    public bool NeedsNotes => MinRating > 0 || Color is not null || Comments is { Count: > 0 };

    [GeneratedRegex(@"^rating:([1-5])$", RegexOptions.IgnoreCase)]
    private static partial Regex RatingWord();

    [GeneratedRegex(@"^colou?r:(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ColorWord();

    [GeneratedRegex(@"^comment:(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CommentWord();

    [GeneratedRegex(@"^(\d{2,3}(?:\.\d+)?)-(\d{2,3}(?:\.\d+)?)(?:bpm)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BpmRange();

    [GeneratedRegex(@"^(\d{2,3}(?:\.\d+)?)bpm$", RegexOptions.IgnoreCase)]
    private static partial Regex OneBpm();

    [GeneratedRegex(@"^(~?)(1[0-2]|[1-9])([AB])$", RegexOptions.IgnoreCase)]
    private static partial Regex CamelotKey();

    // A range written with spaces round its dash, or an en dash: "120 - 126", "120–126".
    [GeneratedRegex(@"(\d)\s*[-–]\s*(\d)")]
    private static partial Regex SpacedRange();

    // A quoted stretch, its closing quote optional as it's being typed; or a stretch with no quotes.
    [GeneratedRegex("\"([^\"]*)\"?|[^\"]+")]
    private static partial Regex QuotedOrNot();

    public static SongQuery Parse(string text)
    {
        var words = new List<string>();
        double? min = null, max = null;
        MusicalKey? key = null;
        var compatible = false;
        var rating = 0;
        string? color = null;
        var comments = new List<string>();
        foreach (Match part in QuotedOrNot().Matches(text))
        {
            if (part.Value[0] == '"')
            {
                if (part.Groups[1].Value.Trim() is { Length: > 0 } quoted)
                    words.Add(quoted);
                continue;
            }
            foreach (var word in SpacedRange().Replace(part.Value, "$1-$2").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (BpmRange().Match(word) is { Success: true } range)
                {
                    var (a, b) = (Number(range.Groups[1].Value), Number(range.Groups[2].Value));
                    (min, max) = (Math.Min(a, b), Math.Max(a, b));
                }
                else if (OneBpm().Match(word) is { Success: true } one)
                    (min, max) = (Number(one.Groups[1].Value) - ONE_BPM_TOLERANCE, Number(one.Groups[1].Value) + ONE_BPM_TOLERANCE);
                else if (CamelotKey().Match(word) is { Success: true } camelot)
                {
                    key = FromCamelot(int.Parse(camelot.Groups[2].Value, CultureInfo.InvariantCulture), char.ToUpperInvariant(camelot.Groups[3].Value[0]) == 'A');
                    compatible = camelot.Groups[1].Value == "~";
                }
                else if (RatingWord().Match(word) is { Success: true } stars)
                    rating = int.Parse(stars.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (ColorWord().Match(word) is { Success: true } tagged)
                    color = tagged.Groups[1].Value;
                else if (CommentWord().Match(word) is { Success: true } said)
                    comments.Add(said.Groups[1].Value);
                else
                    words.Add(word);
            }
        }
        return new SongQuery(words, min, max, key, compatible, rating, color, comments);
    }

    static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>
    /// The key at <paramref name="number"/> on the Camelot wheel, minor (A) or major (B).
    /// </summary>
    static MusicalKey FromCamelot(int number, bool minor)
    {
        for (var tonic = 0; tonic < 12; tonic++)
        {
            var key = new MusicalKey(tonic, minor);
            if (key.CamelotNumber == number)
                return key;
        }
        throw new ArgumentOutOfRangeException(nameof(number));
    }

    public bool Matches(Song song)
    {
        foreach (var word in Words)
        {
            if (
                !song.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !song.Artist.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !song.Album.Contains(word, StringComparison.OrdinalIgnoreCase)
                && song.Notes.Comment?.Contains(word, StringComparison.OrdinalIgnoreCase) != true
            )
                return false;
        }
        if (song.Notes.Rating < MinRating)
            return false;
        if (Color is { } color && !string.Equals(song.Notes.Color, color, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var comment in Comments ?? [])
        {
            if (song.Notes.Comment?.Contains(comment, StringComparison.OrdinalIgnoreCase) != true)
                return false;
        }
        if (!NeedsAnalysis)
            return true;
        if (song.Analysis is not { } analysis)
            return false;
        // By the tempo shown for it, to the tenth.
        var bpm = Math.Round(analysis.Bpm, 1);
        if (MinBpm is { } min && (bpm < min || bpm > MaxBpm))
            return false;
        if (Key is { } key && !(Compatible ? analysis.Key.MixesWith(key) : analysis.Key == key))
            return false;
        return true;
    }
}
