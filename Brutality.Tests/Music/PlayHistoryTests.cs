using System.Text;
using Brutality.Audio;
using Brutality.Music;

namespace Brutality.Tests.Music;

public class PlayHistoryTests
{
    static readonly DateTime Evening = new(2026, 11, 3, 21, 30, 0);

    static readonly (DateTime, string)[] History =
    [
        (Evening, @"F:\Music\Avicii\Wake Me Up.mp3"),
        (Evening.AddMinutes(4), @"F:\Music\Unknown\track07.mp3"),
        (Evening.AddHours(3), @"F:\Music\Alan Walker\Faded.m4a"),
    ];

    static Song? SongAt(string path) =>
        path.Contains("Wake Me Up") ? new Song(path, 1, default, "Wake Me Up", "Avicii", "True") { Duration = TimeSpan.FromSeconds(249.4) }
        : path.Contains("Faded") ? new Song(path, 1, default, "Faded", "Alan Walker", "Faded") { Duration = TimeSpan.FromSeconds(212) }
        : null;

    [Fact]
    public void A_playlist_has_each_song_its_length_and_name_then_its_file_in_order()
    {
        var m3u = PlayHistory.Write(History, HistoryFormat.M3u, SongAt);

        Assert.Equal(
            "#EXTM3U\n#PLAYLIST:Played 2026-11-03 21:30\n"
                + "#EXTINF:249,Avicii - Wake Me Up\nF:\\Music\\Avicii\\Wake Me Up.mp3\n"
                + "#EXTINF:-1,track07\nF:\\Music\\Unknown\\track07.mp3\n"
                + "#EXTINF:212,Alan Walker - Faded\nF:\\Music\\Alan Walker\\Faded.m4a\n",
            m3u
        );
    }

    [Fact]
    public void A_set_list_has_each_song_with_when_it_started_under_its_day()
    {
        var text = PlayHistory.Write(History, HistoryFormat.Text, SongAt);

        var lines = text.Split('\n');
        Assert.StartsWith("Played on ", lines[0]);
        Assert.Contains("21:30  Avicii - Wake Me Up", lines);
        Assert.Contains("21:34  track07", lines);
        // Past midnight, the next day gets a heading of its own.
        Assert.Equal(2, lines.Count(line => line.StartsWith("Played on ")));
        Assert.True(Array.IndexOf(lines, "00:30  Alan Walker - Faded") > Array.FindLastIndex(lines, line => line.StartsWith("Played on ")));
    }

    [Fact]
    public void A_set_list_is_saved_with_Windows_line_endings_and_a_playlist_without_a_byte_order_mark()
    {
        using var folder = new TempFolder();

        Assert.Null(PlayHistory.Save(folder["set.txt"], "Played on Tuesday\n\n21:30  A - B\n", HistoryFormat.Text));
        Assert.Null(PlayHistory.Save(folder["set.m3u8"], "#EXTM3U\n#EXTINF:1,Ä - Ö\nF:\\a.mp3\n", HistoryFormat.M3u));

        Assert.Equal("Played on Tuesday\r\n\r\n21:30  A - B\r\n", File.ReadAllText(folder["set.txt"]));
        var bytes = File.ReadAllBytes(folder["set.m3u8"]);
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Contains("Ä - Ö", Encoding.UTF8.GetString(bytes));
        Assert.NotNull(PlayHistory.Save(folder["Nowhere", "set.txt"], "x", HistoryFormat.Text));
    }

}

/// <summary>
/// The play history as it's kept, with a real deck playing; with the other tests that make an audio engine, as only one
/// can be going at a time.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public class SessionPlaysTests
{
    static readonly DateTime Evening = new(2026, 11, 3, 21, 30, 0);

    /// <summary>
    /// Silence as a WAV, for a deck to load and play.
    /// </summary>
    static void Silence(string path, double seconds = 30)
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

    [Fact]
    public void Songs_played_ten_seconds_go_in_the_history_in_order_each_time_and_marked_unplayed_come_out()
    {
        using var folder = new TempFolder();
        Silence(folder["one.wav"]);
        Silence(folder["two.wav"]);
        using var engine = new AudioEngine();
        using var deck = engine.CreateDeck("A");
        var now = Evening;
        var plays = new SessionPlays { Now = () => now };

        void Play(string path, double seconds)
        {
            deck.Load(path);
            plays.Restart(deck);
            deck.Play();
            now += TimeSpan.FromSeconds(seconds);
            plays.Update([deck], seconds);
        }

        Play(folder["one.wav"], 12);
        Play(folder["two.wav"], 5);
        Play(folder["one.wav"], 11);

        Assert.True(deck.IsPlaying);
        Assert.Equal(new[] { folder["one.wav"], folder["one.wav"] }, plays.History.Select(entry => entry.Path));
        // Each from when it started: twelve seconds before the first count, and eleven before the last.
        Assert.Equal(Evening, plays.History[0].Started);
        Assert.Equal(Evening.AddSeconds(12 + 5), plays.History[1].Started);

        plays.Forget([folder["one.wav"]]);
        Assert.Empty(plays.History);
    }
}
