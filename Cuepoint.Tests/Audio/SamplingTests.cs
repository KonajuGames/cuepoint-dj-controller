using Cuepoint.Audio;
using Cuepoint.Audio.Analysis;
using Cuepoint.Music;

namespace Cuepoint.Tests.Audio;

/// <summary>
/// The sampler's second bank, and sampling a deck into it, on real decks; with the other tests that make an audio
/// engine. The song's a 1 kHz tone at 120 BPM, so a bar's two seconds.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public sealed class SamplingTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Deck _deck;
    readonly Sampler _sampler;

    public SamplingTests()
    {
        Tone(_folder["song.wav"]);
        _deck = _engine.CreateDeck("A");
        _deck.Load(_folder["song.wav"]);
        _deck.Analysis = new TrackAnalysis(BeatGrid.Constant(120, 0, 30), -14, 0.5f, new MusicalKey(9, true));
        _deck.Quantize = false;
        _sampler = new Sampler(_engine);
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _deck.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Tone(string path)
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
        for (var i = 0; i < samples; i++)
            writer.Write((short)(10000 * Math.Sin(2 * Math.PI * 1000 * i / rate)));
    }

    [Fact]
    public void Sixteen_slots_in_two_banks_the_pads_playing_the_bank_chosen()
    {
        var pads = new SamplerPads(_deck, _sampler);

        Assert.Equal(16, Sampler.SLOTS);
        Assert.Equal(("3", "B3"), (Sampler.SlotName(2), Sampler.SlotName(10)));
        Assert.Equal(2, pads.Slot(2));
        pads.Bank = 1;
        Assert.Equal(10, pads.Slot(2));
    }

    [Fact]
    public void Sampled_without_a_loop_it_s_the_bar_up_to_the_beat_just_gone_and_with_one_the_loop()
    {
        _deck.Seek(TimeSpan.FromSeconds(9.3));
        Assert.Equal((TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9)), _deck.SampleRange());

        _deck.SetHotcues([TimeSpan.FromSeconds(6)]);
        _deck.SetHotcueDetails([new HotcueDetail(6, null, null, 8)]);
        _deck.Play();
        _deck.HotcueDown(0);
        Assert.Equal((TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8)), _deck.SampleRange());
    }

    [Fact]
    public void What_s_sampled_is_kept_as_a_WAV_that_loads_as_long_as_it_was()
    {
        _deck.Seek(TimeSpan.FromSeconds(9.3));
        var (from, to) = _deck.SampleRange()!.Value;
        var audio = _deck.AudioBetween(from, to)!;
        Assert.Equal(2, audio.Duration.TotalSeconds, 2);
        Assert.True(audio.Samples.Max(MathF.Abs) > 0.2f);

        var path = _folder["Samples", "bar.wav"];
        Assert.Null(Sampler.WriteWav(path, audio));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44 + audio.Samples.Length * 2, bytes.Length);
        Assert.Equal(audio.SampleRate, BitConverter.ToInt32(bytes, 24));
    }

    [Fact]
    public void Nothing_loaded_has_nothing_to_sample()
    {
        using var empty = _engine.CreateDeck("B");
        Assert.Null(empty.SampleRange());
        Assert.Null(empty.AudioBetween(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }
}
