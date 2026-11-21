using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// Sync lock, on two real decks loaded with silence, their songs at 120 and 125 BPM.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class SyncLockTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Deck _a;
    readonly Deck _b;

    public SyncLockTests()
    {
        Silence(_folder["a.wav"]);
        Silence(_folder["b.wav"]);
        (_a, _b) = (Loaded("A", 120), Loaded("B", 125));
    }

    Deck Loaded(string name, double bpm)
    {
        var deck = _engine.CreateDeck(name);
        deck.Load(_folder[$"{name.ToLowerInvariant()}.wav"]);
        deck.Analysis = new TrackAnalysis(BeatGrid.Constant(bpm, 0, 30), -14, 0.5f, new MusicalKey(9, true));
        return deck;
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Silence(string path, double seconds = 30)
    {
        const int rate = 8000;
        var samples = (int)(rate * seconds);
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
    public void A_locked_deck_follows_the_other_s_tempo_as_it_changes()
    {
        _b.SyncLocked = true;
        Assert.True(_b.SyncLocked);
        Assert.Equal(120, _b.Bpm!.Value, 6);

        _a.Tempo = 0.05;
        _b.Update();

        Assert.Equal(126, _b.Bpm!.Value, 6);
        Assert.True(_b.SyncLocked);
    }

    [Fact]
    public void Moving_a_locked_deck_s_own_tempo_lets_go()
    {
        _b.SyncLocked = true;
        _b.Tempo = 0.01;
        _b.Update();

        Assert.False(_b.SyncLocked);
        Assert.Equal(0.01, _b.Tempo, 9);
        // And it no longer follows.
        _a.Tempo = 0.05;
        _b.Update();
        Assert.Equal(0.01, _b.Tempo, 9);
    }

    [Fact]
    public void One_deck_follows_at_a_time_and_the_other_leads()
    {
        _b.SyncLocked = true;
        _a.SyncLocked = true;

        Assert.True(_a.SyncLocked);
        Assert.False(_b.SyncLocked);
        // B, now leading, keeps the tempo it was locked to; A follows it there, and on when it moves.
        Assert.Equal(_b.Bpm!.Value, _a.Bpm!.Value, 6);
        _b.Tempo = 0.02;
        _a.Update();
        Assert.Equal(127.5, _a.Bpm!.Value, 6);
    }

    [Fact]
    public void It_can_t_lock_with_nothing_to_follow_and_waits_if_the_other_deck_empties()
    {
        _a.Analysis = null;
        _b.SyncLocked = true;
        Assert.False(_b.SyncLocked);

        _a.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 30), -14, 0.5f, new MusicalKey(9, true));
        _b.SyncLocked = true;
        _a.Analysis = null;
        _b.Update();
        Assert.True(_b.SyncLocked);
    }
}
