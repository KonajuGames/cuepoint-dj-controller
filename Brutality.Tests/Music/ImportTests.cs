using System.Buffers.Binary;
using System.Text;
using Brutality.Audio.Analysis;
using Brutality.Music;
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
              <POSITION_MARK Name="" Type="4" Start="32.5" End="36.25" Num="-1"/>
            </TRACK>
            <TRACK TrackID="12" Name="Two" TotalTime="240" Location="file://localhost/D:/Sets/Two.flac">
              <TEMPO Inizio="1.5" Bpm="100.00" Metro="4/4" Battito="3"/>
              <TEMPO Inizio="61.5" Bpm="102.50" Metro="4/4" Battito="3"/>
              <TEMPO Inizio="61.5" Bpm="104.00" Metro="4/4" Battito="3"/>
              <TEMPO Inizio="bad" Bpm="104.00" Metro="4/4" Battito="1"/>
            </TRACK>
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
    public void Memory_cues_and_memory_loops_come_in_order_as_memory_cues()
    {
        using var folder = new TempFolder();
        var collection = Load(folder);

        Assert.Equal(new[] { TimeSpan.FromSeconds(0.025), TimeSpan.FromSeconds(32.5) }, collection.MemoryCues[@"C:\Music\A Song.mp3"]);
        Assert.Single(collection.MemoryCues);
    }

    [Fact]
    public void A_steady_beat_grid_comes_from_its_one_tempo()
    {
        using var folder = new TempFolder();
        var grid = Load(folder).Grids[@"C:\Music\A Song.mp3"];

        Assert.Equal(new[] { new GridMarker(0.025, 128) }, grid.Markers);
        // Battito 1: the marker's beat starts a bar.
        Assert.True(grid.IsBarStart(0));
    }

    [Fact]
    public void A_changing_beat_grid_keeps_its_tempos_in_order_and_where_its_bars_start()
    {
        using var folder = new TempFolder();
        var grid = Load(folder).Grids[@"D:\Sets\Two.flac"];

        // A second tempo at the same moment takes the first's place; one that won't read is left out.
        Assert.Equal(new[] { new GridMarker(1.5, 100), new GridMarker(61.5, 104) }, grid.Markers);
        Assert.Equal(240, grid.Duration);
        // Battito 3: the first marker's beat is the third of its bar, so the bar starts two beats later.
        Assert.Equal(2, grid.BarPosition(0));
        Assert.True(grid.IsBarStart(2));
    }

    [Fact]
    public void Its_songs_are_those_with_hot_cues_memory_cues_or_a_beat_grid()
    {
        using var folder = new TempFolder();

        Assert.Equal(new[] { @"C:\Music\A Song.mp3", @"D:\Sets\Two.flac" }, Load(folder).Songs.Order());
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
    public void Songs_chosen_get_Serato_s_hot_cues_unless_they_have_their_own_here()
    {
        using var folder = new TempFolder();
        var withCues = folder["a.mp3"];
        var ownCues = folder["b.mp3"];
        var noCues = folder["c.mp3"];
        File.WriteAllBytes(withCues, Mp3(4, Markers2((0, 1500))));
        File.WriteAllBytes(ownCues, Mp3(3, Markers2((2, 9000))));
        File.WriteAllBytes(noCues, Mp3(4, Markers2()));
        var database = LibraryDatabase.Load(folder["library.json"], folder.Path, _ => folder.Path);
        database.SetHotcues(ownCues, [TimeSpan.FromSeconds(4)]);

        var found = SeratoCueImport.Read([withCues, ownCues, noCues, folder["missing.mp3"]]);
        Assert.Equal(new[] { withCues, ownCues }, found.Select(read => read.Path));

        Assert.Equal((1, 1), SeratoCueImport.Take(database, found));
        Assert.Equal(TimeSpan.FromSeconds(1.5), database.GetHotcues(withCues)[0]);
        Assert.Equal(TimeSpan.FromSeconds(4), database.GetHotcues(ownCues)[0]);
        Assert.Null(database.GetHotcues(ownCues).ElementAtOrDefault(2));
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

    /// <summary>
    /// Serato's markers as an M4A or FLAC keeps them: the header, then the markers as an MP3 keeps them, all in base64
    /// again, without padding, a line break every 72 characters.
    /// </summary>
    static byte[] Wrapped(byte[] markers2)
    {
        byte[] unwrapped = [.. "application/octet-stream\0\0Serato Markers2\0"u8, .. markers2];
        var base64 = Convert.ToBase64String(unwrapped).TrimEnd('=');
        return Encoding.ASCII.GetBytes(string.Join('\n', base64.Chunk(72).Select(chunk => new string(chunk))));
    }

    static byte[] Box(string type, params byte[][] contents)
    {
        var body = contents.SelectMany(part => part).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(box, box.Length);
        // Latin-1, for the one byte types like "©nam" start with.
        Encoding.Latin1.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    /// <summary>
    /// An M4A's metadata with a title and, if given, Serato's markers, as iTunes-style tags: ilst › "----" › mean, name
    /// and data.
    /// </summary>
    static byte[] Ilst(byte[]? wrapped)
    {
        byte[] Freeform(string mean, string name, byte[] value) =>
            Box("----", Box("mean", [0, 0, 0, 0], Encoding.UTF8.GetBytes(mean)), Box("name", [0, 0, 0, 0], Encoding.UTF8.GetBytes(name)), Box("data", [0, 0, 0, 1, 0, 0, 0, 0], value));
        // iTunes' title tag, "©nam", which Serato's is found among.
        var title = Box("©nam", Box("data", [0, 0, 0, 1, 0, 0, 0, 0], "Song"u8.ToArray()));
        var other = Freeform("com.apple.iTunes", "iTunNORM", "0000"u8.ToArray());
        return wrapped is null ? Box("ilst", title, other) : Box("ilst", title, other, Freeform("com.serato.dj", "markersv2", wrapped));
    }

    // A "meta" box: four bytes of version and flags, a handler, then the tags.
    static byte[] Meta(byte[] ilst) => Box("meta", [0, 0, 0, 0], Box("hdlr", new byte[25]), ilst);

    static byte[] M4a(byte[] moov, bool moovFirst)
    {
        var ftyp = Box("ftyp", "M4A \0\0\0\0M4A mp42isom"u8.ToArray());
        var mdat = Box("mdat", new byte[3000]);
        return moovFirst ? [.. ftyp, .. moov, .. mdat] : [.. ftyp, .. mdat, .. moov];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_M4As_Serato_hot_cues_are_read_from_its_freeform_tag_wherever_its_metadata_is(bool moovFirst)
    {
        using var folder = new TempFolder();
        var moov = Box("moov", Box("mvhd", new byte[100]), Box("udta", Meta(Ilst(Wrapped(Markers2((1, 4_000), (7, 200_500)))))));
        File.WriteAllBytes(folder["song.m4a"], M4a(moov, moovFirst));

        var cues = SeratoMarkers.ReadHotcues(folder["song.m4a"])!;

        Assert.Equal(TimeSpan.FromSeconds(4), cues[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(200_500), cues[7]);
    }

    [Fact]
    public void An_M4A_with_its_metadata_straight_in_moov_is_read_too()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder["song.m4a"], M4a(Box("moov", Meta(Ilst(Wrapped(Markers2((0, 750)))))), moovFirst: true));

        Assert.Equal(TimeSpan.FromMilliseconds(750), SeratoMarkers.ReadHotcues(folder["song.m4a"])![0]);
    }

    [Fact]
    public void An_M4A_without_Seratos_tag_has_none()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder["song.m4a"], M4a(Box("moov", Box("udta", Meta(Ilst(null)))), moovFirst: true));
        File.WriteAllBytes(folder["broken.m4a"], [0, 0, 0, 99, .. "ftyp"u8, 1, 2]);

        Assert.Null(SeratoMarkers.ReadHotcues(folder["song.m4a"]));
        Assert.Null(SeratoMarkers.ReadHotcues(folder["broken.m4a"]));
    }

    /// <summary>
    /// A FLAC's start: its stream info, then a Vorbis comment with a title and the comments given.
    /// </summary>
    static byte[] Flac(params string[] comments)
    {
        byte[] Block(int type, bool last, byte[] data) => [(byte)(type | (last ? 0x80 : 0)), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. data];
        byte[] Field(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            return [.. length, .. bytes];
        }
        var all = comments.Prepend("TITLE=Song").ToArray();
        var count = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, all.Length);
        byte[] vorbis = [.. Field("reference libFLAC 1.4.3"), .. count, .. all.SelectMany(Field)];
        return [.. "fLaC"u8, .. Block(0, false, new byte[34]), .. Block(1, false, new byte[100]), .. Block(4, true, vorbis), 0xff, 0xf8];
    }

    [Fact]
    public void A_FLACs_Serato_hot_cues_are_read_from_its_Vorbis_comment()
    {
        using var folder = new TempFolder();
        var markers = Encoding.ASCII.GetString(Wrapped(Markers2((3, 90_000))));
        File.WriteAllBytes(folder["song.flac"], Flac("ARTIST=Someone", "serato_markers_v2=" + markers));

        Assert.Equal(TimeSpan.FromSeconds(90), SeratoMarkers.ReadHotcues(folder["song.flac"])![3]);
    }

    [Fact]
    public void A_FLAC_with_an_ID3_tag_first_is_read_past_it()
    {
        using var folder = new TempFolder();
        var markers = Encoding.ASCII.GetString(Wrapped(Markers2((0, 5_000))));
        byte[] id3 = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 20, .. new byte[20]];
        File.WriteAllBytes(folder["song.flac"], [.. id3, .. Flac("SERATO_MARKERS_V2=" + markers)]);

        Assert.Equal(TimeSpan.FromSeconds(5), SeratoMarkers.ReadHotcues(folder["song.flac"])![0]);
    }

    [Fact]
    public void A_FLAC_without_Seratos_comment_has_none()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder["song.flac"], Flac("ARTIST=Someone"));
        // Wrapped wrongly: base64 of the markers, but without the header.
        File.WriteAllBytes(folder["odd.flac"], Flac("SERATO_MARKERS_V2=" + Convert.ToBase64String(Markers2((0, 1000)))));

        Assert.Null(SeratoMarkers.ReadHotcues(folder["song.flac"]));
        Assert.Null(SeratoMarkers.ReadHotcues(folder["odd.flac"]));
    }
}
