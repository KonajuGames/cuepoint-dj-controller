using System.Text.Json.Serialization;

namespace Brutality.Music;

/// <summary>
/// What the DJ's noted about a song: a rating from 0 (none) to <see cref="MAX_RATING"/>, a comment, and one of
/// <see cref="Colors"/> to tag it with. Kept apart from the analysis, as hot cues are.
/// </summary>
sealed record SongNotes(int Rating = 0, string? Comment = null, string? Color = null)
{
    public const int MAX_RATING = 5;

    public static readonly SongNotes None = new();

    /// <summary>
    /// The colours a song can be tagged with, as they're saved and searched for.
    /// </summary>
    public static readonly IReadOnlyList<string> Colors = ["red", "orange", "yellow", "green", "blue", "purple", "pink"];

    /// <summary>
    /// Whether there's nothing noted, so nothing to keep.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => Rating == 0 && Comment is null && Color is null;
}
