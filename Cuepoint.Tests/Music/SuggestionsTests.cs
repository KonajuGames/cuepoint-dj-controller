using Cuepoint.Audio.Analysis;
using Cuepoint.Music;
using static Cuepoint.Tests.Music.LibraryFileTests;

namespace Cuepoint.Tests.Music;

public class SuggestionsTests
{
    // 8A is A minor; 9A E minor; 7A D minor; 10A B minor; 8B C major; 3A Bb minor.
    static readonly MusicalKey A8 = new(9, true);
    static readonly MusicalKey A9 = new(4, true);
    static readonly MusicalKey A7 = new(2, true);
    static readonly MusicalKey A10 = new(11, true);
    static readonly MusicalKey B8 = new(0, false);
    static readonly MusicalKey A3 = new(10, true);

    static readonly Playing Now = new(@"C:\Music\now.mp3", A8, 124, 7);

    static Song At(string name, MusicalKey key, double bpm, int? energy = null)
    {
        var song = Song($@"C:\Music\{name}.mp3", title: name);
        song.Analysis = new TrackAnalysis(BeatGrid.Constant(bpm, 0, 300), -9, 0.9f, key, energy);
        return song;
    }

    [Fact]
    public void Keys_score_by_how_they_sit_on_the_Camelot_wheel()
    {
        Assert.Equal(1, Suggestions.KeyScore(A8, A8));
        Assert.Equal(0.85, Suggestions.KeyScore(A8, A9));
        Assert.Equal(0.85, Suggestions.KeyScore(A8, A7));
        Assert.Equal(0.85, Suggestions.KeyScore(A8, B8));
        Assert.Equal(0.4, Suggestions.KeyScore(A8, A10));
        Assert.Equal(0, Suggestions.KeyScore(A8, A3));
    }

    [Fact]
    public void Tempos_within_the_fader_s_reach_score_by_how_close_half_and_double_time_too()
    {
        Assert.Equal(1, Suggestions.TempoScore(124, 124), 9);
        Assert.Equal(1, Suggestions.TempoScore(124, 62), 9);
        Assert.Equal(0.5, Suggestions.TempoScore(100, 104), 9);
        Assert.True(Suggestions.TempoScore(124, 140) < 0);
    }

    [Fact]
    public void A_step_up_in_energy_is_best_and_big_jumps_least()
    {
        Assert.True(Suggestions.EnergyScore(7, 8) > Suggestions.EnergyScore(7, 7));
        Assert.True(Suggestions.EnergyScore(7, 7) > Suggestions.EnergyScore(7, 6));
        Assert.True(Suggestions.EnergyScore(7, 6) > Suggestions.EnergyScore(7, 9));
        Assert.True(Suggestions.EnergyScore(7, 10) < Suggestions.EnergyScore(7, 5));
        Assert.Equal(0.6, Suggestions.EnergyScore(null, 8));
    }

    [Fact]
    public void The_best_come_first_and_clashes_out_of_reach_and_skipped_songs_aren_t_suggested()
    {
        var perfect = At("perfect", A8, 124, 8);
        var neighbour = At("neighbour", A9, 125, 7);
        var boost = At("boost", A10, 124, 8);
        var clash = At("clash", A3, 124, 8);
        var tooFast = At("too fast", A8, 140, 8);
        var played = At("played", A8, 124, 8);
        var unanalysed = Song(@"C:\Music\unanalysed.mp3");
        var now = At("now", A8, 124, 7);

        var suggested = Suggestions.For(Now, [boost, clash, neighbour, tooFast, played, unanalysed, perfect, now], song => song == played);

        Assert.Equal(["perfect", "neighbour", "boost"], suggested.Select(suggestion => suggestion.Song.Title));
        Assert.Equal("8A → 8A, same tempo, energy 7 → 8", suggested[0].Why);
        Assert.Equal("8A → 9A, +0.8% tempo, energy 7 → 7", suggested[1].Why);
    }
}
