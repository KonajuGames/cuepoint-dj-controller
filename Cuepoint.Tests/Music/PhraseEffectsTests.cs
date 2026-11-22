using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Music;

namespace Cuepoint.Tests.Music;

/// <summary>
/// Echo outs and filter sweeps to the next phrase, on a real deck playing silence; with the other tests that make an
/// audio engine. At 120 BPM from the start, a bar's two seconds and a phrase (eight bars) sixteen.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class PhraseEffectsTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Deck _deck;
    readonly PhraseEffects _phrase;

    public PhraseEffectsTests()
    {
        Silence(_folder["song.wav"]);
        _deck = _engine.CreateDeck("A");
        _deck.Load(_folder["song.wav"]);
        _deck.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 60), -14, 0.5f, new MusicalKey(9, true));
        _deck.Quantize = false;
        _phrase = new PhraseEffects(_deck);
    }

    public void Dispose()
    {
        _deck.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Silence(string path)
    {
        const int rate = 8000;
        var samples = rate * 60;
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

    void PlayFrom(double seconds)
    {
        _deck.Seek(TimeSpan.FromSeconds(seconds));
        _deck.Play();
    }

    [Theory]
    [InlineData(5, 16)]
    [InlineData(13.9, 16)]
    // Less than a bar before one, it's the one after.
    [InlineData(14.5, 32)]
    [InlineData(16, 32)]
    public void The_next_phrase_is_eight_bars_from_the_first_downbeat_and_at_least_a_bar_away(double now, double phrase)
    {
        Assert.Equal(phrase, PhraseEffects.NextPhrase(_deck.Analysis!.Grid, now)!.Value, 3);
    }

    [Fact]
    public void A_sweep_rises_to_the_phrase_and_drops_back_on_it()
    {
        PlayFrom(8);
        _deck.Equalizer.Filter = 0;

        Assert.Contains("filter sweep to the next phrase, 4 bars away", _phrase.Start(PhraseEffect.FilterSweep));
        Assert.Equal(16, _phrase.Phrase!.Value, 3);
        _deck.Seek(TimeSpan.FromSeconds(14));
        _phrase.Update();
        Assert.InRange(_deck.Equalizer.Filter, 0.3f, 0.85f);

        _deck.Seek(TimeSpan.FromSeconds(16.1));
        _phrase.Update();
        Assert.Equal(0, _deck.Equalizer.Filter);
        Assert.Null(_phrase.Kind);
    }

    [Fact]
    public void An_echo_out_echoes_cuts_the_song_on_the_phrase_rings_on_then_stops_the_deck_and_puts_things_back()
    {
        PlayFrom(8);
        (_deck.Effects.Kind, _deck.Effects.On, _deck.Equalizer.Low) = (DeckEffect.Reverb, false, 0.3f);

        _phrase.Start(PhraseEffect.EchoOut);
        Assert.Equal((DeckEffect.Echo, true), (_deck.Effects.Kind, _deck.Effects.On));

        _deck.Seek(TimeSpan.FromSeconds(16.5));
        _phrase.Update();
        Assert.Equal(-1, _deck.Equalizer.Mid);
        Assert.True(_deck.IsPlaying);

        // Two bars of repeats later.
        _deck.Seek(TimeSpan.FromSeconds(20.1));
        Assert.Equal("Deck A: echoed out", _phrase.Update());
        Assert.False(_deck.IsPlaying);
        Assert.Equal((DeckEffect.Reverb, false, 0.3f, 0f), (_deck.Effects.Kind, _deck.Effects.On, _deck.Equalizer.Low, _deck.Equalizer.Mid));
    }

    [Fact]
    public void Pressed_again_or_the_deck_stopped_it_stops_and_puts_things_back()
    {
        PlayFrom(8);
        _phrase.Start(PhraseEffect.EchoOut);
        Assert.Contains("stopped", _phrase.Start(PhraseEffect.EchoOut));
        Assert.False(_deck.Effects.On);

        _phrase.Start(PhraseEffect.FilterSweep);
        _deck.Seek(TimeSpan.FromSeconds(12));
        _phrase.Update();
        _deck.Pause();
        _phrase.Update();
        Assert.Equal(0, _deck.Equalizer.Filter);
        Assert.Null(_phrase.Kind);
    }

    [Fact]
    public void A_deck_stopped_has_nothing_to_build_from()
    {
        Assert.Contains("needs the deck playing", _phrase.Start(PhraseEffect.FilterSweep));
        Assert.Null(_phrase.Kind);
    }
}
