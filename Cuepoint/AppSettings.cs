using System.Text.Json;
using System.Text.Encodings.Web;
using Cuepoint.UI;
using Cuepoint.Audio;

namespace Cuepoint;

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
    /// The playback device carrying the main mix to the booth monitors too, or null for none; and their volume.
    /// </summary>
    public string? BoothDevice { get; set; }

    public float BoothVolume { get; set; } = 0.8f;

    /// <summary>
    /// The output's buffer size asked for, in sample frames; null for the driver's own.
    /// </summary>
    public int? BufferFrames { get; set; }

    /// <summary>
    /// Whether to find lighting software and tell it what the decks are doing, by OS2L.
    /// </summary>
    public bool Os2lEnabled { get; set; } = true;

    /// <summary>
    /// The decks with quantise turned off, by name; it's on for the rest.
    /// </summary>
    public List<string>? UnquantizedDecks { get; set; }

    /// <summary>
    /// How many seconds of song the scrolling waveforms show across, how they're coloured, whether they show the beat
    /// grid, and whether they're tall.
    /// </summary>
    public float WaveformSeconds { get; set; } = 12;

    public string? WaveformColours { get; set; }
    public bool WaveformBeatGrid { get; set; } = true;
    public bool TallWaveforms { get; set; }

    /// <summary>
    /// Rekordbox collections chosen to import, by path, as well as any found where exports usually go.
    /// </summary>
    public List<string>? RekordboxXmlFiles { get; set; }

    /// <summary>
    /// Whether the master limiter keeps the mix from clipping.
    /// </summary>
    public bool MasterLimiter { get; set; } = true;

    /// <summary>
    /// The crossfader's curve, by name: Smooth or Sharp; null, or one not known, for Smooth.
    /// </summary>
    public string? CrossfaderCurve { get; set; }

    /// <summary>
    /// The look chosen (see <see cref="ThemeKind"/>), taken on as the app starts; null for the usual dark one.
    /// </summary>
    public string? Theme { get; set; }

    /// <summary>
    /// Whether a song can't be loaded onto a deck that's playing into the main mix.
    /// </summary>
    public bool ProtectPlayingDecks { get; set; } = true;

    /// <summary>
    /// Whether songs not analysed yet are analysed, one at a time, while nothing's playing.
    /// </summary>
    public bool AnalyseWhenIdle { get; set; } = true;

    /// <summary>
    /// Whether a song with no cue point of its own loads cued to where its music starts.
    /// </summary>
    public bool AutoCue { get; set; } = true;

    /// <summary>
    /// The saved searches listed under Smart playlists.
    /// </summary>
    public List<Music.SmartPlaylist> SmartPlaylists { get; set; } = [];

    /// <summary>
    /// A deck's effect as it was left: which one, how much, and each one's length in beats. It always starts off.
    /// </summary>
    public sealed class DeckEffectSettings
    {
        public DeckEffect Kind { get; set; }
        public float Level { get; set; } = 0.6f;
        public double EchoBeats { get; set; } = 0.5;
        public double FlangerBeats { get; set; } = 8;
        public double GateBeats { get; set; } = 0.25;
    }

    /// <summary>
    /// Each deck's effect, by the deck's name.
    /// </summary>
    public Dictionary<string, DeckEffectSettings>? Effects { get; set; }

    /// <summary>
    /// The effect on the whole mix: which, how much and how long; it always starts off.
    /// </summary>
    public DeckEffectSettings? MasterEffect { get; set; }

    /// <summary>
    /// The recording device the microphone's on, or null for none.
    /// </summary>
    public string? MicrophoneDevice { get; set; }

    public float MicrophoneLevel { get; set; } = 0.7f;

    /// <summary>
    /// Whether the music's turned down while the microphone's spoken into.
    /// </summary>
    public bool Talkover { get; set; } = true;

    /// <summary>
    /// Lighting software to connect to as well as any found on the network, as host:port; null for none.
    /// </summary>
    public string? Os2lAddress { get; set; }

    /// <summary>
    /// The file on each sampler pad, in order; null for an empty one.
    /// </summary>
    public List<string?>? SamplerSlots { get; set; }

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
