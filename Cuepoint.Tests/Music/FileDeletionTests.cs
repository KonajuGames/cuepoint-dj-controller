using Cuepoint.Music;

namespace Cuepoint.Tests.Music;

public class FileDeletionTests : IDisposable
{
    readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_file_not_for_the_recycle_bin_is_deleted_for_good()
    {
        var path = _folder.File("Music", "a.mp3");

        Assert.Null(FileDeletion.Delete(path, recycle: false));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_file_that_cant_be_deleted_says_why_and_is_left()
    {
        var path = _folder.File("Music", "a.mp3");
        using var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(FileDeletion.Delete(path, recycle: false));
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void Away_from_windows_nothing_is_recycled()
    {
        var path = _folder.File("Music", "a.mp3");

        if (!OperatingSystem.IsWindows())
            Assert.False(FileDeletion.WouldRecycle(path));
        else
            FileDeletion.WouldRecycle(path); // Whatever this computer's set to, it answers without throwing.
    }
}
