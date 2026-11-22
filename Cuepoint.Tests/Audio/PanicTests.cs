using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// The panic button, on real decks; with the other tests that make an audio engine.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class PanicTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Deck _a;
    readonly Deck _b;

    public PanicTests()
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
    public void It_puts_the_sound_back_to_neutral_leaving_the_faders_and_crossfader_and_Undo_puts_it_back()
    {
        (_a.Equalizer.Low, _a.Equalizer.Filter, _a.Trim, _a.KeyShift, _a.Effects.On) = (-1, 0.6f, 0.3f, 2, true);
        _b.Equalizer.High = 0.5f;
        _a.ToggleLoop();
        _engine.MasterEffects.On = true;
        (_a.Volume, _engine.Crossfader) = (0.4f, 0.1f);

        var panic = Panic.Reset([_a, _b], _engine.MasterEffects);

        // A's EQ, trim, key, effect and loop, B's EQ, and the master effect.
        Assert.Equal(7, panic.Changed);
        Assert.Equal((0f, 0f, 0f, 0, false, false), (_a.Equalizer.Low, _a.Equalizer.Filter, _a.Trim, _a.KeyShift, _a.Effects.On, _a.IsLooping));
        Assert.Equal(0, _b.Equalizer.High);
        Assert.False(_engine.MasterEffects.On);
        Assert.Equal((0.4f, 0.1f), (_a.Volume, _engine.Crossfader));

        panic.Undo();
        Assert.Equal((-1f, 0.6f, 0.3f, 2, true), (_a.Equalizer.Low, _a.Equalizer.Filter, _a.Trim, _a.KeyShift, _a.Effects.On));
        Assert.Equal(0.5f, _b.Equalizer.High);
        Assert.True(_engine.MasterEffects.On);
    }

    [Fact]
    public void At_neutral_already_it_changes_nothing()
    {
        Assert.Equal(0, Panic.Reset([_a, _b], _engine.MasterEffects).Changed);
    }
}
