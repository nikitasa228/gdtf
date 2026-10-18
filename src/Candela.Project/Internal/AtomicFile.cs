namespace Candela.Project.Internal;

/// <summary>
/// Writes a file so that readers only ever see the old or the complete new content: the data goes to a temporary file
/// in the same folder (same volume, so the final rename is atomic), is flushed to disk, optionally verified, and then
/// replaces the target. If anything fails before the rename, the target is untouched and the temporary file is deleted.
/// </summary>
internal static class AtomicFile
{
    private const int BufferSize = 1 << 16;

    /// <summary>Writes <paramref name="path"/> through <paramref name="write"/>; returns the size of the written file.</summary>
    /// <param name="path">Target file; its folder is created when missing.</param>
    /// <param name="write">Writes the complete content to the (seekable) stream.</param>
    /// <param name="verify">Optional check of the finished temporary file (throw to abort).</param>
    public static long Write(string path, Action<Stream> write, Action<string>? verify = null)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full) ?? throw new ArgumentException($"'{path}' has no folder.", nameof(path));
        Directory.CreateDirectory(folder);
        var temp = TempPathFor(full);
        try
        {
            long length;
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
                length = stream.Length;
            }
            verify?.Invoke(temp);
            Replace(temp, full);
            return length;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>A hidden temporary name next to <paramref name="fullPath"/>, unique per call.</summary>
    public static string TempPathFor(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath)!;
        return Path.Combine(folder, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp");
    }

    /// <summary>
    /// Moves <paramref name="source"/> over <paramref name="target"/> (atomic rename on the same volume). Sharing
    /// violations — a virus scanner or indexer briefly holding the target on Windows — are retried for about a second.
    /// </summary>
    public static void Replace(string source, string target)
    {
        const int attempts = 6;
        for (int i = 1; ; i++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception e) when (i < attempts && (e is IOException or UnauthorizedAccessException) && File.Exists(source))
            {
                Thread.Sleep(20 << i);
            }
        }
    }

    /// <summary>Deletes a file, ignoring every failure (cleanup must never mask the original error).</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; it is hidden and named so that it is recognisable.
        }
    }
}
