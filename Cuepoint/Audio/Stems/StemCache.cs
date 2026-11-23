using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Cuepoint.Audio.Stems;

/// <summary>
/// Songs' stems kept on disk once they've been separated, so a song only has to be separated once. Each is a file
/// named for the song's path, size and time last written, so an edited file is separated again. The samples are kept
/// as 16-bit floats, which is plenty for a stem and half the size.
/// </summary>
sealed class StemCache(string folder)
{
    const uint MAGIC = 0x4D455453; // "STEM"
    const int VERSION = 1;

    public string Folder { get; } = folder;

    /// <summary>
    /// The stems kept for the song at <paramref name="songPath"/>; null if there are none, or they're unreadable.
    /// </summary>
    public SongStems? Load(string songPath)
    {
        if (FileFor(songPath) is not { } path || !File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != MAGIC || reader.ReadInt32() != VERSION)
                return null;
            var channels = reader.ReadInt32();
            var rate = reader.ReadInt32();
            var samples = reader.ReadInt64();
            if (channels <= 0 || rate <= 0 || samples <= 0 || samples > Array.MaxLength || stream.Length != stream.Position + 3 * samples * 2)
                return null;
            var stems = new float[3][];
            var halves = new Half[samples];
            for (var s = 0; s < 3; s++)
            {
                stream.ReadExactly(MemoryMarshal.AsBytes(halves.AsSpan()));
                var floats = new float[samples];
                for (var i = 0; i < floats.Length; i++)
                    floats[i] = (float)halves[i];
                stems[s] = floats;
            }
            return new SongStems(stems[0], stems[1], stems[2], channels, rate);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether there are stems kept for the song at <paramref name="songPath"/>, without reading them.
    /// </summary>
    public bool Has(string songPath) => FileFor(songPath) is { } path && File.Exists(path);

    /// <summary>
    /// How many bytes the stems kept for the song at <paramref name="songPath"/> take; null if there are none.
    /// </summary>
    public long? SizeOf(string songPath) => FileFor(songPath) is { } path && new FileInfo(path) is { Exists: true } file ? file.Length : null;

    /// <summary>
    /// Deletes the stems kept for the song at <paramref name="songPath"/>, if there are any. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if they can't be.
    /// </summary>
    public void Delete(string songPath)
    {
        if (FileFor(songPath) is { } path)
            File.Delete(path);
    }

    /// <summary>
    /// Keeps the stems that were in <paramref name="oldFile"/> (from <see cref="FileFor"/>) for the song at
    /// <paramref name="songPath"/> as its file is now, after its tags were written, say: the samples haven't changed,
    /// only the file's size and time. Returns whether there were any to keep. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if they can't be moved.
    /// </summary>
    public bool Follow(string? oldFile, string songPath)
    {
        if (oldFile is null || !File.Exists(oldFile) || FileFor(songPath) is not { } path || string.Equals(oldFile, path, StringComparison.OrdinalIgnoreCase))
            return false;
        File.Move(oldFile, path, overwrite: true);
        return true;
    }

    public void Save(string songPath, SongStems stems)
    {
        if (FileFor(songPath) is not { } path)
            return;
        Directory.CreateDirectory(Folder);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(MAGIC);
            writer.Write(VERSION);
            writer.Write(stems.Channels);
            writer.Write(stems.SampleRate);
            writer.Write((long)stems.Vocals.Length);
            var halves = new Half[stems.Vocals.Length];
            foreach (var stem in new[] { stems.Vocals, stems.Drums, stems.Bass })
            {
                for (var i = 0; i < halves.Length; i++)
                    halves[i] = (Half)stem[i];
                writer.Write(MemoryMarshal.AsBytes(halves.AsSpan()));
            }
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The file the stems for the song at <paramref name="songPath"/> are kept in, whether or not they've been made;
    /// null if the song's file isn't there.
    /// </summary>
    public string? FileFor(string songPath)
    {
        var file = new FileInfo(songPath);
        if (!file.Exists)
            return null;
        var key = $"{Path.GetFullPath(songPath).ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(Folder, hash + ".stems");
    }
}
