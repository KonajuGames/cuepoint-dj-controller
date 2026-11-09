using Brutality.Audio;
using Brutality.Music;
using Brutality.Music.Imports;

namespace Brutality.Tests.Music;

public class ImportedNodeTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly MusicLibrary _library;

    // A playlist's or crate's songs, once they've been looked for in the background.
    IReadOnlyList<Song> Loaded(LibraryNode node)
    {
        _ = node.Songs;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (node.IsLoadingSongs && watch.Elapsed.TotalSeconds < 5)
        {
            _library.Background.Apply();
            Thread.Sleep(5);
        }
        Assert.False(node.IsLoadingSongs);
        return node.Songs;
    }

    public ImportedNodeTests()
    {
        _folder.File("Music", "a.mp3");
        _folder.File("Music", "b.mp3");
        _folder.File("Elsewhere", "c.mp3");
        Directory.CreateDirectory(_folder["C"]);
        _library = new MusicLibrary(_folder["Music"]);
    }

    public void Dispose()
    {
        _library.Dispose();
        _folder.Dispose();
    }

    List<LibraryNode> Build(ImportedLibraries? imports)
    {
        var folders = new VirtualFolders(_library, _folder["C", "VirtualFolders"], () => [new StorageDrive(_folder["C"] + "\\", "C:", true)], _folder["C"] + "\\");
        return LibraryTree.Build(_library, Array.Empty<Deck>(), [], folders, _ => false, imports);
    }

    static string[] Names(IEnumerable<LibraryNode> nodes) => nodes.Select(node => node.Name).ToArray();

    [Fact]
    public void Nothing_found_adds_nothing()
    {
        Assert.DoesNotContain(Build(new ImportedLibraries()), node => node.Name is "Rekordbox" or "Serato");
        Assert.DoesNotContain(Build(null), node => node.Name is "Rekordbox" or "Serato");
    }

    [Fact]
    public void Rekordbox_lists_each_collection_with_its_playlists_in_their_folders()
    {
        var friday = new RekordboxNode("Friday", false, [], [_folder["Music", "b.mp3"], _folder["Gone", "x.mp3"], _folder["Elsewhere", "c.mp3"]]);
        var collection = new RekordboxCollection(_folder["rekordbox.xml"], new Dictionary<string, IReadOnlyList<TimeSpan?>>(), [new("Gigs", true, [friday], [])]);
        var nodes = Build(new ImportedLibraries { Rekordbox = [collection] });

        Assert.Equal(new[] { "Music library", "Local drives", "Virtual folders", "Rekordbox", "Lists", "Filters" }, Names(nodes));
        var playlist = LibraryTree.Find(nodes, ["Rekordbox", "rekordbox.xml", "Gigs", "Friday"])!;

        Assert.Equal(LibraryNodeKind.Imported, playlist.Kind);
        // In the playlist's order, those whose files are here, in the library or not.
        Assert.Equal(new[] { _folder["Music", "b.mp3"], _folder["Elsewhere", "c.mp3"] }, Loaded(playlist).Select(song => song.Path));
        Assert.False(LibraryTree.Find(nodes, ["Rekordbox", "rekordbox.xml", "Gigs"])!.HasSongs);
    }

    [Fact]
    public void Serato_lists_crates_inside_the_crates_they_are_in()
    {
        // "House" has songs and a crate inside; "Gigs" is only known by the crate inside it.
        SeratoCrate[] crates =
        [
            new("House", [], [_folder["Music", "a.mp3"]]),
            new("Deep", ["House"], [_folder["Music", "b.mp3"]]),
            new("Friday", ["Gigs"], [_folder["Elsewhere", "c.mp3"]]),
        ];
        var nodes = Build(new ImportedLibraries { Serato = [("C:", crates)] });

        var serato = LibraryTree.Find(nodes, ["Serato"])!;
        Assert.Equal(new[] { "House", "Gigs" }, Names(serato.Children));
        var house = LibraryTree.Find(nodes, ["Serato", "House"])!;
        Assert.Equal(LibraryNodeKind.Imported, house.Kind);
        Assert.Equal(_folder["Music", "a.mp3"], Assert.Single(Loaded(house)).Path);
        Assert.Equal(_folder["Music", "b.mp3"], Assert.Single(Loaded(LibraryTree.Find(nodes, ["Serato", "House", "Deep"])!)).Path);
        var gigs = LibraryTree.Find(nodes, ["Serato", "Gigs"])!;
        Assert.False(gigs.HasSongs);
        Assert.Equal(new[] { "Friday" }, Names(gigs.Children));
    }

    [Fact]
    public void Serato_crates_on_more_than_one_drive_go_under_each_drive()
    {
        var nodes = Build(new ImportedLibraries { Serato = [("C:", [new SeratoCrate("Warm up", [], [])]), ("E:", [new SeratoCrate("Gig", [], [])])] });

        Assert.Equal(new[] { "C:", "E:" }, Names(LibraryTree.Find(nodes, ["Serato"])!.Children));
        Assert.NotNull(LibraryTree.Find(nodes, ["Serato", "E:", "Gig"]));
    }
}

