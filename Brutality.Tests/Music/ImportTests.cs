using System.Buffers.Binary;
using System.Text;
using Brutality.Music.Imports;

namespace Brutality.Tests.Music;

public class RekordboxXmlTests
{
    const string Collection = """
        <?xml version="1.0" encoding="UTF-8"?>
        <DJ_PLAYLISTS Version="1.0.0">
          <PRODUCT Name="rekordbox" Version="6.8.5" Company="AlphaTheta"/>
          <COLLECTION Entries="3">
            <TRACK TrackID="11" Name="One" Artist="A" Location="file://localhost/C:/Music/A%20Song.mp3">
              <TEMPO Inizio="0.025" Bpm="128.00" Metro="4/4" Battito="1"/>
              <POSITION_MARK Name="" Type="0" Start="0.025" Num="-1"/>
              <POSITION_MARK Name="Drop" Type="0" Start="64.5" Num="0" Red="40" Green="226" Blue="20"/>
              <POSITION_MARK Name="" Type="0" Start="96.125" Num="3"/>
              <POSITION_MARK Name="" Type="4" Start="120" End="128" Num="1"/>
            </TRACK>
            <TRACK TrackID="12" Name="Two" Location="file://localhost/D:/Sets/Two.flac"/>
            <TRACK TrackID="13" Name="Mac" Location="file://localhost/Users/dj/Music/Three.mp3">
              <POSITION_MARK Name="" Type="0" Start="5" Num="0"/>
            </TRACK>
          </COLLECTION>
          <PLAYLISTS>
            <NODE Type="0" Name="ROOT" Count="2">
              <NODE Name="Gigs" Type="0" Count="1">
                <NODE Name="Friday" Type="1" KeyType="0" Entries="2">
                  <TRACK Key="12"/>
                  <TRACK Key="11"/>
                </NODE>
              </NODE>
              <NODE Name="By location" Type="1" KeyType="1" Entries="1">
                <TRACK Key="file://localhost/C:/Music/A%20Song.mp3"/>
              </NODE>
            </NODE>
          </PLAYLISTS>
        </DJ_PLAYLISTS>
        """;

    static RekordboxCollection Load(TempFolder folder, string xml = Collection)
    {
        File.WriteAllText(folder["rekordbox.xml"], xml);
        return RekordboxXml.Load(folder["rekordbox.xml"])!;
    }

    [Fact]
    public void Hot_cues_A_to_H_come_from_cue_marks_numbered_0_to_7()
    {
        using var folder = new TempFolder();
        var collection = Load(folder);

        var cues = collection.Hotcues[@"C:\Music\A Song.mp3"];

        Assert.Equal(8, cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(64.5), cues[0]);
        // A hot loop on B is a hot cue at its start; a memory cue (-1) isn't one.
        Assert.Equal(TimeSpan.FromSeconds(120), cues[1]);
        Assert.Equal(TimeSpan.FromSeconds(96.125), cues[3]);
        Assert.Equal(new[] { 0, 1, 3 }, cues.Select((cue, i) => (cue, i)).Where(pair => pair.cue is not null).Select(pair => pair.i));
    }

    [Fact]
    public void Songs_without_hot_cues_and_songs_on_a_Mac_are_left_out()
    {
        using var folder = new TempFolder();
        var collection = Load(folder);

        Assert.Single(collection.Hotcues);
    }

    [Fact]
    public void Playlists_keep_their_folders_and_order_by_ID_or_location()
    {
        using var folder = new TempFolder();
        var collection = Load(folder);

        Assert.Equal(new[] { "Gigs", "By location" }, collection.Playlists.Select(node => node.Name));
        var gigs = collection.Playlists[0];
        Assert.True(gigs.IsFolder);
        var friday = Assert.Single(gigs.Children);
        Assert.False(friday.IsFolder);
        Assert.Equal(new[] { @"D:\Sets\Two.flac", @"C:\Music\A Song.mp3" }, friday.Paths);
        Assert.Equal(new[] { @"C:\Music\A Song.mp3" }, collection.Playlists[1].Paths);
    }

