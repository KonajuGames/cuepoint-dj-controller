using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// Taking a song off a deck, on real decks; with the other tests that make an audio engine.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class UnloadTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Deck _a;
    readonly Deck _b;

    public UnloadTests()
    {
        Silence(_folder["song.wav"]);
        _a = _engine.CreateDeck("A");
        _b = _engine.CreateDeck("B");
        foreach (var deck in new[] { _a, _b })
        {
            deck.Load(_folder["song.wav"]);
            deck.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 30), -14, 0.5f, new MusicalKey(9, true));
        }
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Silence(string path)
    {
        const int rate = 8000;
        var samples = rate * 30;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }

    [Fact]
    public void It_leaves_the_deck_empty_with_nothing_of_the_song_left_and_says_so()
    {
        _a.Seek(TimeSpan.FromSeconds(4));
        _a.HotcueDown(0);
        _a.HotcueUp(0);
        _a.ToggleLoop();
        _a.KeyShift = 2;
        _a.Play();
        Deck? unloaded = null;
        _a.Unloaded += deck => unloaded = deck;

        _a.Unload();

        Assert.Same(_a, unloaded);
        Assert.False(_a.IsLoaded);
        Assert.False(_a.IsPlaying);
        Assert.False(_a.IsLooping);
        Assert.Null(_a.FilePath);
        Assert.Equal("", _a.Title);
        Assert.Null(_a.Waveform);
        Assert.Null(_a.Analysis);
        Assert.Equal(TimeSpan.Zero, _a.Duration);
        Assert.Equal(TimeSpan.Zero, _a.Position);
        Assert.Equal(0, _a.KeyShift);
        Assert.All(_a.Hotcues, hotcue => Assert.Null(hotcue));
        Assert.False(_a.CanLoop);
    }

    [Fact]
    public void An_empty_deck_does_nothing_when_played_and_takes_another_song()
    {
        _a.Unload();

        _a.Play();
        _a.CueDown();
        _a.CueUp();
        Assert.False(_a.IsPlaying);

        _a.Load(_folder["song.wav"]);
        Assert.True(_a.IsLoaded);
        _a.Play();
        Assert.True(_a.IsPlaying);
    }

    [Fact]
    public void A_song_still_loading_never_arrives()
    {
        _a.BeginLoad(_folder["song.wav"], "Song");
        Assert.True(_a.IsLoading);
        var arrived = false;
        _a.Loaded += _ => arrived = true;

        _a.Unload();
        Assert.False(_a.IsLoading);
        Thread.Sleep(300);
        _a.Update();

        Assert.False(arrived);
        Assert.False(_a.IsLoaded);
    }

    [Fact]
    public void A_sync_lock_lets_go_and_the_other_deck_keeps_playing()
    {
        _a.SyncLocked = true;
        _b.Play();

        _a.Unload();

        Assert.False(_a.SyncLocked);
        Assert.True(_b.IsPlaying);
        Assert.True(_b.IsLoaded);
    }

    [Fact]
    public void Unloading_an_empty_deck_does_nothing()
    {
        _a.Unload();
        var raised = false;
        _a.Unloaded += _ => raised = true;

        _a.Unload();

        Assert.False(raised);
    }
}
