using Brutality.Audio.Analysis;

namespace Brutality.Tests.Analysis;

public class MusicalKeyTests
{
    [Theory]
    [InlineData(9, true, "A minor", "Am", "8A")]
    [InlineData(0, false, "C major", "C", "8B")]
    [InlineData(3, true, "Eb minor", "Ebm", "2A")]
    [InlineData(8, true, "Ab minor", "Abm", "1A")]
    [InlineData(1, true, "C# minor", "C#m", "12A")]
    [InlineData(7, false, "G major", "G", "9B")]
    public void Keys_are_named_and_placed_on_the_Camelot_wheel(int tonic, bool minor, string name, string shortName, string camelot)
    {
        var key = new MusicalKey(tonic, minor);

        Assert.Equal(name, key.Name);
        Assert.Equal(shortName, key.ShortName);
        Assert.Equal(camelot, key.Camelot);
    }

    [Fact]
    public void Every_key_reads_back_from_its_name()
    {
        for (var tonic = 0; tonic < 12; tonic++)
        {
            foreach (var minor in new[] { false, true })
            {
                var key = new MusicalKey(tonic, minor);
                Assert.Equal(key, MusicalKey.Parse(key.Name));
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("H major")]
    [InlineData("A dorian")]
    [InlineData("A  minor")]
    public void Anything_else_does_not_read_as_a_key(string? name)
    {
        Assert.Null(MusicalKey.Parse(name));
    }

    [Theory]
    // The same key, a step either way round the wheel, and the relative major or minor mix.
    [InlineData(9, true, 9, true, true)]
    [InlineData(9, true, 4, true, true)]
    [InlineData(9, true, 2, true, true)]
    [InlineData(9, true, 0, false, true)]
    // Two steps round, or a step round and across, don't.
    [InlineData(9, true, 11, true, false)]
    [InlineData(9, true, 7, false, false)]
    // Round past 12 to 1.
    [InlineData(1, true, 8, true, true)]
    public void Keys_mix_by_the_Camelot_rules(int tonicA, bool minorA, int tonicB, bool minorB, bool mixes)
    {
        var a = new MusicalKey(tonicA, minorA);
        var b = new MusicalKey(tonicB, minorB);

        Assert.Equal(mixes, a.MixesWith(b));
        Assert.Equal(mixes, b.MixesWith(a));
    }

    [Fact]
    public void Transposing_wraps_round_the_octave()
    {
        var c = new MusicalKey(0, false);

        Assert.Equal(new MusicalKey(11, false), c.Transposed(-1));
        Assert.Equal(new MusicalKey(2, false), c.Transposed(14));
        Assert.Equal(c, c.Transposed(12));
    }
}
