using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Brutality.Music.Imports;

/// <summary>
/// A folder or playlist in a Rekordbox collection: a folder has <see cref="Children"/>, a playlist
/// <see cref="Paths"/>, its songs' files in order.
/// </summary>
/// <param name="IsFolder">Whether it's a folder of playlists, even an empty one, rather than a playlist.</param>
sealed record RekordboxNode(string Name, bool IsFolder, IReadOnlyList<RekordboxNode> Children, IReadOnlyList<string> Paths);

/// <summary>
/// A Rekordbox collection exported as XML (File &gt; Export Collection in xml format): its songs' hot cues, by file,
/// and its playlists, in their folders.
/// </summary>
sealed record RekordboxCollection(string FilePath, IReadOnlyDictionary<string, IReadOnlyList<TimeSpan?>> Hotcues, IReadOnlyList<RekordboxNode> Playlists);

/// <summary>
/// Reads Rekordbox's XML collection format. Its songs are TRACKs with a file URL as their Location and POSITION_MARKs
/// for their cues: Type 0 a cue and 4 a loop, Num 0 to 7 hot cues A to H and -1 a memory cue, Start in seconds. A hot
/// loop comes in as a hot cue at its start, so its pad still goes there.
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
                        break;
                    case "PLAYLISTS":
                        playlists = XElement.Load(reader.ReadSubtree());
                        break;
                }
            }

            // The tree's top node is ROOT, a folder holding the rest.
            var root = playlists?.Element("NODE");
            var nodes = root is null ? [] : root.Elements("NODE").Select(node => Node(node, byId)).ToList();
            return new RekordboxCollection(path, hotcues, nodes);
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
