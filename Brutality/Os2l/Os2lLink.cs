using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brutality.Audio;

namespace Brutality.Os2l;

/// <summary>
/// Tells lighting software what the decks are doing, by OS2L (Open Sound to Light). The lighting software is the
/// server: it's found on the network by DNS-SD (as "_os2l._tcp"), or given directly as host:port, and every one found
/// is connected to. Each is sent:
/// <list type="bullet">
/// <item>a beat message on every beat of the deck you can hear, numbered so bars start on multiples of four;</item>
/// <item>a button message, "Deck 1 Play" on or off and so on, as each deck starts and stops;</item>
/// <item>and the values it subscribes to, by the extension VirtualDJ and SoundSwitch use: the lighting software
/// sends {"evt":"subscribe","trigger":[...],"frequency":"100"} with VDJScript expressions such as "deck 1 play", and
/// is sent {"evt":"subscribed","trigger":...,"value":...} for each, then again whenever one changes (checked at the
/// frequency asked, in milliseconds).</item>
/// </list>
/// UI thread only: call <see cref="Update"/> every frame.
/// </summary>
sealed class Os2lLink : IDisposable
{
    public const string SERVICE_TYPE = "_os2l._tcp";

    static readonly TimeSpan Retry = TimeSpan.FromSeconds(3);
    const double DEFAULT_FREQUENCY_MS = 100;

    readonly IReadOnlyList<Deck> _decks;
    readonly AudioEngine _engine;
    readonly Func<string, MdnsBrowser?> _createBrowser;
    MdnsBrowser? _browser;
    bool _enabled;
    string? _directAddress;
    IPEndPoint? _directEndPoint;

    sealed class Peer(Os2lConnection connection)
    {
        public Os2lConnection Connection { get; } = connection;
        public bool Announced;
        public List<string> Triggers = [];
        public double FrequencySeconds = DEFAULT_FREQUENCY_MS / 1000;
        public double NextCheck;
        public readonly Dictionary<string, string> Sent = [];
        public readonly HashSet<string> Unsupported = [];
    }

    // Connections by what they're to, and when one that closed may be tried again.
    readonly Dictionary<string, Peer> _peers = [];
    readonly Dictionary<string, DateTime> _retryAt = [];

    // Each deck's play state as last reported.
    readonly Dictionary<Deck, bool> _playing = [];

    // The beat being followed: the deck, its song, the last whole beat, the tempo, and whether the next beat message
    // says the count jumped.
    Deck? _beatDeck;
    string? _beatSong;
    long? _lastBeat;
    double _lastBpm;
    bool _beatChange = true;

