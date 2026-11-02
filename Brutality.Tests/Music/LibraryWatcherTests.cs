using System.Diagnostics;
using Brutality.Music;

namespace Brutality.Tests.Music;

public class LibraryWatcherTests : IDisposable
{
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>
    /// Whether the watcher says something's changed within a few seconds, as Windows takes a moment to say.
    /// </summary>
    static bool ChangesWithin(LibraryWatcher watcher, double seconds = 3)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            if (watcher.TakeChanges(Quiet))
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    [Fact]
    public void A_song_copied_in_is_a_change_said_once()
    {
        using var watcher = LibraryWatcher.Start(_folder.Path, recursive: false)!;

        _folder.File("new.mp3");

        Assert.True(ChangesWithin(watcher));
        Assert.False(ChangesWithin(watcher, 0.6));
    }

    [Fact]
    public void Songs_renamed_or_deleted_are_changes()
    {
        var song = _folder.File("a.flac");
        using var watcher = LibraryWatcher.Start(_folder.Path, recursive: false)!;

        File.Move(song, _folder["b.flac"]);
        Assert.True(ChangesWithin(watcher));

        File.Delete(_folder["b.flac"]);
        Assert.True(ChangesWithin(watcher));
    }

    [Fact]
    public void The_apps_own_files_and_anything_ignored_are_not_changes()
    {
        Directory.CreateDirectory(_folder["Recordings"]);
        using var watcher = LibraryWatcher.Start(_folder.Path, recursive: true, ignore: path => path.StartsWith(_folder["Recordings"]))!;

        File.WriteAllText(_folder["library.json"], "{}");
        File.WriteAllText(_folder["library.json.tmp"], "{}");
        File.WriteAllText(_folder["Set.m3u8"], "#EXTM3U");
        _folder.File("Recordings", "Mix.wav");

        Assert.False(ChangesWithin(watcher, 1));
    }

    [Fact]
    public void Subfolders_are_watched_only_when_recursive()
    {
        Directory.CreateDirectory(_folder["Album"]);
        using var flat = LibraryWatcher.Start(_folder.Path, recursive: false)!;
        using var deep = LibraryWatcher.Start(_folder.Path, recursive: true)!;

        _folder.File("Album", "01 Song.mp3");

        Assert.True(ChangesWithin(deep));
        Assert.False(ChangesWithin(flat, 0.6));
    }

    [Fact]
    public void A_folder_that_is_not_there_cannot_be_watched()
    {
        Assert.Null(LibraryWatcher.Start(_folder["Nowhere"], recursive: true));
    }

    [Fact]
    public void Scanning_again_picks_up_songs_added_changed_and_gone()
    {
        var kept = _folder.File("Kept.mp3");
        var edited = _folder.File("Edited.mp3");
        var removed = _folder.File("Removed.mp3");
        using var library = new MusicLibrary(_folder.Path);
        var keptSong = library.Find(kept);
        var editedSong = library.Find(edited)!;

        _folder.File("Added.mp3");
        File.WriteAllBytes(edited, new byte[100]);
        File.Delete(removed);
        library.Scan(library.Root, library.Recursive);

        Assert.Equal(new[] { "Added", "Edited", "Kept" }, library.Songs.Select(song => song.Title));
        Assert.Same(keptSong, library.Find(kept));
        var editedNow = library.Find(edited)!;
        Assert.NotSame(editedSong, editedNow);
        Assert.Equal(100, editedNow.Size);
        Assert.Null(library.Find(removed));
    }
}
