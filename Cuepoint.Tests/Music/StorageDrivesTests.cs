using Cuepoint.Music;

namespace Cuepoint.Tests.Music;

public class StorageDrivesTests
{
    [Fact]
    public void A_Mac_s_drives_are_the_startup_disk_by_its_name_then_the_other_volumes_by_name()
    {
        var drives = StorageDrives.MacDrives([("USB Stick", null), ("Macintosh HD", "/"), (".timemachine", null), ("Archive", null)]);

        Assert.Equal([("/", "Macintosh HD"), ("/Volumes/Archive/", "Archive"), ("/Volumes/USB Stick/", "USB Stick")], drives);
    }

    [Fact]
    public void A_Mac_without_a_link_to_its_startup_disk_still_lists_it()
    {
        Assert.Equal([("/", "Macintosh HD")], StorageDrives.MacDrives([]));
    }

    [Theory]
    [InlineData("/Users/dj/Music/song.mp3", "/")]
    [InlineData("/Volumes/USB Stick/Music/song.mp3", "/Volumes/USB Stick/")]
    [InlineData("/Volumes/USB Stick", "/Volumes/USB Stick/")]
    [InlineData("/volumes/usb stick/song.mp3", "/Volumes/usb stick/")]
    [InlineData("/Volumes/Macintosh HD/Users/dj/song.mp3", "/")]
    [InlineData("/Volumes/", "/")]
    public void A_Mac_file_s_drive_is_the_volume_it_s_on(string path, string root)
    {
        Assert.Equal(root, StorageDrives.MacRootOf(path, "Macintosh HD"));
    }
}
