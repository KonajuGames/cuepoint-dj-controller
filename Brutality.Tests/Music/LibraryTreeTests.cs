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

    // Takes in what's been looked for in the background until there's nothing more coming, or it's taken too long.
    void ApplyAll(double seconds = 5)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (_library.Background.IsBusy && watch.Elapsed.TotalSeconds < seconds)
        {
            _library.Background.Apply();
            Thread.Sleep(10);
        }
        Assert.False(_library.Background.IsBusy);
    }

    LibraryNode MusicFolder(bool recursive = false)
    {
        _folder.File("Music", "Album", "b.mp3");
        _folder.File("Music", "Album", "Disc 2", "c.mp3");
        return LibraryTree.Folder(_library, _ => recursive, _folder["Music"], "Music", LibraryNodeKind.Folder);
    }

    [Fact]
    public void A_folder_s_songs_are_looked_for_in_the_background()
    {
        var music = MusicFolder();
        LibraryNode? loaded = null;
        music.SongsLoaded += node => loaded = node;

        Assert.Empty(music.Songs);
        Assert.True(music.IsLoadingSongs);
        ApplyAll();

        Assert.Same(music, loaded);
        Assert.False(music.IsLoadingSongs);
        Assert.Equal(_folder["Music", "a.mp3"], Assert.Single(music.Songs).Path);
        Assert.False(_library.Background.IsBusy);
    }

    [Fact]
    public void A_recursive_folder_lists_everything_under_it_in_the_background()
    {
        var music = MusicFolder(recursive: true);

        _ = music.Songs;
        ApplyAll();

        Assert.Equal(3, music.Songs.Count);
    }

    [Fact]
    public void A_folder_s_subfolders_are_looked_for_in_the_background()
    {
        var music = MusicFolder();

        Assert.Empty(music.ChildrenInBackground);
        Assert.True(music.IsLoadingChildren);
        ApplyAll();

        var album = Assert.Single(music.ChildrenInBackground);
        Assert.Equal("Album", album.Name);
        Assert.Same(music, album.Parent);
        Assert.False(music.IsLoadingChildren);
    }

    [Fact]
    public void Songs_looked_for_before_a_refresh_are_passed_over()
    {
        var music = MusicFolder();
        var loads = 0;
        music.SongsLoaded += _ => loads++;

        _ = music.Songs;
        music.RefreshSongs();
        _folder.File("Music", "d.mp3");
        _ = music.Songs;
        ApplyAll();

        Assert.Equal(1, loads);
        Assert.Equal(2, music.Songs.Count);
    }

    [Fact]
    public void Subfolders_found_there_and_then_are_kept_over_ones_still_coming()
    {
        var music = MusicFolder();

        _ = music.ChildrenInBackground;
        var found = music.Children;
        ApplyAll();

        Assert.Same(found, music.ChildrenInBackground);
        Assert.False(music.IsLoadingChildren);
    }

    [Fact]
    public void A_folder_that_can_t_be_read_lists_nothing()
    {
        var gone = LibraryTree.Folder(_library, _ => false, _folder["Gone"], "Gone", LibraryNodeKind.Folder);

        _ = gone.Songs;
        _ = gone.ChildrenInBackground;
        ApplyAll();

        Assert.Empty(gone.Songs);
        Assert.Empty(gone.ChildrenInBackground);
        Assert.False(gone.IsLoadingSongs);
    }
}
