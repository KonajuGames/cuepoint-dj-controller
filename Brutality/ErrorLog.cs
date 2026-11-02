namespace Brutality;

/// <summary>
/// Where the app writes down what went wrong, with when and what it was doing, so it can be looked into after a set:
/// errors.log, beside the settings. Kept to its last <see cref="MAX_BYTES"/> or so, so it can't fill the disk. Safe to
/// use from any thread; it never throws.
/// </summary>
static class ErrorLog
{
    /// <summary>
    /// How big the log gets before it's started afresh, with its previous contents kept as errors.old.log.
    /// </summary>
    const long MAX_BYTES = 1 << 20;

    static readonly object Lock = new();

    /// <summary>
    /// The file written to; nothing's written until it's set.
    /// </summary>
    public static string? FilePath { get; set; }

    /// <summary>
    /// Writes down <paramref name="exception"/>, what the app was doing when it happened, and when.
    /// </summary>
    public static void Write(string doing, Exception exception)
    {
        if (FilePath is not { } path)
            return;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MAX_BYTES)
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  While {doing}:{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it; the app carries on regardless.
        }
    }
}
