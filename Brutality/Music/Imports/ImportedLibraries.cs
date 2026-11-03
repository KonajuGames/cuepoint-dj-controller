using System.Text.Json;

namespace Brutality.Music.Imports;

/// <summary>
/// What other DJ software has that Cuepoint can use: Rekordbox collections exported as XML, and Serato's crates,
/// grouped by the _Serato_ folder they're in (one per drive with any).
/// </summary>
sealed class ImportedLibraries
{
    /// <summary>
    /// The Rekordbox collections found or chosen, one for each file. UI thread only.
    /// </summary>
    public List<RekordboxCollection> Rekordbox { get; init; } = [];

    /// <summary>
    /// Takes <paramref name="collection"/> in place of the one from the same file, or as well as the rest.
    /// </summary>
    public void Put(RekordboxCollection collection)
    {
        var at = Rekordbox.FindIndex(known => string.Equals(known.FilePath, collection.FilePath, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            Rekordbox[at] = collection;
        else
            Rekordbox.Add(collection);
    }

    /// <summary>
    /// Serato's crates, by the drive their _Serato_ folder is for, as "C:".
    /// </summary>
    public IReadOnlyList<(string Drive, IReadOnlyList<SeratoCrate> Crates)> Serato { get; init; } = [];

    public bool IsEmpty => Rekordbox.Count == 0 && Serato.Count == 0;

    /// <summary>
    /// Looks for Rekordbox collections in the folders an export usually goes (Documents, Music, Desktop, Downloads,
    /// Rekordbox's own, and <paramref name="libraryFolder"/>), as well as those <paramref name="chosen"/> to import,
    /// and for Serato's crates in the Music folder and at the root of each of <paramref name="driveRoots"/>.
    /// </summary>
    public static ImportedLibraries Find(string libraryFolder, IEnumerable<string> driveRoots, IEnumerable<string>? chosen = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string[] places =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            music,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(home, "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pioneer", "rekordbox"),
            libraryFolder,
        ];
        var rekordbox = places
            .Where(place => place.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(XmlFilesIn)
            .Concat((chosen ?? []).Where(File.Exists))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(RekordboxXml.IsCollection)
            .Select(RekordboxXml.Load)
            .OfType<RekordboxCollection>()
            .ToList();

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var serato = SeratoCrates.Folders(music, systemRoot, driveRoots)
            .Select(found => (Drive: Path.GetPathRoot(found.Root)?.TrimEnd('\\', '/') ?? found.Root, Crates: SeratoCrates.Load(found.Folder, found.Root)))
            .Where(found => found.Crates.Count > 0)
            .Select(found => (found.Drive, (IReadOnlyList<SeratoCrate>)found.Crates))
            .ToList();

        return new ImportedLibraries { Rekordbox = rekordbox, Serato = serato };
    }

    static IEnumerable<string> XmlFilesIn(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.xml").ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// What's been imported already, so it isn't again: each Rekordbox collection, until it's exported afresh, and each
/// song's Serato hot cues, until its file changes. Without it, hot cues cleared in Cuepoint would come back the next
/// time. Kept beside the settings.
/// </summary>
sealed class ImportRecord
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>
    /// Each Rekordbox collection imported, by path, with when it was last written then, in UTC.
    /// </summary>
    public Dictionary<string, DateTime> Rekordbox { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each song looked in for Serato's hot cues, by path, with when it was last written then, in UTC.
    /// </summary>
    public Dictionary<string, DateTime> Serato { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> is new to <paramref name="record"/>, or changed since; and if so, notes it as it
    /// is now.
    /// </summary>
    public static bool IsNew(Dictionary<string, DateTime> record, string path)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (record.TryGetValue(path, out var seen) && seen == written)
            return false;
        record[path] = written;
        return true;
    }

    public static ImportRecord Load(string path)
    {
        try
        {
            var loaded = File.Exists(path) ? JsonSerializer.Deserialize<ImportRecord>(File.ReadAllText(path), JsonOptions) : null;
            if (loaded is null)
                return new();
            // Paths are told apart ignoring case, as Windows does, which the file can't say.
            return new()
            {
                Rekordbox = new(loaded.Rekordbox, StringComparer.OrdinalIgnoreCase),
                Serato = new(loaded.Serato, StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// Writes it to a temporary file then swaps it in. Returns why it couldn't, if it couldn't.
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