public class ChosenImportTests : IDisposable
{
    readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    string Xml(string name, params string[] playlists)
    {
        var nodes = string.Concat(playlists.Select(playlist => $"<NODE Name=\"{playlist}\" Type=\"1\" KeyType=\"0\" Entries=\"0\"/>"));
        var path = _folder[name];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<DJ_PLAYLISTS Version=\"1.0.0\"><COLLECTION Entries=\"0\"/><PLAYLISTS><NODE Type=\"0\" Name=\"ROOT\">{nodes}</NODE></PLAYLISTS></DJ_PLAYLISTS>");
        return path;
    }

    [Fact]
    public void A_collection_chosen_to_import_is_found_wherever_it_is_and_only_once()
    {
        var chosen = Xml(Path.Combine("Somewhere", "export.xml"), "Friday");

        var imports = ImportedLibraries.Find(_folder["Library"], [], [chosen, chosen.ToUpperInvariant(), _folder["missing.xml"]]);

        Assert.Equal(chosen, Assert.Single(imports.Rekordbox, collection => collection.FilePath.Equals(chosen, StringComparison.OrdinalIgnoreCase)).FilePath, ignoreCase: true);
    }

    [Fact]
    public void Reading_a_collection_again_takes_the_place_of_the_one_before()
    {
        var path = Xml("one.xml", "Friday");
        var imports = new ImportedLibraries();
        imports.Put(RekordboxXml.Load(path)!);

        Xml("one.xml", "Friday", "Saturday");
        imports.Put(RekordboxXml.Load(path)!);
        imports.Put(RekordboxXml.Load(Xml("two.xml", "Sunday"))!);

        Assert.Equal(2, imports.Rekordbox.Count);
        Assert.Equal(new[] { "Friday", "Saturday" }, imports.Rekordbox[0].Playlists.Select(node => node.Name));
    }

    [Fact]
    public void The_Rekordbox_node_shows_collections_as_they_are_once_refreshed()
    {
        using var library = new MusicLibrary(_folder.Path);
        var imports = new ImportedLibraries();
        imports.Put(RekordboxXml.Load(Xml("one.xml", "Friday"))!);
        var root = LibraryTree.RekordboxRoot(library, imports);
        Assert.Equal(new[] { "one.xml" }, root.Children.Select(node => node.Name));

        imports.Put(RekordboxXml.Load(Xml("two.xml", "Sunday"))!);
        imports.Put(RekordboxXml.Load(Xml("one.xml", "Friday", "Saturday"))!);
        root.Refresh();

        Assert.Equal(new[] { "one.xml", "two.xml" }, root.Children.Select(node => node.Name));
        Assert.Equal(new[] { "Friday", "Saturday" }, root.Children[0].Children.Select(node => node.Name));
    }
}

public class ImportRecordTests
{
    [Fact]
    public void A_file_is_new_until_noted_and_again_once_it_changes()
    {
        using var folder = new TempFolder();
        var song = folder.File("a.mp3");
        var record = new ImportRecord();

        Assert.True(ImportRecord.IsNew(record.Serato, song));
        Assert.False(ImportRecord.IsNew(record.Serato, song));
        Assert.False(ImportRecord.IsNew(record.Serato, song.ToUpperInvariant()));

        File.SetLastWriteTimeUtc(song, DateTime.UtcNow.AddMinutes(5));
        Assert.True(ImportRecord.IsNew(record.Serato, song));
    }

    [Fact]
    public void What_was_noted_comes_back_as_it_was_saved_ignoring_case()
    {
        using var folder = new TempFolder();
        var xml = folder.File("rekordbox.xml");
        var record = new ImportRecord();
        ImportRecord.IsNew(record.Rekordbox, xml);
        Assert.Null(record.Save(folder["imports.json"]));

        var loaded = ImportRecord.Load(folder["imports.json"]);

        Assert.False(ImportRecord.IsNew(loaded.Rekordbox, xml.ToUpperInvariant()));
        Assert.Empty(ImportRecord.Load(folder["missing.json"]).Serato);
    }
}
