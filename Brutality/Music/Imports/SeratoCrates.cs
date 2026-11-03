using System.Buffers.Binary;
using System.Text;

namespace Brutality.Music.Imports;

/// <summary>
/// A Serato crate: its name, the names of the crates it's in (outermost first), and its songs' files in order.
/// </summary>
sealed record SeratoCrate(string Name, IReadOnlyList<string> Parents, IReadOnlyList<string> Paths);

/// <summary>
/// Reads Serato's crates: the .crate files in a _Serato_\Subcrates folder, in the Music folder for songs on the system
/// drive and at the root of any other drive for its songs. A crate inside another is named "Outer%%Inner". A crate
/// file is a run of tags, each four letters then a four-byte length, big-endian, and its value; each song is an
/// "otrk" tag holding a "ptrk" tag, its path in UTF-16, big-endian, from the root of the drive the _Serato_ folder's
/// for, with forward slashes and no drive letter.
/// </summary>
static class SeratoCrates
{
    const string NESTING = "%%";

    /// <summary>
    /// The _Serato_ folders there are: <paramref name="music"/>'s, for the system drive, and at the root of each of
    /// <paramref name="driveRoots"/>; each with the root its songs' paths are from.
    /// </summary>
    public static IReadOnlyList<(string Folder, string Root)> Folders(string music, string systemRoot, IEnumerable<string> driveRoots)
    {
        var folders = new List<(string, string)>();
        void Add(string folder, string root)
        {
            if (Directory.Exists(Path.Combine(folder, "Subcrates")) && !folders.Any(known => string.Equals(known.Item1, folder, StringComparison.OrdinalIgnoreCase)))
                folders.Add((folder, root));
        }
        Add(Path.Combine(music, "_Serato_"), systemRoot);
        foreach (var root in driveRoots)
            Add(Path.Combine(root, "_Serato_"), root);
        return folders;
    }

    /// <summary>
    /// Every crate in <paramref name="folder"/>'s Subcrates, its songs' paths from <paramref name="root"/>, by name.
    /// Crates that can't be read are left out.
    /// </summary>
    public static IReadOnlyList<SeratoCrate> Load(string folder, string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(Path.Combine(folder, "Subcrates"), "*.crate").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var crates = new List<SeratoCrate>();
        foreach (var file in files.Order(StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var names = Path.GetFileNameWithoutExtension(file).Split(NESTING);
                crates.Add(new SeratoCrate(names[^1], names[..^1], Read(File.ReadAllBytes(file), root)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return crates;
    }

    /// <summary>
    /// The songs' paths in a crate's bytes, from <paramref name="root"/>.
    /// </summary>
    internal static IReadOnlyList<string> Read(ReadOnlySpan<byte> crate, string root)
    {
        var paths = new List<string>();
        foreach (var (tag, value) in Tags(crate))
        {
            if (tag != "otrk")
                continue;
            foreach (var (inner, path) in Tags(value))
            {
                if (inner != "ptrk")
                    continue;
                var text = Encoding.BigEndianUnicode.GetString(path).TrimEnd('\0').Replace('/', Path.DirectorySeparatorChar);
                if (text.Length > 0)
                    paths.Add(Path.IsPathRooted(text) && text.Length > 1 && text[1] == ':' ? text : Path.Combine(root, text.TrimStart(Path.DirectorySeparatorChar)));
            }
        }
        return paths;
    }

    /// <summary>
    /// The tags in <paramref name="bytes"/>, each its name and value, up to the first that runs past the end.
    /// </summary>
    static List<(string Tag, byte[] Value)> Tags(ReadOnlySpan<byte> bytes)
    {
        var tags = new List<(string, byte[])>();
        var at = 0;
        while (at + 8 <= bytes.Length)
        {
            var tag = Encoding.ASCII.GetString(bytes.Slice(at, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(at + 4, 4));
            if (length < 0 || at + 8 + length > bytes.Length)
                break;
            tags.Add((tag, bytes.Slice(at + 8, length).ToArray()));
            at += 8 + length;
        }
        return tags;
    }
}