    public Os2lLink(IReadOnlyList<Deck> decks, AudioEngine engine, Func<string, MdnsBrowser?>? createBrowser = null)
    {
        _decks = decks;
        _engine = engine;
        _createBrowser = createBrowser ?? (service =>
        {
            try
            {
                return new MdnsBrowser(service);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// News worth a line on the status bar: connections made and lost, and subscriptions it can't answer.
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// Whether to look for lighting software and talk to it. Off closes every connection.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (!value)
            {
                CloseAll();
                _browser?.Dispose();
                _browser = null;
            }
        }
    }

    /// <summary>
    /// A server to connect to as well as any found, as host:port (as VirtualDJ's os2lDirectIp); null for none. A host
    /// name is looked up once, here.
    /// </summary>
    public string? DirectAddress
    {
        get => _directAddress;
        set
        {
            _directAddress = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _directEndPoint = ParseAddress(_directAddress);
            if (_directAddress is not null && _directEndPoint is null)
                Log?.Invoke($"Lighting: \"{_directAddress}\" isn't an address and port to connect to, such as 127.0.0.1:9960");
        }
    }

    /// <summary>
    /// Whether anything's connected, so beats need sending even with the window minimised.
    /// </summary>
    public bool IsConnected => _peers.Values.Any(peer => peer.Connection.IsConnected);

    /// <summary>
    /// What it's connected to, or what it's doing, in a few words.
    /// </summary>
    public string Status
    {
        get
        {
            if (!_enabled)
                return "Off";
            var connected = _peers.Values.Where(peer => peer.Connection.IsConnected).Select(peer => peer.Connection.Name).ToList();
            return connected.Count > 0 ? "Connected to " + string.Join(", ", connected) : "Looking for lighting software";
        }
    }

    /// <summary>
    /// Parses host:port, by name or address; null if it won't do.
    /// </summary>
    public static IPEndPoint? ParseAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return null;
        var host = text[..colon].Trim();
        if (IPAddress.TryParse(host, out var address))
            return new IPEndPoint(address, port);
        try
        {
            var found = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return found is null ? null : new IPEndPoint(found, port);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Connects to what's been found, handles what's come in, and sends what's changed. <paramref name="now"/> is in
    /// seconds, from any start.
    /// </summary>
    public void Update(double now)
    {
        if (!_enabled)
            return;
        _browser ??= _createBrowser(SERVICE_TYPE);

        Connect();
        foreach (var peer in _peers.Values)
            Receive(peer);

        SendPlayStates();
        SendBeats();
        foreach (var peer in _peers.Values)
        {
            if (peer.Connection.IsConnected && peer.Triggers.Count > 0 && now >= peer.NextCheck)
            {
                peer.NextCheck = now + peer.FrequencySeconds;
                SendSubscribed(peer, all: false);
            }
        }
    }

    /// <summary>
    /// Opens a connection to each server found or given that hasn't one, closes those to servers gone, and says when
    /// one opens or closes.
    /// </summary>
    void Connect()
    {
        var targets = new Dictionary<string, (string Name, IPEndPoint EndPoint)>();
        if (_browser is not null)
        {
            foreach (var (name, endPoint) in _browser.Services)
                targets[endPoint.ToString()] = (name, endPoint);
        }
        if (_directEndPoint is { } direct)
            targets.TryAdd(direct.ToString(), (_directAddress!, direct));

        foreach (var (key, peer) in _peers.ToList())
        {
            var connection = peer.Connection;
            if (connection.IsConnected && !peer.Announced)
            {
                peer.Announced = true;
                Log?.Invoke($"Lighting: connected to {connection.Name} ({connection.EndPoint})");
                SendPlayStates(peer);
            }
            if (connection.IsClosed || !targets.ContainsKey(key))
            {
                if (peer.Announced)
                    Log?.Invoke($"Lighting: {connection.Name} disconnected{(connection.Error is { } error ? $" ({error})" : "")}");
                connection.Dispose();
                _peers.Remove(key);
                _retryAt[key] = DateTime.UtcNow + Retry;
            }
        }

        foreach (var (key, (name, endPoint)) in targets)
        {
            if (_peers.ContainsKey(key) || _retryAt.TryGetValue(key, out var at) && DateTime.UtcNow < at)
                continue;
            _peers[key] = new Peer(new Os2lConnection(name, endPoint));
        }
    }

    void Receive(Peer peer)
    {
        while (peer.Connection.TryReceive(out var text))
        {
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (message?["evt"]?.GetValueKind() != JsonValueKind.String)
                continue;

            switch ((string)message["evt"]!)
            {
                case "subscribe":
                    Subscribe(peer, message);
                    break;
                case "unsubscribe":
                    peer.Triggers.Clear();
                    peer.Sent.Clear();
                    break;
            }
            // Feedback (lighting a button) has nothing here to light.
        }
    }

    /// <summary>
    /// Takes a subscription: one trigger or a list (each subscribe replacing the last, as VirtualDJ treats them), and
    /// how often to check, in milliseconds, as a number or a string. Every value goes back straight away.
    /// </summary>
    void Subscribe(Peer peer, JsonObject message)
    {
        var triggers = message["trigger"] switch
        {
            JsonArray list => list.Where(node => node?.GetValueKind() == JsonValueKind.String).Select(node => (string)node!).ToList(),
            JsonValue one when one.GetValueKind() == JsonValueKind.String => [(string)one!],
            _ => [],
        };
        peer.Triggers = triggers.Distinct().ToList();
        peer.Sent.Clear();

        var frequency = message["frequency"] switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.Number => (double)value,
            JsonValue value when double.TryParse((string?)value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => DEFAULT_FREQUENCY_MS,
        };
        peer.FrequencySeconds = Math.Clamp(frequency, 10, 5000) / 1000;
        SendSubscribed(peer, all: true);
    }

    void SendSubscribed(Peer peer, bool all)
    {
        foreach (var trigger in peer.Triggers)
        {
            if (Evaluate(trigger) is not { } value)
            {
                if (peer.Unsupported.Add(trigger))
                    Log?.Invoke($"Lighting: {peer.Connection.Name} asked for \"{trigger}\", which Cuepoint doesn't report");
                continue;
            }
            var json = value.ToJsonString();
            if (!all && peer.Sent.TryGetValue(trigger, out var last) && last == json)
                continue;
            peer.Sent[trigger] = json;
            peer.Connection.Send(Os2lMessages.Subscribed(trigger, value));
        }
    }

    /// <summary>
    /// What a VDJScript trigger is now, for the ones that say what a deck or the mixer is doing; null for any other.
    /// Decks count from 1. Units follow VirtualDJ: times in milliseconds, levels from 0 to 1, switches "on" or "off".
    /// </summary>
    public JsonNode? Evaluate(string trigger)
    {
        var words = trigger.Trim();
        switch (words)
        {
            case "crossfader":
                return JsonValue.Create(Round(_engine.Crossfader));
            case "get_decks":
                return JsonValue.Create(_decks.Count);
            case "get_activedeck" or "master":
                return JsonValue.Create(MasterDeck() is { } master ? IndexOf(master) + 1 : 0);
        }

        // "deck 2 get_bpm", and so on.
        var parts = words.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "deck" || !int.TryParse(parts[1], out var number) || number < 1 || number > _decks.Count)
            return null;
        var deck = _decks[number - 1];
        var verb = parts[2];
        var analysis = deck.IsLoaded ? deck.Analysis : null;
        var seconds = deck.Position.TotalSeconds;

        switch (verb)
        {
            case "play":
                return OnOff(deck.IsPlaying);
            case "loop":
                return OnOff(deck.IsLooping);
            case "get_loop":
                return JsonValue.Create(Round(deck.LoopBeats));
            case "level" or "volume":
                return JsonValue.Create(Round(deck.Volume));
            case "get_filepath":
                return JsonValue.Create(deck.IsLoaded && deck.FilePath is { } path ? path.Replace('/', '\\') : "");
            case "get_title":
                return JsonValue.Create(deck.IsLoaded ? deck.Title : "");
            case "get_artist":
                return JsonValue.Create(deck.IsLoaded ? deck.Artist : "");
            case "get_bpm":
                return JsonValue.Create(Round(deck.Bpm ?? 0));
            case "get_beatpos":
                return JsonValue.Create(analysis is null ? 0 : Round(analysis.Grid.BeatNumberAt(seconds) - analysis.Grid.BeatNumberAt(analysis.Grid.FirstBeat)));
            case "get_firstbeat":
                return JsonValue.Create(analysis is null ? 0 : (long)Math.Round(analysis.Grid.FirstBeat * 1000));
            case "get_songlength":
                return JsonValue.Create(Round(deck.Duration.TotalSeconds));
            case "get_time" or "get_time elapsed" or "get_time elapsed absolute":
                return JsonValue.Create((long)Math.Round(deck.Position.TotalMilliseconds));
            case "get_time remain" or "get_time remain absolute":
                return JsonValue.Create((long)Math.Round(Math.Max(0, (deck.Duration - deck.Position).TotalMilliseconds)));
            case "get_text '%SOUNDSWITCH_ID'":
                // SoundSwitch's own ID for the song's light show, kept in the file's tags by its software; there's
                // none here.
                return JsonValue.Create("");
        }

        // SoundSwitch asks which loop roll is held with an expression of "loop_roll" tests; there's no loop roll.
        if (verb.StartsWith("loop_roll", StringComparison.Ordinal))
            return JsonValue.Create(0);
        return null;
    }

    static JsonNode OnOff(bool on) => JsonValue.Create(on ? "on" : "off");

    static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 4) : 0;

    int IndexOf(Deck deck)
    {
        for (var i = 0; i < _decks.Count; i++)
        {
            if (_decks[i] == deck)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The deck the room hears: the only one playing, or of several the loudest through its fader and the
    /// crossfader (the first, if they're level). Holding Cue to listen from the cue point isn't playing.
    /// </summary>
    public Deck? MasterDeck()
    {
        Deck? best = null;
        var loudest = -1f;
        foreach (var deck in _decks)
        {
            if (!deck.IsPlaying || deck.IsCuePreviewing)
                continue;
            var level = Deck.VolumeTaper(deck.Volume) * _engine.CrossfaderGain(deck.Side);
            if (level > loudest)
                (best, loudest) = (deck, level);
        }
        return best;
    }

    /// <summary>
    /// Sends each deck's play state that's changed to every connection, or every deck's as it is now to just
    /// <paramref name="only"/> (one just connected), without counting that as reporting a change.
    /// </summary>
    void SendPlayStates(Peer? only = null)
    {
        foreach (var deck in _decks)
        {
            var playing = deck.IsPlaying && !deck.IsCuePreviewing;
            var message = Os2lMessages.Button($"Deck {IndexOf(deck) + 1} Play", playing);
            if (only is not null)
            {
                only.Connection.Send(message);
                continue;
            }
            if (_playing.TryGetValue(deck, out var was) && was == playing)
                continue;
            _playing[deck] = playing;
            foreach (var peer in _peers.Values)
                peer.Connection.Send(message);
        }
    }

    /// <summary>
    /// Sends a beat as the deck heard crosses each one in its grid. The beat's number counts from the song's first
    /// bar, so it's a multiple of four on each downbeat; it's marked as a change when the deck heard, its song, or its
    /// tempo changes, or the count jumps (a seek, a loop going round).
    /// </summary>
    void SendBeats()
    {
        var deck = MasterDeck();
        if (deck?.Analysis is not { } analysis || deck.Bpm is not { } bpm)
        {
            _beatDeck = null;
            _lastBeat = null;
            _beatChange = true;
            return;
        }

        if (deck != _beatDeck || deck.FilePath != _beatSong)
        {
            (_beatDeck, _beatSong) = (deck, deck.FilePath);
            _lastBeat = null;
            _beatChange = true;
        }

        var grid = analysis.Grid;
        var beat = (long)Math.Floor(grid.BeatNumberAt(deck.Position.TotalSeconds) + 1e-6);
        if (_lastBeat is not { } last)
        {
            // Wait for the next beat to come round, rather than sending one mid-beat.
            _lastBeat = beat;
            return;
        }
        if (beat == last)
            return;
        _lastBeat = beat;

        // A jump (a seek, a loop going round) lands mid-beat: the next beat to come round is the one to send.
        if (beat != last + 1)
        {
            _beatChange = true;
            return;
        }
        if (Math.Abs(bpm - _lastBpm) > 0.01)
            _beatChange = true;
        _lastBpm = bpm;

        var message = Os2lMessages.Beat(_beatChange, Position(grid, beat), bpm);
        _beatChange = false;
        foreach (var peer in _peers.Values)
            peer.Connection.Send(message);
    }

    /// <summary>
    /// A beat's number for OS2L: counted from the downbeat of the song's first bar, so bars start on multiples of
    /// four.
    /// </summary>
    static long Position(Audio.Analysis.BeatGrid grid, long beat)
    {
        // The first bar starts on the first downbeat at or before the song's first beat.
        var first = (long)Math.Floor(grid.BeatNumberAt(grid.FirstBeat) + 1e-6);
        var firstBar = first - grid.BarPosition(first);
        return beat - firstBar;
    }

    void CloseAll()
    {
        foreach (var peer in _peers.Values)
        {
            if (peer.Announced)
                Log?.Invoke($"Lighting: disconnected from {peer.Connection.Name}");
            peer.Connection.Dispose();
        }
        _peers.Clear();
        _retryAt.Clear();
        _playing.Clear();
    }

    public void Dispose()
    {
        CloseAll();
        _browser?.Dispose();
        _browser = null;
    }
}
