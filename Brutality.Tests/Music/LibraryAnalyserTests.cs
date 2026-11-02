using Brutality.Music;
using static Brutality.Tests.Music.LibraryFileTests;

namespace Brutality.Tests.Music;

/// <summary>
/// The analyser's queue, with no workers to take songs from it, so what's waiting can be seen. Analysing itself
/// needs real audio files and the decoders, so isn't tested here.
/// </summary>
public class LibraryAnalyserTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly LibraryDatabase _database;

    public LibraryAnalyserTests() => _database = LibraryDatabase.Load(_folder["library.json"], _folder.Path, _ => _folder.Path);

    public void Dispose() => _folder.Dispose();

    LibraryAnalyser Analyser(IEnumerable<Song>? songs = null) => new(songs ?? [], _database, workers: 0);

    List<Song> Songs(int count) => Enumerable.Range(1, count).Select(i => Song(_folder[$"{i}.mp3"])).ToList();

    [Fact]
    public void Puts_what_the_database_knows_onto_songs_from_the_start()
    {
        var known = Song(_folder["known.mp3"]);
        _database.Set(known, Analysed());
        var fresh = Song(_folder["known.mp3"]);

        using var analyser = Analyser([fresh]);

        Assert.Equal(Analysed(), fresh.Analysis);
    }

    [Fact]
    public void Asking_queues_a_song_once()
    {
        using var analyser = Analyser();
        var songs = Songs(3);

        analyser.Request(songs);
        analyser.Request(songs[0]);

        Assert.Equal(3, analyser.QueuedCount);
    }

    [Fact]
    public void Songs_analysed_already_are_not_asked_for()
    {
        using var analyser = Analyser();
        var songs = Songs(2);
        songs[0].Analysis = Analysed();
        songs[1].AnalysisError = "Couldn't decode it";

        analyser.Request(songs);

        Assert.Equal(0, analyser.QueuedCount);
    }

    [Fact]
    public void Analyse_all_queues_what_needs_it_and_counts_it()
    {
        using var analyser = Analyser();
        var songs = Songs(5);
        songs[2].Analysis = Analysed();

        var added = analyser.QueueAll(songs);

        Assert.Equal(4, added);
        Assert.True(analyser.IsBulkActive);
        Assert.Equal(4, analyser.BulkTotal);
        Assert.Equal(0, analyser.BulkDone);
        Assert.Equal(4, analyser.QueuedCount);
    }

    [Fact]
    public void Analyse_all_again_adds_only_songs_it_did_not_have()
    {
        using var analyser = Analyser();
        var songs = Songs(4);
        analyser.QueueAll(songs.Take(3).ToList());

        var added = analyser.QueueAll(songs);

        Assert.Equal(1, added);
        Assert.Equal(4, analyser.BulkTotal);
        Assert.Equal(4, analyser.QueuedCount);
    }

    [Fact]
    public void Songs_already_asked_for_count_towards_analyse_all_without_queueing_twice()
    {
        using var analyser = Analyser();
        var songs = Songs(3);
        analyser.Request(songs[0]);

        analyser.QueueAll(songs);

        Assert.Equal(3, analyser.BulkTotal);
        Assert.Equal(3, analyser.QueuedCount);
    }

    [Fact]
    public void Cancelling_analyse_all_takes_its_songs_out_of_the_queue()
    {
        using var analyser = Analyser();
        var songs = Songs(4);
        analyser.QueueAll(songs);

        var left = analyser.CancelBulk();

        Assert.Equal(4, left);
        Assert.False(analyser.IsBulkActive);
        Assert.Equal(0, analyser.BulkTotal);
        Assert.Equal(0, analyser.QueuedCount);

        // And they can be asked for again, as songs on screen are.
        analyser.Request(songs[1]);
        Assert.Equal(1, analyser.QueuedCount);
    }

    [Fact]
    public void Analysing_again_queues_songs_analysed_already_keeping_their_analysis_meanwhile()
    {
        using var analyser = Analyser();
        var songs = Songs(2);
        songs[0].Analysis = Analysed();

        var queued = analyser.Reanalyse(songs);

        Assert.Equal(2, queued);
        Assert.Equal(2, analyser.QueuedCount);
        Assert.Equal(Analysed(), songs[0].Analysis);
        Assert.Equal(2, analyser.Reanalyse(songs));
        Assert.Equal(2, analyser.QueuedCount);
    }

    [Fact]
    public void Nothing_finished_means_nothing_to_update()
    {
        using var analyser = Analyser();
        analyser.Request(Songs(2));

        Assert.Equal(0, analyser.Update());
        Assert.Equal(0, analyser.ActiveCount);
    }
}
