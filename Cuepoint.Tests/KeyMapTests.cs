using Brutal.GlfwApi;

namespace Cuepoint.Tests;

/// <summary>
/// Changing keys. With the keyboard tests, as the keys in use are shared.
/// </summary>
[Collection(AudioEngineTests.NAME)]
public class KeyMapTests
{
    [Fact]
    public void A_key_given_to_another_binding_swaps_with_the_key_that_was_there()
    {
        var map = KeyMap.Usual();

        // Deck A's cue onto X, which was its play: play takes Z.
        var swapped = map.Set("0.Cue", GlfwKey.X);

        Assert.Equal(("0.Play", GlfwKey.Z), (swapped!.Id, swapped.Key));
        Assert.Equal((GlfwKey.X, GlfwKey.Z), (map.Decks[0].Cue, map.Decks[0].Play));
        Assert.Null(map.Set("0.hotcue1", GlfwKey.F13));
        Assert.Equal(GlfwKey.F13, map.Hotcues[0][0]);
        var keys = map.Bindings.Select(binding => binding.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Keys_kept_for_browsing_and_loading_can_t_be_given()
    {
        var map = KeyMap.Usual();
        Assert.Throws<ArgumentException>(() => map.Set("0.Cue", GlfwKey.Number1));
        Assert.Throws<ArgumentException>(() => map.Set("crossfader.left", GlfwKey.Down));
        Assert.Equal(GlfwKey.Z, map.Decks[0].Cue);
    }

    [Fact]
    public void Keys_changed_are_kept_and_a_broken_file_gives_the_usual_ones()
    {
        using var folder = new TempFolder();
        var path = folder["keys.json"];
        var map = KeyMap.Usual();
        map.Set("1.Sync", GlfwKey.F13);
        map.Set("crossfader.centre", GlfwKey.Backslash);
        Assert.Null(map.Save(path));

        var again = KeyMap.Load(path);
        Assert.Equal(GlfwKey.F13, again.Decks[1].Sync);
        Assert.Equal(GlfwKey.Backslash, again.CrossfaderCentre);
        Assert.Equal(GlfwKey.Z, again.Decks[0].Cue);

        // Two jobs for one key is no good, so it's the usual keys.
        File.WriteAllText(path, "{\"Keys\":{\"0.Cue\":\"X\"}}");
        Assert.Equal(GlfwKey.Z, KeyMap.Load(path).Decks[0].Cue);
        File.WriteAllText(path, "not json");
        Assert.Equal(GlfwKey.Z, KeyMap.Load(path).Decks[0].Cue);
    }

    [Fact]
    public void Applied_the_keyboard_works_by_them_and_the_shortcuts_say_so()
    {
        try
        {
            var map = KeyMap.Usual();
            map.Set("0.hotcue1", GlfwKey.F13);
            map.Set("0.Cue", GlfwKey.Kp1);
            map.Apply();

            Assert.Equal((0, 0), HotcueKeys.For(GlfwKey.F13));
            Assert.Null(HotcueKeys.For(GlfwKey.Q));
            Assert.Equal("Num 1 X C V", KeyboardShortcuts.Decks.Single(row => row.Action == "Cue, play, sync, loop").DeckA);
        }
        finally
        {
            KeyMap.Usual().Apply();
        }
    }
}
