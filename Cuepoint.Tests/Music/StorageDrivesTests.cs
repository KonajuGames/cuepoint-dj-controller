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

    [Fact]
    public void A_drive_s_files_keep_paths_with_Windows_separators_so_either_computer_reads_them()
    {
        Assert.Equal(@"Music\Set\a.mp3", StorageDrives.ToKept("Music/Set/a.mp3", windows: false));
        Assert.Equal("Music/Set/a.mp3", StorageDrives.FromKept(@"Music\Set\a.mp3", windows: false));
        Assert.Equal(@"Music\Set\a.mp3", StorageDrives.FromKept(@"Music\Set\a.mp3", windows: true));
        Assert.Equal(@"Music\Set\a.mp3", StorageDrives.ToKept(@"Music\Set\a.mp3", windows: true));
    }

    [Fact]
    public void Names_leave_out_what_Windows_won_t_have_wherever_they_re_made()
    {
        foreach (var c in "<>:\"/\\|?*\0\t")
            Assert.Contains(c, StorageDrives.InvalidNameChars);
        Assert.DoesNotContain('-', StorageDrives.InvalidNameChars);
        Assert.Equal("Friday- late-night", DriveExport.SafeName("Friday: late/night"));
    }

    [Theory]
    [InlineData("file://localhost/Users/dj/Music/A%20Song%20%26%20B.mp3", "/Users/dj/Music/A Song & B.mp3")]
    [InlineData("file:///Volumes/USB/Set/x.mp3", "/Volumes/USB/Set/x.mp3")]
    [InlineData("file://localhost/C:/Music/x.mp3", null)]
    public void A_Mac_s_Rekordbox_locations_are_paths_from_the_root(string location, string? path)
    {
        Assert.Equal(path, Cuepoint.Music.Imports.RekordboxXml.ToPath(location, windows: false));
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
