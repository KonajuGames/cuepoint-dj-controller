using Brutality.Audio;
using Brutality.Music;

namespace Brutality.Tests.Music;

public class LibraryTreeTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly MusicLibrary _library;

    public LibraryTreeTests()
    {
        _folder.File("Music", "a.mp3");
        Directory.CreateDirectory(_folder["C"]);
        _library = new MusicLibrary(_folder["Music"]);
    }

    public void Dispose()
    {
        _library.Dispose();
        _folder.Dispose();
    }

    string Drive(string letter) => _folder[letter] + Path.DirectorySeparatorChar;

    (List<LibraryNode> Nodes, VirtualFolders Folders) Build(params string[] drives)
    {
        foreach (var drive in drives)
            Directory.CreateDirectory(drive);
        var folders = new VirtualFolders(
            _library,
            _folder["C", "VirtualFolders"],
            () => [new StorageDrive(Drive("C"), "C:", true), .. drives.Select(root => new StorageDrive(root, root, true))],
            Drive("C")
        );
        return (LibraryTree.Build(_library, Array.Empty<Deck>(), [], folders, _ => false), folders);
    }

    [Fact]
    public void A_node_is_found_again_by_the_names_down_to_it()
    {
        var (nodes, _) = Build();
        var history = LibraryTree.Find(nodes, ["Lists", "History"])!;

        Assert.Equal(LibraryNodeKind.List, history.Kind);
        Assert.Equal(new[] { "Lists", "History" }, LibraryTree.PathOf(history));
        Assert.Same(history, LibraryTree.Find(nodes, LibraryTree.PathOf(history)));
    }

    [Fact]
    public void Something_no_longer_there_is_not_found()
    {
        var (nodes, _) = Build();

        Assert.Null(LibraryTree.Find(nodes, ["Lists", "Nothing"]));
        Assert.Null(LibraryTree.Find(nodes, ["Virtual folders", "Gone"]));
    }

    [Fact]
    public void The_library_lists_its_own_songs()
    {
        var (nodes, _) = Build();
        var library = LibraryTree.Find(nodes, ["Music library"])!;

        Assert.Equal(LibraryNodeKind.Library, library.Kind);
        Assert.Equal(_folder["Music", "a.mp3"], Assert.Single(library.Songs).Path);
    }

    [Fact]
    public void A_virtual_folder_on_a_drive_is_found_again_under_another_letter()
    {
        var (_, before) = Build(Drive("E"));
        before.MoveToDrive(before.Create("Gig"), Drive("E"));
        var (nodes, _) = Build(Drive("E"));
        var saved = LibraryTree.PathOf(LibraryTree.Find(nodes, ["Virtual folders", "Gig (" + Drive("E").TrimEnd('\\') + ")"])!);

        // The drive comes back as F, so the folder's shown name has changed.
        Directory.Move(Drive("E"), Drive("F"));
        var (after, _) = Build(Drive("F"));

        var found = LibraryTree.Find(after, saved);
        Assert.NotNull(found);
        Assert.Equal("Gig", found.Folder!.Name);
    }

    [Fact]
    public void Two_virtual_folders_of_the_same_name_on_other_drives_are_not_guessed_between()
    {
        var (_, folders) = Build(Drive("E"), Drive("F"));
        folders.MoveToDrive(folders.Create("Gig"), Drive("E"));
        folders.MoveToDrive(folders.Create("Gig"), Drive("F"));
        var (nodes, _) = Build(Drive("E"), Drive("F"));

        Assert.Null(LibraryTree.Find(nodes, ["Virtual folders", "Gig (X:)"]));
    }
}
