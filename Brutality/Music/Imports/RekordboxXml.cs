using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Brutality.Audio.Analysis;

namespace Brutality.Music.Imports;

/// <summary>
/// A folder or playlist in a Rekordbox collection: a folder has <see cref="Children"/>, a playlist
/// <see cref="Paths"/>, its songs' files in order.
/// </summary>
/// <param name="IsFolder">Whether it's a folder of playlists, even an empty one, rather than a playlist.</param>
sealed record RekordboxNode(string Name, bool IsFolder, IReadOnlyList<RekordboxNode> Children, IReadOnlyList<string> Paths);

/// <summary>
/// A Rekordbox collection exported as XML (File &gt; Export Collection in xml format): its songs' hot cues, by file,
/// and its playlists, in their folders; and its songs' memory cues and beat grids, by file.
/// </summary>
sealed record RekordboxCollection(string FilePath, IReadOnlyDictionary<string, IReadOnlyList<TimeSpan?>> Hotcues, IReadOnlyList<RekordboxNode> Playlists)
{
    /// <summary>
    /// Each song's memory cues, in order through the song; songs with none are left out. A memory loop comes in as a
    /// memory cue at its start.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TimeSpan>> MemoryCues { get; init; } = new Dictionary<string, IReadOnlyList<TimeSpan>>();

    /// <summary>
    /// Each song's beat grid, as Rekordbox has it (often fixed by hand there); songs without one are left out.
    /// </summary>
    public IReadOnlyDictionary<string, BeatGrid> Grids { get; init; } = new Dictionary<string, BeatGrid>();

    /// <summary>
    /// Each song's hot cues' names, colours and hot loops, where it has any.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HotcueDetail?>> HotcueDetails { get; init; } = new Dictionary<string, IReadOnlyList<HotcueDetail?>>();

    /// <summary>
    /// Each song's memory loops, where it has any.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> MemoryLoops { get; init; } = new Dictionary<string, IReadOnlyList<(TimeSpan Start, TimeSpan End)>>();

    /// <summary>
    /// Every song it has hot cues, memory cues or a beat grid for.
    /// </summary>
    public IEnumerable<string> Songs => Hotcues.Keys.Concat(MemoryCues.Keys).Concat(Grids.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reads Rekordbox's XML collection format. Its songs are TRACKs with a file URL as their Location and POSITION_MARKs
/// for their cues: Type 0 a cue and 4 a loop, Num 0 to 7 hot cues A to H and -1 a memory cue, Start in seconds. A hot
/// loop comes in as a hot cue at its start, so its pad still goes there, and a memory loop as a memory cue at its
/// start. A TRACK's beat grid is its TEMPOs: each a beat at Inizio seconds and the tempo (Bpm) from there, Battito
/// saying which beat of its bar (1 to 4) that beat is; one for a steady tempo, more where it changes.
/// Playlists are NODEs under PLAYLISTS: Type 0 a folder, Type 1 a playlist of TRACKs keyed by TrackID (KeyType 0) or
/// Location (KeyType 1).
/// </summary>
static class RekordboxXml
{
    /// <summary>
    /// How many hot cues Rekordbox has, A to H, as Cuepoint has.
    /// </summary>
    const int HOTCUES = 8;

    /// <summary>
    /// Whether <paramref name="path"/> is a Rekordbox collection: an XML file whose top element is DJ_PLAYLISTS.
    /// Only its start is read.
    /// </summary>
    public static bool IsCollection(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, Settings);
            return reader.MoveToContent() == XmlNodeType.Element && reader.Name == "DJ_PLAYLISTS";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true, IgnoreComments = true };

    /// <summary>
    /// Reads the collection at <paramref name="path"/>, a track at a time so a big one doesn't fill memory; null if
    /// it can't be read or isn't one.
    /// </summary>
    public static RekordboxCollection? Load(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, Settings);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "DJ_PLAYLISTS")
                return null;

