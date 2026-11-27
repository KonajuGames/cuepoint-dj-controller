using System.Reflection;
using System.Text.Json;
using Brutal.GlfwApi;

namespace Cuepoint;

/// <summary>
/// One key that can be changed: what it's for, on which deck (-1 for the mixer's), and the key it is.
/// </summary>
sealed record KeyBinding(string Id, string Name, int Deck, GlfwKey Key);

/// <summary>
/// Which keys work the decks, their hotcue pads and the crossfader, as changed from the usual ones and kept in
/// keys.json. Each key does one thing: given to another, it swaps with the key that was there. The modifiers that go
/// with them (Shift for volume, Ctrl and Shift to nudge, Alt for key shift) carry on with whatever key's chosen.
/// Keys that browse, load, search and the rest stay as they are, so the lists always work.
/// </summary>
sealed class KeyMap
{
    /// <summary>
    /// Keys that can't be given to a deck: they browse, load, search or show the shortcuts, or are modifiers.
    /// </summary>
    static readonly HashSet<GlfwKey> Reserved =
    [
        GlfwKey.Number1, GlfwKey.Number2, GlfwKey.Slash, GlfwKey.Escape, GlfwKey.Tab, GlfwKey.Enter, GlfwKey.KpEnter,
        GlfwKey.Up, GlfwKey.Down, GlfwKey.Left, GlfwKey.Right, GlfwKey.PageUp, GlfwKey.PageDown, GlfwKey.Home, GlfwKey.End,
        GlfwKey.LeftShift, GlfwKey.RightShift, GlfwKey.LeftControl, GlfwKey.RightControl, GlfwKey.LeftAlt, GlfwKey.RightAlt,
        GlfwKey.LeftSuper, GlfwKey.RightSuper, GlfwKey.Space, GlfwKey.Unknown,
    ];

    /// <summary>
    /// What each of a deck's own keys is for, in the order they're listed.
    /// </summary>
    static readonly (string Property, string Name)[] DeckActions =
    [
        ("Cue", "Cue"),
        ("Play", $"Play ({GlfwUiInput.CtrlName}: censor)"),
        ("Sync", "Sync"),
        ("Loop", "Loop (Shift: reloop)"),
        ("HalveLoop", "Halve loop (Shift: loop in)"),
        ("DoubleLoop", "Double loop (Shift: loop out)"),
        ("TempoDown", "Tempo down (Shift: volume)"),
        ("TempoUp", "Tempo up (Shift: volume)"),
        ("Pfl", "Headphone cue"),
        ("KeyLock", "Key lock (Shift: slip)"),
        ("KillLow", "Kill low"),
        ("KillMid", "Kill mid"),
        ("KillHigh", "Kill high"),
        ("ResetEq", "Reset EQ"),
        ("LowPass", "Low-pass filter"),
        ("HighPass", "High-pass filter"),
    ];

    public DeckKeys[] Decks { get; private set; }
    public GlfwKey[][] Hotcues { get; private set; }
    public GlfwKey CrossfaderLeft { get; private set; }
    public GlfwKey CrossfaderRight { get; private set; }
    public GlfwKey CrossfaderCentre { get; private set; }

    KeyMap(DeckKeys[] decks, GlfwKey[][] hotcues, GlfwKey left, GlfwKey right, GlfwKey centre) =>
        (Decks, Hotcues, CrossfaderLeft, CrossfaderRight, CrossfaderCentre) = (decks, hotcues, left, right, centre);

    /// <summary>
    /// The keys Cuepoint comes with.
    /// </summary>
    public static KeyMap Usual() =>
        new([.. KeyboardControls.UsualKeys], [.. HotcueKeys.UsualKeys.Select(keys => keys.ToArray())], GlfwKey.LeftBracket, GlfwKey.RightBracket, GlfwKey.B);

    /// <summary>
    /// Every binding, deck by deck and then the crossfader's, for listing and changing.
    /// </summary>
    public IReadOnlyList<KeyBinding> Bindings
    {
        get
        {
            var bindings = new List<KeyBinding>();
            for (var deck = 0; deck < Decks.Length; deck++)
            {
                foreach (var (property, name) in DeckActions)
                    bindings.Add(new($"{deck}.{property}", name, deck, (GlfwKey)typeof(DeckKeys).GetProperty(property)!.GetValue(Decks[deck])!));
                for (var pad = 0; pad < Hotcues[deck].Length; pad++)
                    bindings.Add(new($"{deck}.hotcue{pad + 1}", $"Hotcue {pad + 1}", deck, Hotcues[deck][pad]));
            }
            bindings.Add(new("crossfader.left", "Crossfader left", -1, CrossfaderLeft));
            bindings.Add(new("crossfader.right", "Crossfader right", -1, CrossfaderRight));
            bindings.Add(new("crossfader.centre", "Crossfader centre", -1, CrossfaderCentre));
            return bindings;
        }
    }

