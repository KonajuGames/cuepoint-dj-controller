using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// A song loading cued to where its music starts, on a real deck; with the other tests that make an audio engine.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class AutoCueTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Deck _deck;

    public AutoCueTests()
    {
        // 2.1 seconds of silence, then sound.
        Wav(_folder["song.wav"], silentFor: 2.1, seconds: 10);
        _deck = _engine.CreateDeck("A");
    }

    public void Dispose()
    {
        _deck.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Wav(string path, double silentFor, double seconds)
    {
        const int rate = 8000;
        var samples = (int)(rate * seconds);
        var silent = (int)(rate * silentFor);
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
        for (var i = 0; i < samples; i++)
            writer.Write((short)(i < silent ? 0 : (i % 16 < 8 ? 12000 : -12000)));
    }

    void Load(TimeSpan? cuePoint = null, bool grid = true)
    {
        _deck.Load(_folder["song.wav"]);
        _deck.Analysis = grid ? new TrackAnalysis(BeatGrid.Constant(120, 0, 10), -14, 0.5f, new MusicalKey(9, true)) : null;
        _deck.SetMemoryCues([], cuePoint);
    }

    [Fact]
    public void A_song_loads_cued_to_the_beat_nearest_its_first_sound()
    {
        Load();

        // At 120 BPM the beats are half a second apart, so 2.1 seconds is nearest the one at 2.
        Assert.Equal(2, _deck.CuePoint.TotalSeconds, 2);
        Assert.Equal(2, _deck.Position.TotalSeconds, 2);
    }

    [Fact]
    public void Without_a_grid_it_s_the_first_sound()
    {
        Load(grid: false);

        Assert.Equal(2.1, _deck.CuePoint.TotalSeconds, 2);
    }

    [Fact]
    public void A_cue_point_of_its_own_comes_first_and_it_can_be_turned_off()
    {
        Load(TimeSpan.FromSeconds(5));
        Assert.Equal(5, _deck.CuePoint.TotalSeconds, 2);

        _deck.AutoCue = false;
        Load();
        Assert.Equal(0, _deck.CuePoint.TotalSeconds, 2);
    }

    [Fact]
    public void Silence_has_no_first_sound()
    {
        Assert.Null(Deck.FirstSound(new PcmAudio(new float[100], 2, 8000)));
        Assert.Equal(0.25, Deck.FirstSound(new PcmAudio([.. new float[4000], 0.5f, 0.5f], 2, 8000)));
    }
}
