using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brutality.Midi;

/// <summary>
/// How a control reads what it's sent.
/// </summary>
enum ControlKind
{
    /// <summary>A button: acts on the press, and some (cue) on the release too.</summary>
    Button,

    /// <summary>A fader, from one end to the other.</summary>
    Fader,

    /// <summary>A knob with a centre detent, such as an EQ: the middle is "unchanged".</summary>
    Knob,

    /// <summary>A jog wheel or endless encoder: each message is a step one way or the other.</summary>
    Encoder,
}

/// <summary>
/// Something a controller can work: an ID for mapping files ("A.play", "crossfader"), a name for people, and
/// whether it has a light to keep in step.
/// </summary>
sealed record ControlInfo(string Id, string Name, ControlKind Kind, bool HasLight = false);

static class ControlCatalog
{
    public const int HOTCUES = 8;

    /// <summary>
    /// Every control, for decks with these names.
    /// </summary>
    public static IReadOnlyList<ControlInfo> For(IEnumerable<string> deckNames)
    {
        var controls = new List<ControlInfo>();
        foreach (var deck in deckNames)
        {
            controls.AddRange(
                [
                    new($"{deck}.play", $"Deck {deck} play", ControlKind.Button, HasLight: true),
                    new($"{deck}.cue", $"Deck {deck} cue", ControlKind.Button, HasLight: true),
                    new($"{deck}.sync", $"Deck {deck} sync", ControlKind.Button, HasLight: true),
                    new($"{deck}.load", $"Load deck {deck}", ControlKind.Button),
                    new($"{deck}.pfl", $"Deck {deck} headphones", ControlKind.Button, HasLight: true),
                    new($"{deck}.keylock", $"Deck {deck} key lock", ControlKind.Button, HasLight: true),
                    new($"{deck}.loop", $"Deck {deck} loop", ControlKind.Button, HasLight: true),
                    new($"{deck}.loop.halve", $"Deck {deck} halve loop", ControlKind.Button),
                    new($"{deck}.loop.double", $"Deck {deck} double loop", ControlKind.Button),
                    new($"{deck}.grid", $"Deck {deck} grid mode (pads fix the beat grid)", ControlKind.Button, HasLight: true),
                ]
            );
            for (var i = 1; i <= HOTCUES; i++)
                controls.Add(new($"{deck}.hotcue{i}", $"Deck {deck} hotcue {i}", ControlKind.Button, HasLight: true));
            for (var i = 1; i <= HOTCUES; i++)
                controls.Add(new($"{deck}.hotcue{i}.delete", $"Deck {deck} delete hotcue {i}", ControlKind.Button, HasLight: true));
            controls.AddRange(
                [
                    new($"{deck}.tempo", $"Deck {deck} tempo", ControlKind.Fader),
                    new($"{deck}.tempo.reset", $"Deck {deck} reset tempo", ControlKind.Button),
                    new($"{deck}.volume", $"Deck {deck} volume", ControlKind.Fader),
                    new($"{deck}.eq.high", $"Deck {deck} high", ControlKind.Knob),
                    new($"{deck}.eq.mid", $"Deck {deck} mid", ControlKind.Knob),
                    new($"{deck}.eq.low", $"Deck {deck} low", ControlKind.Knob),
                    new($"{deck}.filter", $"Deck {deck} filter", ControlKind.Knob),
                    new($"{deck}.jog", $"Deck {deck} jog wheel", ControlKind.Encoder),
                    new($"{deck}.search", $"Deck {deck} fast search", ControlKind.Encoder),
                ]
            );
        }
        controls.AddRange(
            [
                new("crossfader", "Crossfader", ControlKind.Fader),
                new("browse", "Browse songs", ControlKind.Encoder),
                new("browse.folders", "Browse folders", ControlKind.Encoder),
                new("browse.press", "Open or close folder", ControlKind.Button),
            ]
        );
        return controls;
    }
}

/// <summary>
/// How a relative control counts its steps.
/// </summary>
enum StepEncoding
{
    /// <summary>64 is still; 65 is one step forward, 63 one back.</summary>
    Offset64,

    /// <summary>1 is one step forward, 127 one back (seven-bit two's complement).</summary>
    TwosComplement,
}

/// <summary>
/// One message bound to one control. A fader or knob sent at fourteen-bit resolution has its fine part on a second
/// control number, <see cref="Fine"/> (by convention 32 above the coarse one).
/// </summary>
sealed record MidiBinding
{
    public required string Control { get; init; }
    public MidiKind Kind { get; init; }

    /// <summary>
    /// From 0 to 15.
    /// </summary>
    public int Channel { get; init; }

    public int Number { get; init; }
    public int? Fine { get; init; }

    /// <summary>
    /// Whether the control's low end is this control's high end, as tempo faders often are.
    /// </summary>
    public bool Invert { get; init; }

    public StepEncoding Steps { get; init; }

    public bool Matches(MidiMessage message) =>
        message.Kind == Kind && message.Channel == Channel && (message.Number == Number || (Kind == MidiKind.ControlChange && message.Number == Fine));

    public override string ToString() =>
        Kind == MidiKind.Note ? $"Note {Number}, ch {Channel + 1}" : $"CC {Number}{(Fine is { } fine ? $"/{fine}" : "")}, ch {Channel + 1}";
}

/// <summary>
/// A controller's bindings, kept per device by name.
/// </summary>
sealed class ControllerMapping
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Device { get; set; } = "";
    public List<MidiBinding> Bindings { get; set; } = [];

    public ControllerMapping Copy() => new() { Device = Device, Bindings = [.. Bindings] };

    /// <summary>
    /// Binds <paramref name="binding"/>'s control to its message alone: whatever else the control had, and whatever
    /// else had the message, goes.
    /// </summary>
    public void Bind(MidiBinding binding)
    {
        Bindings.RemoveAll(other =>
            other.Control == binding.Control
            || (other.Kind == binding.Kind && other.Channel == binding.Channel && (other.Number == binding.Number || other.Fine == binding.Number))
        );
        Bindings.Add(binding);
    }

    public void Unbind(string control) => Bindings.RemoveAll(binding => binding.Control == control);

    public static ControllerMapping? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ControllerMapping>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the mapping to a temporary file then swaps it in. Returns why it couldn't, if it couldn't.
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

    /// <summary>
    /// Where a device's own mapping is kept in <paramref name="folder"/>: its name, with anything a file name can't
    /// hold replaced.
    /// </summary>
    public static string PathFor(string folder, string device)
    {
        var safe = string.Concat(device.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(folder, (safe == "" ? "controller" : safe) + ".json");
    }
}
