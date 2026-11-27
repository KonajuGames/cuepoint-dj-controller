using Cue.Framework.Window;
using Cuepoint.Streaming;

namespace Cuepoint.Tests;

/// <summary>
/// The Mac's dialogs as AppleScript, and the streaming password in its Keychain: what can be checked away from a Mac.
/// </summary>
public class MacShellTests
{
    [Fact]
    public void Text_in_AppleScript_has_its_quotes_and_backslashes_escaped()
    {
        Assert.Equal("\"The \\\"Best\\\" \\\\ Set\"", MacShell.Quote("The \"Best\" \\ Set"));
    }

    [Fact]
    public void Windows_patterns_become_the_extensions_to_offer()
    {
        Assert.Equal(["wav", "cue"], MacShell.Extensions("*.wav;*.cue"));
        Assert.Equal(["xml"], MacShell.Extensions("*.xml"));
        Assert.Empty(MacShell.Extensions("*.*"));
    }

    [Fact]
    public void Asking_for_a_file_offers_its_types_and_starts_in_its_folder()
    {
        var folder = Path.GetTempPath();

        var script = MacShell.ChooseFileScript("Import a Rekordbox collection", ["xml"], folder);

        Assert.Equal($"POSIX path of (choose file with prompt \"Import a Rekordbox collection\" of type {{\"xml\"}} default location (POSIX file {MacShell.Quote(folder)}))", script);
    }

    [Fact]
    public void Asking_where_to_save_leaves_out_a_folder_that_isn_t_there()
    {
        var script = MacShell.ChooseFileNameScript("Export play history", "Set.m3u8", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.Equal("POSIX path of (choose file name with prompt \"Export play history\" default name \"Set.m3u8\")", script);
    }

    [Fact]
    public void A_saved_file_s_type_is_the_one_its_extension_says_or_the_first_with_its_extension_put_on()
    {
        (string, string)[] types = [("M3U playlist", "*.m3u8"), ("Set list", "*.txt")];

        Assert.Equal(("/Users/dj/Set.txt", 1), MacShell.TypeOf("/Users/dj/Set.txt", types));
        Assert.Equal(("/Users/dj/Set.m3u8", 0), MacShell.TypeOf("/Users/dj/Set", types));
    }

    sealed class FakeStore : ISecretStore
    {
        public string? Saved;
        public bool Save(string secret) => (Saved = secret) is not null;
        public string? Load() => Saved;
    }

    [Fact]
    public void A_password_in_the_Keychain_leaves_only_a_note_of_that_in_the_settings()
    {
        var store = new FakeStore();

        var setting = SavedSecret.Protect("hackme", store);

        Assert.Equal(SavedSecret.IN_KEYCHAIN, setting);
        Assert.Equal("hackme", store.Saved);
        Assert.Equal("hackme", SavedSecret.Unprotect(setting, store));
    }

    [Fact]
    public void A_password_Windows_encrypted_reads_as_none_on_a_Mac()
    {
        Assert.Equal("", SavedSecret.Unprotect("AQAAANCMnd8BFdERjHoAwE", new FakeStore { Saved = "other" }));
        Assert.Null(SavedSecret.Protect("hackme", null));
    }
}