            var hotcues = new Dictionary<string, IReadOnlyList<TimeSpan?>>(StringComparer.OrdinalIgnoreCase);
            var memoryCues = new Dictionary<string, IReadOnlyList<TimeSpan>>(StringComparer.OrdinalIgnoreCase);
            var grids = new Dictionary<string, BeatGrid>(StringComparer.OrdinalIgnoreCase);
            var details = new Dictionary<string, IReadOnlyList<HotcueDetail?>>(StringComparer.OrdinalIgnoreCase);
            var loops = new Dictionary<string, IReadOnlyList<(TimeSpan Start, TimeSpan End)>>(StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<string, string>();
            XElement? playlists = null;
            var inCollection = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "COLLECTION")
                    inCollection = false;
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                switch (reader.Name)
                {
                    case "COLLECTION":
                        inCollection = !reader.IsEmptyElement;
                        break;
                    case "TRACK" when inCollection:
                        var track = XElement.Load(reader.ReadSubtree());
                        if (ToPath((string?)track.Attribute("Location")) is not { } file)
                            break;
                        if ((string?)track.Attribute("TrackID") is { } id)
                            byId[id] = file;
                        if (Hotcues(track) is { } cues)
                            hotcues[file] = cues;
                        if (MemoryCues(track) is { } memory)
                            memoryCues[file] = memory;
                        if (Grid(track) is { } grid)
                            grids[file] = grid;
                        if (Details(track) is { } detail)
                            details[file] = detail;
                        if (MemoryLoops(track) is { } loop)
                            loops[file] = loop;
                        break;
                    case "PLAYLISTS":
                        playlists = XElement.Load(reader.ReadSubtree());
                        break;
                }
            }

            // The tree's top node is ROOT, a folder holding the rest.
            var root = playlists?.Element("NODE");
            var nodes = root is null ? [] : root.Elements("NODE").Select(node => Node(node, byId)).ToList();
            return new RekordboxCollection(path, hotcues, nodes) { MemoryCues = memoryCues, Grids = grids, HotcueDetails = details, MemoryLoops = loops };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// A track's hot cues A to H, in seconds; null if it has none.
    /// </summary>
    static IReadOnlyList<TimeSpan?>? Hotcues(XElement track)
    {
        var cues = new TimeSpan?[HOTCUES];
        var any = false;
        foreach (var mark in track.Elements("POSITION_MARK"))
        {
            if (!int.TryParse((string?)mark.Attribute("Num"), out var number) || number < 0 || number >= HOTCUES)
                continue;
            if (!double.TryParse((string?)mark.Attribute("Start"), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) || start < 0)
                continue;
            cues[number] = TimeSpan.FromSeconds(start);
            any = true;
        }
        return any ? cues : null;
    }

    /// <summary>
    /// A track's hot cues' names, colours (Red, Green and Blue, 0 to 255) and hot loops (Type 4, to End); null if none
    /// of them has any.
    /// </summary>
    static IReadOnlyList<HotcueDetail?>? Details(XElement track)
    {
        var details = new HotcueDetail?[HOTCUES];
        var any = false;
        foreach (var mark in track.Elements("POSITION_MARK"))
        {
            if (!int.TryParse((string?)mark.Attribute("Num"), out var number) || number < 0 || number >= HOTCUES || Seconds(mark, "Start") is not { } start)
                continue;
            var name = (string?)mark.Attribute("Name") is { Length: > 0 } text ? text : null;
            uint? color = byte.TryParse((string?)mark.Attribute("Red"), out var red) && byte.TryParse((string?)mark.Attribute("Green"), out var green) && byte.TryParse((string?)mark.Attribute("Blue"), out var blue)
                ? (uint)(red << 16 | green << 8 | blue)
                : null;
            var end = (string?)mark.Attribute("Type") == "4" && Seconds(mark, "End") is { } until && until > start ? until : (double?)null;
            if (name is null && color is null && end is null)
                continue;
            details[number] = new HotcueDetail(start, name, color, end);
            any = true;
        }
        return any ? details : null;
    }

    /// <summary>
    /// A track's memory loops (Type 4, numbered -1), each its start and end; null if it has none.
    /// </summary>
    static IReadOnlyList<(TimeSpan Start, TimeSpan End)>? MemoryLoops(XElement track)
    {
        var loops = track.Elements("POSITION_MARK")
            .Where(mark => (string?)mark.Attribute("Num") == "-1" && (string?)mark.Attribute("Type") == "4")
            .Select(mark => (Start: Seconds(mark, "Start"), End: Seconds(mark, "End")))
            .Where(loop => loop.Start >= 0 && loop.End > loop.Start)
            .OrderBy(loop => loop.Start)
            .Select(loop => (TimeSpan.FromSeconds(loop.Start!.Value), TimeSpan.FromSeconds(loop.End!.Value)))
            .ToList();
        return loops.Count > 0 ? loops : null;
    }

    /// <summary>
    /// A track's memory cues (and memory loops' starts), in order; null if it has none.
    /// </summary>
    static IReadOnlyList<TimeSpan>? MemoryCues(XElement track)
    {
        var cues = track.Elements("POSITION_MARK")
            .Where(mark => (string?)mark.Attribute("Num") == "-1")
            .Select(mark => Seconds(mark, "Start"))
            .OfType<double>()
            .Where(start => start >= 0)
            .Distinct()
            .Order()
            .Select(TimeSpan.FromSeconds)
            .ToList();
        return cues.Count > 0 ? cues : null;
    }

    /// <summary>
    /// A track's beat grid from its TEMPOs; null if it has none that make one. Bars are counted from the first's
    /// Battito: the beat it's on is that one of its bar.
    /// </summary>
    static BeatGrid? Grid(XElement track)
    {
        var markers = new List<GridMarker>();
        var downbeat = 0;
        foreach (var tempo in track.Elements("TEMPO"))
        {
            if (Seconds(tempo, "Inizio") is not { } start || start < 0 || Seconds(tempo, "Bpm") is not { } bpm || bpm <= 0)
                continue;
            // In time order, each after the last: one at the same moment as the one before takes its place.
            if (markers.Count > 0 && start <= markers[^1].Time)
            {
                if (start < markers[^1].Time)
                    continue;
                markers.RemoveAt(markers.Count - 1);
            }
            if (markers.Count == 0 && int.TryParse((string?)tempo.Attribute("Battito"), out var beat) && beat is >= 1 and <= BeatGrid.BEATS_PER_BAR)
                downbeat = 1 - beat;
            markers.Add(new GridMarker(start, bpm));
        }
        if (markers.Count == 0)
            return null;
        // The song's length, for how far the last tempo runs; or, without it, a beat past the last marker.
        var duration = Seconds(track, "TotalTime") is { } total && total > markers[^1].Time ? total : markers[^1].Time + markers[^1].BeatSeconds;
        return new BeatGrid(markers, duration).WithDownbeat(downbeat);
    }

    static double? Seconds(XElement element, string attribute) =>
        double.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    static RekordboxNode Node(XElement node, IReadOnlyDictionary<string, string> byId)
    {
        var name = (string?)node.Attribute("Name") ?? "";
        if ((string?)node.Attribute("Type") == "1")
        {
            var byLocation = (string?)node.Attribute("KeyType") == "1";
            var paths = node.Elements("TRACK")
                .Select(track => (string?)track.Attribute("Key"))
                .Select(key => key is null ? null : byLocation ? ToPath(key) : byId.GetValueOrDefault(key))
                .OfType<string>()
                .ToList();
            return new RekordboxNode(name, IsFolder: false, [], paths);
        }
        return new RekordboxNode(name, IsFolder: true, node.Elements("NODE").Select(child => Node(child, byId)).ToList(), []);
    }

    /// <summary>
    /// A file's path from Rekordbox's URL for it, "file://localhost/C:/Music/A%20Song.mp3"; null for anything else.
    /// </summary>
    internal static string? ToPath(string? location)
    {
        if (location is null)
            return null;
        foreach (var prefix in (string[])["file://localhost/", "file:///", "file://"])
        {
            if (location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = Uri.UnescapeDataString(location[prefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
                // A Mac's path, "/Users/...", isn't one this computer has.
                return path.Length >= 3 && path[1] == ':' ? path : null;
            }
        }
        return null;
    }
}
