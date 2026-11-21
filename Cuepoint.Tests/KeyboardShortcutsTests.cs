namespace Cuepoint.Tests;

[Collection(AudioEngineTests.NAME)]
public class KeyboardShortcutsTests
{
    [Fact]
    public void The_decks_shortcuts_read_as_the_keys_really_are()
    {
        var cue = KeyboardShortcuts.Decks.Single(row => row.Action == "Cue, play, sync, loop");
        Assert.Equal(("Z X C V", "N M , ."), (cue.DeckA, cue.DeckB));
        var hotcues = KeyboardShortcuts.Decks.Single(row => row.Action == "Hotcues 1 to 8");
        Assert.Equal(("Q W E R A S D F", "U I O P J K L ;"), (hotcues.DeckA, hotcues.DeckB));
    }

    [Fact]
    public void Every_deck_key_is_in_the_shortcuts()
    {
        for (var deck = 0; deck < KeyboardControls.Keys.Length; deck++)
        {
            var keys = KeyboardControls.Keys[deck];
            var listed = string.Join(" ", KeyboardShortcuts.Decks.Select(row => deck == 0 ? row.DeckA : row.DeckB));
            var words = listed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word.Length > 1 ? word.TrimEnd(',') : word);
            foreach (var key in typeof(DeckKeys).GetProperties().Where(p => p.PropertyType == typeof(Brutal.GlfwApi.GlfwKey)))
                Assert.Contains(DeckKeys.Label((Brutal.GlfwApi.GlfwKey)key.GetValue(keys)!), words);
        }
    }
}