    [Fact]
    public void Only_a_Rekordbox_collection_counts_as_one()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder["rekordbox.xml"], Collection);
        File.WriteAllText(folder["other.xml"], "<settings><a/></settings>");
        File.WriteAllText(folder["broken.xml"], "<DJ_PLAYLISTS");

        Assert.True(RekordboxXml.IsCollection(folder["rekordbox.xml"]));
        Assert.False(RekordboxXml.IsCollection(folder["other.xml"]));
        Assert.Null(RekordboxXml.Load(folder["other.xml"]));
        Assert.Null(RekordboxXml.Load(folder["broken.xml"]));
    }

    [Theory]
    [InlineData("file://localhost/C:/Music/A%20Song%20%26%20B.mp3", @"C:\Music\A Song & B.mp3")]
    [InlineData("file:///D:/x.mp3", @"D:\x.mp3")]
    [InlineData("file://localhost/Users/dj/x.mp3", null)]
    [InlineData("http://example.com/x.mp3", null)]
    public void Locations_are_file_URLs(string location, string? expected)
    {
        Assert.Equal(expected, RekordboxXml.ToPath(location));
    }
}

public class SeratoTests
{
    /// <summary>
    /// A tag as Serato writes them: four letters, a big-endian length, and the value.
    /// </summary>
    static byte[] Tag(string name, byte[] value)
    {
        var bytes = new byte[8 + value.Length];
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), value.Length);
        value.CopyTo(bytes, 8);
        return bytes;
    }

    static byte[] Crate(params string[] paths) =>
        [
            .. Tag("vrsn", Encoding.BigEndianUnicode.GetBytes("1.0/Serato ScratchLive Crate")),
            .. Tag("osrt", Tag("tvcn", Encoding.BigEndianUnicode.GetBytes("song"))),
            .. paths.SelectMany(path => Tag("otrk", Tag("ptrk", Encoding.BigEndianUnicode.GetBytes(path)))),
        ];

    [Fact]
    public void A_crates_songs_are_its_paths_from_the_drive_root_in_order()
    {
        var paths = SeratoCrates.Read(Crate("Users/dj/Music/b.mp3", "Users/dj/Music/a.mp3"), @"C:\");

        Assert.Equal(new[] { @"C:\Users\dj\Music\b.mp3", @"C:\Users\dj\Music\a.mp3" }, paths);
    }

    [Fact]
    public void A_crate_cut_short_keeps_the_songs_before_the_cut()
    {
        var crate = Crate("Music/a.mp3", "Music/b.mp3");

        var paths = SeratoCrates.Read(crate.AsSpan(0, crate.Length - 3), @"E:\");

        Assert.Equal(new[] { @"E:\Music\a.mp3" }, paths);
    }

    [Fact]
    public void Crates_are_found_in_the_music_folder_and_at_drive_roots_named_by_nesting()
    {
        using var folder = new TempFolder();
        var music = folder["C", "Users", "dj", "Music"];
        Directory.CreateDirectory(Path.Combine(music, "_Serato_", "Subcrates"));
        File.WriteAllBytes(Path.Combine(music, "_Serato_", "Subcrates", "House%%Deep.crate"), Crate("Users/dj/Music/a.mp3"));
        File.WriteAllBytes(Path.Combine(music, "_Serato_", "Subcrates", "House.crate"), Crate());
        var drive = folder["E"] + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(drive, "_Serato_", "Subcrates"));
        File.WriteAllBytes(Path.Combine(drive, "_Serato_", "Subcrates", "Gig.crate"), Crate("Sets/b.mp3"));

        var folders = SeratoCrates.Folders(music, folder["C"] + Path.DirectorySeparatorChar, [drive, folder["Z"] + Path.DirectorySeparatorChar]);
        var crates = folders.SelectMany(found => SeratoCrates.Load(found.Folder, found.Root)).ToList();

        Assert.Equal(2, folders.Count);
        // A crate comes before the crates in it.
        Assert.Equal(new[] { "House", "Deep", "Gig" }, crates.Select(crate => crate.Name));
        Assert.Equal(new[] { "House" }, crates[1].Parents);
        Assert.Equal(new[] { Path.Combine(folder["C"], @"Users\dj\Music\a.mp3") }, crates[1].Paths);
        Assert.Equal(new[] { Path.Combine(drive, @"Sets\b.mp3") }, crates[2].Paths);
    }

    /// <summary>
    /// Serato Markers2's entries, encoded as Serato does: base64 with line breaks every 72 characters and no padding.
    /// </summary>
    static byte[] Markers2(params (int Number, uint Milliseconds)[] cues)
    {
        var entries = new List<byte> { 1, 1 };
        void Entry(string name, byte[] data)
        {
            entries.AddRange(Encoding.ASCII.GetBytes(name));
            entries.Add(0);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            entries.AddRange(length);
            entries.AddRange(data);
        }
        Entry("COLOR", [0, 0xff, 0xff, 0xff]);
        foreach (var (number, milliseconds) in cues)
        {
            var data = new byte[13];
            data[1] = (byte)number;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(2), milliseconds);
            data[7] = 0xcc;
            Entry("CUE", data);
        }
        Entry("BPMLOCK", [0]);
        entries.Add(0);

        var base64 = Convert.ToBase64String(entries.ToArray()).TrimEnd('=');
        var lines = string.Join('\n', base64.Chunk(72).Select(chunk => new string(chunk)));
        return [1, 1, .. Encoding.ASCII.GetBytes(lines), .. new byte[470]];
    }

    [Fact]
    public void Hot_cues_come_from_CUE_entries_by_number()
    {
        var cues = SeratoMarkers.FromMarkers2(Markers2((0, 1500), (5, 61_250)))!;

        Assert.Equal(TimeSpan.FromMilliseconds(1500), cues[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(61_250), cues[5]);
        Assert.Equal(2, cues.Count(cue => cue is not null));
    }

    [Fact]
    public void No_cues_means_none()
    {
        Assert.Null(SeratoMarkers.FromMarkers2(Markers2()));
        Assert.Null(SeratoMarkers.FromMarkers2([1, 1, .. "not base64!!"u8]));
    }

    /// <summary>
    /// An MP3 with an ID3v2 tag holding a title, and Serato's markers in a GEOB frame.
    /// </summary>
    static byte[] Mp3(int version, byte[] markers2)
    {
        byte[] Frame(string id, byte[] content)
        {
            var frame = new byte[10 + content.Length];
            Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
            if (version == 4)
                Syncsafe(content.Length).CopyTo(frame, 4);
            else
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), content.Length);
            content.CopyTo(frame, 10);
            return frame;
        }
        byte[] geob = [0, .. "application/octet-stream\0\0Serato Markers2\0"u8, .. markers2];
        byte[] frames = [.. Frame("TIT2", [0, .. "Song"u8]), .. Frame("GEOB", geob), .. new byte[64]];
        return [.. "ID3"u8, (byte)version, 0, 0, .. Syncsafe(frames.Length), .. frames, 0xff, 0xfb, 0x90, 0x00];
    }

    static byte[] Syncsafe(int size) => [(byte)(size >> 21 & 0x7f), (byte)(size >> 14 & 0x7f), (byte)(size >> 7 & 0x7f), (byte)(size & 0x7f)];

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void An_MP3s_Serato_hot_cues_are_read_from_its_ID3_tag(int version)
    {
        using var folder = new TempFolder();
        var path = folder["song.mp3"];
        File.WriteAllBytes(path, Mp3(version, Markers2((2, 30_000))));

        var cues = SeratoMarkers.ReadHotcues(path)!;

        Assert.Equal(TimeSpan.FromSeconds(30), cues[2]);
    }

    [Fact]
    public void A_file_without_Serato_markers_or_not_an_MP3_has_none()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder["plain.mp3"], [0xff, 0xfb, 0x90, 0x00, 1, 2, 3]);
        File.WriteAllBytes(folder["song.flac"], Mp3(4, Markers2((0, 1000))));

        Assert.Null(SeratoMarkers.ReadHotcues(folder["plain.mp3"]));
        Assert.Null(SeratoMarkers.ReadHotcues(folder["song.flac"]));
        Assert.Null(SeratoMarkers.ReadHotcues(folder["missing.mp3"]));
    }
}
