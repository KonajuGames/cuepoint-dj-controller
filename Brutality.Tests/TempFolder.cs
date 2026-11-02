namespace Brutality.Tests;

/// <summary>
/// A folder of its own under the system's temporary folder for a test to write in, deleted with everything in it
/// when the test's done. Stands in for drives, too, where code takes the drive's root as a path.
/// </summary>
sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cuepoint.Tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    /// <summary>
    /// The full path of <paramref name="parts"/> inside the folder.
    /// </summary>
    public string this[params string[] parts] => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>
    /// Makes a small file at <paramref name="parts"/> inside the folder, and the folders on the way, returning its
    /// full path. Its contents don't matter: nothing here decodes it.
    /// </summary>
    public string File(params string[] parts)
    {
        var path = this[parts];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file still open somewhere; the system clears its temporary folder in time.
        }
    }
}
