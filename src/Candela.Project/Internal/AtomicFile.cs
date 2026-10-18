namespace Candela.Project.Internal;

/// <summary>
/// Writes a file so that readers only ever see the old or the complete new content: the data goes to a temporary file
/// in the same folder (same volume, so the final rename is atomic), is flushed to disk, optionally verified, and then
/// replaces the target. If anything fails before the rename, the target is untouched and the temporary file is deleted.
/// <list type="bullet">
/// <item>A target that is a symbolic link is written through the link (the file it points to is replaced, the link
/// stays), so a project kept in a synchronised folder and linked from elsewhere is really updated.</item>
/// <item>A read-only target is refused (<see cref="UnauthorizedAccessException"/>) instead of being renamed over: on Unix
/// a rename ignores the file's own permissions, on Windows it fails anyway.</item>
/// <item>The target's Unix permission bits are kept, so saving never makes a private file readable by others.</item>
/// <item>The temporary name is shortened when needed, so every name the file system accepts can be saved.</item>
/// </list>
/// </summary>
internal static class AtomicFile
{
    private const int BufferSize = 1 << 16;

    /// <summary>Longest file name in bytes (UTF-8 on Unix; Windows counts 255 UTF-16 units, never more than the UTF-8 bytes).</summary>
    private const int MaxFileNameBytes = 255;

    private const UnixFileMode PermissionBits = (UnixFileMode)0x1FF; // rwxrwxrwx

    /// <summary>Writes <paramref name="path"/> through <paramref name="write"/>; returns the size of the written file.</summary>
    /// <param name="path">Target file; its folder is created when missing.</param>
    /// <param name="write">Writes the complete content to the (seekable) stream.</param>
    /// <param name="verify">Optional check of the finished temporary file (throw to abort).</param>
    /// <param name="beforeReplace">
    /// Optional step run after the verification, just before the temporary file replaces the target (autosave rotates
    /// older copies here). Throwing aborts the write; the target is then untouched.
    /// </param>
    /// <exception cref="UnauthorizedAccessException">The target is read-only (or the folder cannot be written).</exception>
    public static long Write(string path, Action<Stream> write, Action<string>? verify = null, Action? beforeReplace = null)
    {
        var full = ResolveLinks(Path.GetFullPath(path));
        var folder = Path.GetDirectoryName(full) ?? throw new ArgumentException($"'{path}' has no folder.", nameof(path));
        Directory.CreateDirectory(folder);
        UnixFileMode? mode = null;
        if (File.Exists(full))
        {
            if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException($"'{full}' is read-only; it was not overwritten.");
            if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(full) & PermissionBits;
        }
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
            beforeReplace?.Invoke();
            if (mode is { } m && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, m);
            Replace(temp, full);
            return length;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// A hidden temporary name next to <paramref name="fullPath"/>, unique per call: <c>.{name}.{random}.tmp</c>, with the
    /// copied name shortened so the whole name stays within the file system's limit.
    /// </summary>
    public static string TempPathFor(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath)!;
        var suffix = "." + Guid.NewGuid().ToString("N")[..12] + ".tmp";
        var name = Truncate(Path.GetFileName(fullPath), MaxFileNameBytes - 1 - suffix.Length);
        return Path.Combine(folder, "." + name + suffix);
    }

    /// <summary>The final target of a symbolic link (or chain of links); the path itself when it is not a link.</summary>
    private static string ResolveLinks(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.LinkTarget is null) return fullPath;
            return info.ResolveLinkTarget(returnFinalTarget: true) is { } target ? Path.GetFullPath(target.FullName) : fullPath;
        }
        catch (IOException)
        {
            // A link cycle or an unreadable link: write the path as given.
            return fullPath;
        }
    }

    /// <summary>The longest prefix of <paramref name="name"/> whose UTF-8 form fits in <paramref name="maxBytes"/> (never splits a surrogate pair).</summary>
    private static string Truncate(string name, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(name) <= maxBytes) return name;
        int bytes = 0;
        int i = 0;
        while (i < name.Length)
        {
            int step = char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]) ? 2 : 1;
            int size = System.Text.Encoding.UTF8.GetByteCount(name.AsSpan(i, step));
            if (bytes + size > maxBytes) break;
            bytes += size;
            i += step;
        }
        return name[..i];
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
