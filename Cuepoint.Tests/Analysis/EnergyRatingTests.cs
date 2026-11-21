using Cuepoint.Audio.Analysis;
using Cuepoint.Music;
using static Cuepoint.Tests.Music.LibraryFileTests;

namespace Cuepoint.Tests.Analysis;

public class EnergyRatingTests
{
    [Fact]
    public void Louder_faster_and_more_full_on_is_more_energy_from_1_to_10()
    {
        Assert.Equal(1, EnergyRating.Rate(-30, 60, 0));
        Assert.Equal(10, EnergyRating.Rate(-5, 150, 1));
        var middling = EnergyRating.Rate(-12, 110, 0.5);
        Assert.InRange(middling, 4, 7);
        Assert.True(EnergyRating.Rate(-8, 110, 0.5) > middling);
        Assert.True(EnergyRating.Rate(-12, 140, 0.5) > middling);
        Assert.True(EnergyRating.Rate(-12, 110, 0.9) > middling);
    }

    [Fact]
    public void The_full_on_share_leaves_out_silence_and_counts_the_quiet_stretches()
    {
        const int rate = 100;
        // Two silent seconds, six loud, four quiet, then two silent again.
        var mono = new float[14 * rate];
        for (var i = 2 * rate; i < 8 * rate; i++)
            mono[i] = i % 2 == 0 ? 0.5f : -0.5f;
        for (var i = 8 * rate; i < 12 * rate; i++)
            mono[i] = i % 2 == 0 ? 0.1f : -0.1f;

        Assert.Equal(0.6, EnergyRating.FullShare(mono, rate), 6);
        Assert.Equal(0, EnergyRating.FullShare(new float[5 * rate], rate));
    }

    [Fact]
    public void An_energy_search_finds_that_much_or_a_range_and_needs_analysis()
    {
        Song With(int? energy) => new Song(@"C:\a.mp3", 1, default, "a", "b", "c") { Analysis = Analysed() with { Energy = energy } };

        var seven = SongQuery.Parse("energy:7");
        Assert.True(seven.NeedsAnalysis);
        Assert.True(seven.Matches(With(7)));
        Assert.False(seven.Matches(With(8)));
        Assert.False(seven.Matches(With(null)));

        var range = SongQuery.Parse("energy:8-6");
        Assert.Equal((6, 8), (range.MinEnergy, range.MaxEnergy));
        Assert.True(range.Matches(With(6)));
        Assert.False(range.Matches(With(9)));
        Assert.Equal(["energy:11"], SongQuery.Parse("energy:11").Words);
    }

    [Fact]
    public void Energy_is_kept_with_the_analysis()
    {
        using var folder = new TempFolder();
        var song = Song(folder["Music", "a.mp3"]);
        var file = LibraryFile.Load(folder["library.json"]);
        file.Set(song, Analysed() with { Energy = 8 });
        file.Save();

        Assert.True(LibraryFile.Load(folder["library.json"]).TryGet(song, out var analysis, out _));
        Assert.Equal(8, analysis!.Energy);
    }
}
