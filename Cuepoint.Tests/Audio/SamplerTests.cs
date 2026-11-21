using Cuepoint.Audio;
using Cuepoint.Music;

namespace Cuepoint.Tests.Audio;

[Collection(AudioEngineTests.NAME)]
public sealed class SamplerTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly AudioEngine _engine = new();
    readonly Sampler _sampler;

    public SamplerTests()
    {
        _sampler = new Sampler(_engine);
        Silence(_folder["horn.wav"]);
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _engine.Dispose();
        _folder.Dispose();
    }

    static void Silence(string path, double seconds = 5)
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

    void WaitUntilReady(int slot)
    {
        for (var i = 0; i < 200 && !_sampler.IsReady(slot); i++)
        {
            Thread.Sleep(10);
            _sampler.Update();
        }
        Assert.True(_sampler.IsReady(slot));
    }

    [Fact]
    public void A_sample_plays_from_its_pad_and_comes_off_it()
    {
        var changes = 0;
        _sampler.Changed += () => changes++;

        _sampler.Load(2, _folder["horn.wav"], "Horn");
        WaitUntilReady(2);
        Assert.Equal("Horn", _sampler.NameOf(2));
        Assert.Equal(_folder["horn.wav"], _sampler.Paths[2]);

        _sampler.Trigger(2);
        Assert.True(_sampler.IsPlaying(2));
        _sampler.Clear(2);
        Assert.False(_sampler.IsPlaying(2));
        Assert.Null(_sampler.NameOf(2));
        Assert.Null(_sampler.Paths[2]);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void An_empty_pad_does_nothing()
    {
        _sampler.Trigger(0);
        Assert.False(_sampler.IsPlaying(0));
        _sampler.Trigger(Sampler.SLOTS);
        Assert.Null(_sampler.NameOf(-1));
    }

    [Fact]
    public void Sampler_mode_turns_the_other_pad_modes_off_and_they_turn_it_off()
    {
        using var deck = _engine.CreateDeck("A");
        var roll = new RollPads(deck);
        var jump = new JumpPads(deck, roll: roll);
        var pads = new SamplerPads(deck, _sampler, roll: roll, jump: jump);

        jump.Active = true;
        pads.Active = true;
        Assert.True(pads.Active);
        Assert.False(jump.Active);
        roll.Active = true;
        Assert.False(pads.Active);
    }
}
