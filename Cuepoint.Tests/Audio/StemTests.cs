using Cuepoint.Audio;
using Cuepoint.Audio.Stems;

namespace Cuepoint.Tests.Audio;

public class StemTests
{
    const int RATE = 44100;
    const int CHANNELS = 2;

    // A song of three made-up stems and the rest, a few seconds long, and the samples a deck would play.
    static (PcmAudio Song, SongStems Stems, float[] Others) MakeSong(int seconds = 4)
    {
        var samples = seconds * RATE * CHANNELS;
        var random = new Random(7);
        float[] Noise() => Enumerable.Range(0, samples).Select(_ => (float)(random.NextDouble() - 0.5) * 0.2f).ToArray();
        var (vocals, drums, bass, rest) = (Noise(), Noise(), Noise(), Noise());
        var song = new float[samples];
        for (var i = 0; i < samples; i++)
            song[i] = vocals[i] + drums[i] + bass[i] + rest[i];
        return (new PcmAudio(song, CHANNELS, RATE), new SongStems(vocals, drums, bass, CHANNELS, RATE), rest);
    }

    static void Close(float expected, float actual, float within) =>
        Assert.True(MathF.Abs(expected - actual) <= within, $"{actual} isn't within {within} of {expected}");

    static void AssertMix(PcmAudio song, SongStems stems, float[] rest, float[] levels, int from = 0)
    {
        for (var i = from * CHANNELS; i < song.Samples.Length; i++)
        {
            var expected = stems.Vocals[i] * levels[0] + stems.Drums[i] * levels[1] + stems.Bass[i] * levels[2] + rest[i] * levels[3];
            Assert.True(MathF.Abs(song.Samples[i] - expected) < 1e-5f, $"Sample {i}: {song.Samples[i]} not {expected}");
        }
    }

    [Fact]
    public async Task A_stem_turned_off_leaves_the_rest_of_the_song_throughout()
    {
        var (song, stems, rest) = MakeSong();
        var mixer = new StemMixer(song, stems);

        await mixer.Set([0, 1, 1, 1], playhead: RATE);

        AssertMix(song, stems, rest, [0, 1, 1, 1]);
        Assert.Equal(0, mixer[Stem.Vocals]);
    }

    [Fact]
    public async Task Only_the_instruments_left_is_the_song_less_the_other_three()
    {
        var (song, stems, rest) = MakeSong();
        var mixer = new StemMixer(song, stems);

        await mixer.Set([0, 0, 0, 1], playhead: 0);

        AssertMix(song, stems, rest, [0, 0, 0, 1]);
    }

    [Fact]
    public async Task All_on_again_is_the_song_exactly()
    {
        var (song, stems, _) = MakeSong();
        var original = song.Samples.ToArray();
        var mixer = new StemMixer(song, stems);

        await mixer.Set([0, 0.5f, 0, 1], playhead: RATE);
        await mixer.Set([1, 1, 1, 1], playhead: 2 * RATE);

        for (var i = 0; i < original.Length; i++)
            Assert.True(MathF.Abs(song.Samples[i] - original[i]) < 1e-5f, $"Sample {i}");
        Assert.Equal(original, mixer.Original);
    }

    [Fact]
    public void A_change_is_heard_straight_away_just_after_the_playhead_easing_in()
    {
        var (song, stems, rest) = MakeSong();
        var mixer = new StemMixer(song, stems);
        var playhead = RATE;

        _ = mixer.Set([1, 0, 1, 1], playhead);

        // Before the change, as it was; a little after, the drums gone, without waiting for the rest of the song.
        var lead = (int)(0.08 * RATE);
        for (var i = 0; i < CHANNELS * 100; i++)
        {
            var at = playhead * CHANNELS + i;
            Close(stems.Vocals[at] + stems.Drums[at] + stems.Bass[at] + rest[at], song.Samples[at], 1e-5f);
        }
        var after = (playhead + lead + (int)(0.015 * RATE)) * CHANNELS;
        for (var i = after; i < after + RATE; i++)
            Close(stems.Vocals[i] + stems.Bass[i] + rest[i], song.Samples[i], 1e-5f);
    }

