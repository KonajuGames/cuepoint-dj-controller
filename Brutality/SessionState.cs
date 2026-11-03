using System.Text.Encodings.Web;
using System.Text.Json;
using Brutality.Audio;

namespace Brutality;

/// <summary>
/// What each deck is doing, written down every few seconds while the app runs and deleted as it closes. Found when it
/// starts, it means the app didn't close properly last time, a crash, say, so the decks can pick up where they were.
/// </summary>
sealed class SessionState
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// When it was written, in UTC, to tell how long ago the decks were last where it says.
    /// </summary>
    public DateTime SavedAt { get; set; }

    public List<DeckState> Decks { get; set; } = [];

    /// <summary>
    /// One deck: its song, how far in, whether it was playing, and its tempo fader and key lock.
    /// </summary>
    public sealed class DeckState
    {
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public double Position { get; set; }
        public bool Playing { get; set; }
        public double Tempo { get; set; }
        public bool KeyLocked { get; set; }
        public bool Slip { get; set; }
        public int KeyShift { get; set; }
    }

    /// <summary>
    /// The decks as they are now.
    /// </summary>
    public static SessionState Of(IEnumerable<Deck> decks) =>
        new()
        {
            SavedAt = DateTime.UtcNow,
            Decks = decks
                .Select(deck => new DeckState
                {
                    Name = deck.Name,
                    Path = deck.FilePath,
                    Position = Math.Round(deck.Position.TotalSeconds, 3),
                    Playing = deck.IsPlaying,
                    Tempo = deck.Tempo,
                    KeyLocked = deck.KeyLocked,
                    Slip = deck.Slip,
                    KeyShift = deck.KeyShift,
                })
                .ToList(),
        };

    /// <summary>
    /// Where a deck's song had got to <paramref name="now"/>, if it carried on playing from when this was saved: for
    /// putting it back where the room would expect after a restart. Paused, it's where it was.
    /// </summary>
    public double PositionAt(DeckState deck, DateTime now) =>
        deck.Playing ? deck.Position + Math.Max(0, (now - SavedAt).TotalSeconds) * (1 + deck.Tempo) : deck.Position;

    /// <summary>
    /// The session left at <paramref name="path"/>, or null if there isn't one (the app closed properly) or it can't
    /// be read.
    /// </summary>
    public static SessionState? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes it to a temporary file then swaps it in, so a crash mid-write leaves the last one whole. Returns why it
    /// couldn't, if it couldn't.
    /// </summary>
    public string? Save(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Removes the session at <paramref name="path"/>, as the app closes properly.
    /// </summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind, it's only offered back next time.
        }
    }
}