    /// <summary>
    /// Why <paramref name="key"/> can't be given to anything; null if it can.
    /// </summary>
    public static string? CheckKey(GlfwKey key) =>
        Reserved.Contains(key) ? $"{DeckKeys.Label(key)} is kept for browsing, loading and the like" : null;

    /// <summary>
    /// Gives <paramref name="key"/> to the binding <paramref name="id"/>; whatever had it takes that binding's old key,
    /// so no key does two things. Returns the binding that swapped, if one did, or throws
    /// <see cref="ArgumentException"/> for a key that can't be given.
    /// </summary>
    public KeyBinding? Set(string id, GlfwKey key)
    {
        if (CheckKey(key) is { } problem)
            throw new ArgumentException(problem, nameof(key));
        var bindings = Bindings;
        var binding = bindings.Single(b => b.Id == id);
        var other = bindings.FirstOrDefault(b => b.Key == key && b.Id != id);
        Put(binding.Id, key);
        if (other is not null)
            Put(other.Id, binding.Key);
        return other is null ? null : other with { Key = binding.Key };
    }

    void Put(string id, GlfwKey key)
    {
        switch (id)
        {
            case "crossfader.left":
                CrossfaderLeft = key;
                return;
            case "crossfader.right":
                CrossfaderRight = key;
                return;
            case "crossfader.centre":
                CrossfaderCentre = key;
                return;
        }
        var dot = id.IndexOf('.');
        var deck = int.Parse(id[..dot]);
        var what = id[(dot + 1)..];
        if (what.StartsWith("hotcue"))
        {
            Hotcues[deck][int.Parse(what["hotcue".Length..]) - 1] = key;
            return;
        }
        // A copy with the one key changed, as the record's keys are fixed once it's made.
        var keys = Decks[deck] with { };
        typeof(DeckKeys).GetProperty(what, BindingFlags.Public | BindingFlags.Instance)!.SetValue(keys, key);
        Decks[deck] = keys;
    }

    /// <summary>
    /// Makes these the keys the keyboard works by.
    /// </summary>
    public void Apply()
    {
        KeyboardControls.Keys = [.. Decks];
        HotcueKeys.Keys = [.. Hotcues.Select(keys => keys.ToArray())];
        (KeyboardControls.CrossfaderLeft, KeyboardControls.CrossfaderRight, KeyboardControls.CrossfaderCentre) = (CrossfaderLeft, CrossfaderRight, CrossfaderCentre);
    }

    sealed class Saved
    {
        public Dictionary<string, string> Keys { get; set; } = [];
    }

    /// <summary>
    /// The keys kept at <paramref name="path"/>, laid over the usual ones; the usual ones if there's no file, or it
    /// can't be read, or it would give a key two things to do.
    /// </summary>
    public static KeyMap Load(string path)
    {
        var map = Usual();
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is not { } saved)
                return map;
            var ids = map.Bindings.Select(binding => binding.Id).ToHashSet();
            foreach (var (id, name) in saved.Keys)
            {
                if (ids.Contains(id) && Enum.TryParse<GlfwKey>(name, out var key) && CheckKey(key) is null)
                    map.Put(id, key);
            }
            var keys = map.Bindings.Select(binding => binding.Key).ToList();
            return keys.Distinct().Count() == keys.Count ? map : Usual();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Usual();
        }
    }

    /// <summary>
    /// Keeps the keys that differ from the usual ones at <paramref name="path"/>. Returns why it couldn't, if it
    /// couldn't.
    /// </summary>
    public string? Save(string path)
    {
        try
        {
            var usual = Usual().Bindings.ToDictionary(binding => binding.Id, binding => binding.Key);
            var changed = Bindings.Where(binding => usual[binding.Id] != binding.Key).ToDictionary(binding => binding.Id, binding => binding.Key.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Saved { Keys = changed }, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