    [Fact]
    public void Stems_kept_on_disk_come_back_for_the_same_file()
    {
        using var folder = new TempFolder();
        var songPath = folder.File("song.mp3");
        var (_, stems, _) = MakeSong(1);
        var cache = new StemCache(folder["stems"]);

        cache.Save(songPath, stems);
        var loaded = cache.Load(songPath);

        Assert.NotNull(loaded);
        Assert.Equal((stems.Channels, stems.SampleRate, stems.Frames), (loaded.Channels, loaded.SampleRate, loaded.Frames));
        // Kept as 16-bit floats: close, not exact.
        for (var i = 0; i < stems.Vocals.Length; i++)
        {
            Close(stems.Vocals[i], loaded.Vocals[i], 1e-3f);
            Close(stems.Bass[i], loaded.Bass[i], 1e-3f);
        }
    }

    [Fact]
    public void Stems_kept_for_a_file_since_changed_are_not_used()
    {
        using var folder = new TempFolder();
        var songPath = folder.File("song.mp3");
        var (_, stems, _) = MakeSong(1);
        var cache = new StemCache(folder["stems"]);
        cache.Save(songPath, stems);

        File.AppendAllText(songPath, "edited");

        Assert.Null(cache.Load(songPath));
    }

    [Fact]
    public void Stems_kept_on_disk_say_how_much_room_they_take_and_can_be_deleted()
    {
        using var folder = new TempFolder();
        var songPath = folder.File("song.mp3");
        var (_, stems, _) = MakeSong(1);
        var cache = new StemCache(folder["stems"]);
        Assert.Null(cache.SizeOf(songPath));
        cache.Save(songPath, stems);

        // Three stems of 16-bit samples, and a little to say what they are.
        Assert.InRange(cache.SizeOf(songPath)!.Value, 3L * stems.Vocals.Length * 2, 3L * stems.Vocals.Length * 2 + 64);

        cache.Delete(songPath);

        Assert.False(cache.Has(songPath));
        Assert.Null(cache.SizeOf(songPath));
        Assert.Null(cache.Load(songPath));
    }

    [Fact]
    public async Task Stems_deleted_are_found_again_only_by_separating()
    {
        using var folder = new TempFolder();
        var songPath = folder.File("song.mp3");
        var (song, stems, _) = MakeSong(1);
        var cache = new StemCache(folder["stems"]);
        cache.Save(songPath, stems);
        using var separator = new StemSeparator(cache, folder["no model.onnx"]);
        var found = separator.Request(songPath, song, separate: false);
        await found.Task;
        Assert.NotNull(found.Stems);

        separator.DeleteStems(songPath);

        var again = separator.Request(songPath, song, separate: false);
        await again.Task;
        Assert.NotSame(found, again);
        Assert.Null(again.Stems);
        Assert.False(separator.HasStems(songPath));
    }

    [Fact]
    public void Stems_follow_a_song_whose_tags_were_written()
    {
        using var folder = new TempFolder();
        var songPath = folder.File("song.mp3");
        var (_, stems, _) = MakeSong(1);
        var cache = new StemCache(folder["stems"]);
        cache.Save(songPath, stems);
        var before = cache.FileFor(songPath);

        File.AppendAllText(songPath, "new tags");
        Assert.False(cache.Has(songPath));

        Assert.True(cache.Follow(before, songPath));
        Assert.NotNull(cache.Load(songPath));
        Assert.False(File.Exists(before));
        // Nothing more to follow.
        Assert.False(cache.Follow(before, songPath));
    }

    [Fact]
    public void The_quick_look_for_stems_follows_them_being_saved_deleted_and_followed()
    {
        using var folder = new TempFolder();
        var songPath = folder.File("song.mp3");
        var (_, stems, _) = MakeSong(1);
        var cache = new StemCache(folder["stems"]);
        Assert.False(cache.HasQuickly(songPath));

        cache.Save(songPath, stems);
        Assert.True(cache.HasQuickly(songPath));

        var before = cache.FileFor(songPath);
        File.AppendAllText(songPath, "new tags");
        cache.Follow(before, songPath);
        Assert.True(cache.HasQuickly(songPath));

        cache.Delete(songPath);
        Assert.False(cache.HasQuickly(songPath));
    }

    [Fact]
    public void The_quick_look_for_stems_finds_those_kept_before_the_app_started()
    {
        using var folder = new TempFolder();
        var songPath = folder.File("song.mp3");
        var (_, stems, _) = MakeSong(1);
        new StemCache(folder["stems"]).Save(songPath, stems);

        Assert.True(new StemCache(folder["stems"]).HasQuickly(songPath));
        Assert.False(new StemCache(folder["stems"]).HasQuickly(folder.File("other.mp3")));
    }
}
