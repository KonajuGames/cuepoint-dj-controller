using System.Text.Json;
using System.Text.Encodings.Web;
using Brutality.UI;

namespace Brutality;

/// <summary>
/// The app's settings, kept between runs in a JSON file: the outputs and headphones, lighting, and how the song
/// browser was left. Anything missing or unreadable comes back as its default.
/// </summary>
sealed class AppSettings
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The playback device the main mix goes out of, by name; null for the system's default.
    /// </summary>
    public string? OutputDevice { get; set; }

    /// <summary>
    /// The playback device the headphones are on, by name; null for none. The output device's own name means its
    /// channels 3 and 4.
    /// </summary>
    public string? HeadphoneDevice { get; set; }

    /// <summary>
    /// What the headphones hear, from 0 (only the decks sent to them) to 1 (only the main mix).
    /// </summary>
    public float CueMix { get; set; }

    public float HeadphoneVolume { get; set; } = 0.8f;

    /// <summary>
    /// Whether to find lighting software and tell it what the decks are doing, by OS2L.
    /// </summary>
    public bool Os2lEnabled { get; set; } = true;

    /// <summary>
    /// The decks with quantise turned off, by name; it's on for the rest.
    /// </summary>
    public List<string>? UnquantizedDecks { get; set; }

    /// <summary>
    /// Whether the master limiter keeps the mix from clipping.
    /// </summary>
    public bool MasterLimiter { get; set; } = true;

    /// <summary>
    /// Lighting software to connect to as well as any found on the network, as host:port; null for none.
    /// </summary>
    public string? Os2lAddress { get; set; }

    /// <summary>
    /// The folder the music library is made from; null for Windows' own Music folder.
    /// </summary>
    public string? MusicFolder { get; set; }

    /// <summary>
    /// The folder recordings of the mix go in; null for Cuepoint Recordings in Windows' own Music folder.
    /// </summary>
    public string? RecordingsFolder { get; set; }

    /// <summary>
    /// Whether the folder list and song list are in large text.
    /// </summary>
    public bool LargeListText { get; set; }

    /// <summary>
    /// How the song list was laid out: its sort, and its columns.
    /// </summary>
    public BrowserLayout Browser { get; set; } = new();

    /// <summary>
    /// What was last chosen in the folder list, by the names down to it; null for the library.
    /// </summary>
    public List<string>? BrowserFolder { get; set; }

    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// Writes the settings to a temporary file then swaps it in, so a crash mid-write leaves the last good copy.
    /// Returns why it couldn't, if it couldn't.
    /// </summary>
    public string? Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
}
