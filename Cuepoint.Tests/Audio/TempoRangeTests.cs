using Cuepoint.Audio.Analysis;

namespace Cuepoint.Tests.Audio;

public class TempoRangeTests
{
    static TrackAnalysis At(double bpm) => new(BeatGrid.Constant(bpm, 0.2, 300), -9, 0.9f, new MusicalKey(9, true));

    [Theory]
    [InlineData(87, 88, 174)]
    [InlineData(174, 88, 174)]
    [InlineData(180, 70, 90)]
    [InlineData(140, 70, 70)]
    [InlineData(139.9, 70, 139.9)]
    [InlineData(124, 80, 124)]
    [InlineData(62, 100, 124)]
    public void A_tempo_is_doubled_or_halved_into_the_octave_chosen(double analysed, double min, double shown)
    {
        Assert.Equal(shown, TempoRange.Fold(At(analysed), min).Grid.MainBpm, 3);
    }

    [Fact]
    public void Folding_keeps_the_beats_where_they_were_and_a_tempo_in_range_as_it_is()
    {
        var analysis = At(87);
        var doubled = TempoRange.Fold(analysis, 88);

        // Every beat at 87 is still a beat at 174, with one between each.
        Assert.Equal(analysis.Grid.TimeOfBeat(10), doubled.Grid.TimeOfBeat(20), 6);
        Assert.Equal(analysis.Key, doubled.Key);
        Assert.Same(analysis, TempoRange.Fold(analysis, 80));
    }

    [Fact]
    public void The_ranges_are_named_by_their_octave_and_a_choice_snaps_to_the_nearest()
    {
        Assert.Equal("88–176 BPM", TempoRange.Name(88));
        var before = TempoRange.Min;
        try
        {
            TempoRange.Min = 87;
            Assert.Equal(88, TempoRange.Min);
        }
        finally
        {
            TempoRange.Min = before;
        }
    }
}
