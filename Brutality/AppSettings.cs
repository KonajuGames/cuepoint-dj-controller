using System.Text.Json;
using System.Text.Encodings.Web;

namespace Brutality;

/// <summary>
/// The app's settings, kept between runs in a JSON file: for now, the headphones. Anything missing or unreadable
/// comes back as its default.
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
    /// The playback device the headphones are on, by name; null for none.
    /// </summary>
    public string? HeadphoneDevice { get; set; }

    /// <summary>
    /// What the headphones hear, from 0 (only the decks sent to them) to 1 (only the main mix).
    /// </summary>
    public float CueMix { get; set; }

    public float HeadphoneVolume { get; set; } = 0.8f;

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
