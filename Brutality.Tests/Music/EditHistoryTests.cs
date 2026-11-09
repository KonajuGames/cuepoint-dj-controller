using Brutality.Audio;
using Brutality.Audio.Analysis;
using Brutality.Music;

namespace Brutality.Tests.Music;

[Collection(AudioEngineTests.NAME)]
public sealed class EditHistoryTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly MusicLibrary _library;
    readonly LibraryDatabase _database;
    readonly EditHistory _history;
    readonly Song _song;

    static readonly TimeSpan?[] None = [null, null];

    public EditHistoryTests()
    {
        _folder.File("Music", "a.mp3");
        _library = new MusicLibrary(_folder["Music"]);
        _database = LibraryDatabase.Load(_folder["library.json"], _folder.Path, _ => _folder.Path);
        _history = new EditHistory(_database, _library, []);
        _song = _library.Songs[0];
    }

    public void Dispose()
    {
        _library.Dispose();
        _folder.Dispose();
    }

    void ChangeHotcues(params TimeSpan?[] after)
    {
        _history.RecordHotcues(_song.Path, _database.GetHotcues(_song.Path), after);
        _database.SetHotcues(_song.Path, after);
    }

    [Fact]
    public void Hot_cue_changes_are_taken_back_newest_first_and_saved()
    {
        ChangeHotcues(TimeSpan.FromSeconds(1), null);
        ChangeHotcues(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        ChangeHotcues(null, TimeSpan.FromSeconds(2));

        Assert.Contains("clearing hot cue 1", _history.Undo());
        Assert.Equal(TimeSpan.FromSeconds(1), _database.GetHotcues(_song.Path)[0]);
        Assert.Contains("setting hot cue 2", _history.Undo());
        Assert.Null(_database.GetHotcues(_song.Path)[1]);
        Assert.Contains("setting hot cue 1", _history.Undo());
        Assert.All(_database.GetHotcues(_song.Path), cue => Assert.Null(cue));
        Assert.False(_database.Dirty);
        Assert.Equal(0, _history.Count);
    }

    [Fact]
    public void Adding_or_removing_a_memory_cue_is_taken_back()
    {
        _history.RecordMemoryCues(_song.Path, [], [TimeSpan.FromSeconds(3)]);
        _database.SetMemoryCues(_song.Path, [TimeSpan.FromSeconds(3)]);

        Assert.Contains("adding a memory cue", _history.Undo());
        Assert.Empty(_database.GetMemoryCues(_song.Path));
    }

    [Fact]
    public void With_nothing_left_it_says_so()
    {
        Assert.Equal("Nothing to undo", _history.Undo());
    }

    [Fact]
    public void Only_the_latest_changes_are_remembered()
    {
        for (var i = 0; i < EditHistory.MAX_EDITS + 5; i++)
            _history.RecordHotcues(_song.Path, None, None);

        Assert.Equal(EditHistory.MAX_EDITS, _history.Count);
    }

    TrackAnalysis Analysed() => new(BeatGrid.Constant(124, 0.25, 200), -9, 0.9f, new MusicalKey(9, true));

    [Fact]
    public void A_grid_fixed_by_hand_goes_back_to_as_analysed_on_the_song()
    {
        _database.Set(_song, Analysed());
        var fixedGrid = new GridEdit(BeatGrid.Constant(125, 0.5, 200), []);
        _song.Analysis = Analysed() with { Grid = fixedGrid.Grid };
        _history.RecordGrid(_song.Path, null);
        _database.SetGridEdit(_song.Path, fixedGrid);

        _history.Undo();

        Assert.False(_database.HasGridEdit(_song.Path));
        Assert.Equal(124, _song.Analysis!.Bpm);
    }

    [Fact]
    public void A_grid_goes_back_to_the_fix_before_this_one()
    {
        _database.Set(_song, Analysed());
        var first = new GridEdit(BeatGrid.Constant(125, 0.5, 200), []);
        var second = new GridEdit(BeatGrid.Constant(126, 0.5, 200), []);
        _database.SetGridEdit(_song.Path, first);
        _history.RecordGrid(_song.Path, first);
        _database.SetGridEdit(_song.Path, second);

        _history.Undo();

        Assert.Equal(125, _database.GetGridEdit(_song.Path, 200)!.Grid.Markers[0].Bpm);
        Assert.Equal(125, _song.Analysis!.Bpm);
    }

    [Fact]
    public void Undoing_shows_on_the_deck_with_the_song()
    {
        using var folder = new TempFolder();
        WriteSilence(folder["song.wav"]);
        using var engine = new AudioEngine();
        using var deck = engine.CreateDeck("A");
        deck.Load(folder["song.wav"]);
        var history = new EditHistory(_database, _library, [deck]);
        var before = deck.Hotcues.ToArray();
        deck.HotcueDown(0);
        history.RecordHotcues(deck.FilePath!, before, deck.Hotcues);

        history.Undo();

        Assert.All(deck.Hotcues, cue => Assert.Null(cue));
    }

    [Fact]
    public void The_grid_editor_says_what_a_grid_was_before_each_change()
    {
        using var folder = new TempFolder();
        WriteSilence(folder["song.wav"]);
        using var engine = new AudioEngine();
        using var deck = engine.CreateDeck("A");
        deck.Load(folder["song.wav"]);
        deck.Analysis = Analysed();
        var editor = new GridEditor(deck, _library, _database);
        var seen = new List<GridEdit?>();
        editor.Edited += (_, before) => seen.Add(before);

        editor.Pad(GridEditor.Pads.ToList().IndexOf(GridTool.NudgeLater));
        editor.Pad(GridEditor.Pads.ToList().IndexOf(GridTool.NudgeLater));
        editor.Pad(GridEditor.Pads.ToList().IndexOf(GridTool.Reset));
        editor.Pad(GridEditor.Pads.ToList().IndexOf(GridTool.Reset));

        Assert.Equal(3, seen.Count);
        Assert.Null(seen[0]);
        Assert.NotNull(seen[1]);
        Assert.NotNull(seen[2]);
        Assert.False(_database.HasGridEdit(folder["song.wav"]));
    }

    static void WriteSilence(string path)
    {
        const int RATE = 8000, SAMPLES = RATE * 30;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + SAMPLES * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(RATE);
        writer.Write(RATE * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(SAMPLES * 2);
        writer.Write(new byte[SAMPLES * 2]);
    }
}
