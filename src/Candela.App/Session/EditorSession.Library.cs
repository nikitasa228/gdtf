using Candela.App.Ui;
using Candela.Engine;

namespace Candela.App.Session;

/// <summary>
/// GDTF files the user imports (File → Import → GDTF Fixture, a <c>.gdtf</c> dropped onto the window, the Library
/// panel's import button) are copied into the user's fixture library folder before the engine reads them, so they are
/// still in the library at the next start and the original may be deleted (a file from the Downloads folder).
/// </summary>
internal sealed partial class EditorSession
{
    /// <summary>
    /// The folder imported GDTF files are copied into: the first library folder of the settings that exists; when the
    /// settings list none, the default <c>Documents\Candela\Fixture Library</c> of an installed copy (the caller creates
    /// it); null during development without a configured folder (the repository is the library and files stay where
    /// they are).
    /// </summary>
    internal static string? LibraryImportFolder(IReadOnlyList<string> configured, InstallLayout layout, out bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(layout);
        isDefault = false;
        foreach (var folder in configured)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            try
            {
                if (Directory.Exists(folder))
                    return Path.GetFullPath(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or System.Security.SecurityException)
            {
                // An unusable folder is skipped.
            }
        }

        if (configured.Any(f => !string.IsNullOrWhiteSpace(f)) || layout.RepositoryRoot is not null)
            return null;
        isDefault = true;
        return InstallLayout.DefaultUserLibraryFolder;
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="folder"/> unless it is already there; returns the file the
    /// library should read: the copy (<paramref name="created"/>), the identical file already in the folder, or null to
    /// read the original (it is in the folder already, or a different file of that name is — which is never
    /// overwritten). Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the copy fails.
    /// </summary>
    internal static string? CopyGdtfInto(string source, string folder, out bool created)
    {
        created = false;
        string full = Path.GetFullPath(source);
        string target = Path.Combine(Path.GetFullPath(folder), Path.GetFileName(full));
        if (string.Equals(Path.GetDirectoryName(full), Path.GetDirectoryName(target),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            return null;
        if (File.Exists(target))
            return SameContent(full, target) ? target : null;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Copied aside and renamed, so a library scan never sees half a file; an existing file is never replaced.
        string temp = target + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.Copy(full, temp, overwrite: true);
            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        created = true;
        return target;
    }

    /// <summary>
    /// The copy step of a GDTF import (see the class remarks): the file to import instead of <paramref name="path"/>, or
    /// null to import the original (no library folder during development, a read-only run, a failed copy — reported as a
    /// note, and the original is used for this session).
    /// </summary>
    private string? CopyIntoLibraryFolder(string path, out bool created)
    {
        created = false;
        if (_settings.ReadOnly || Engine is not { } engine)
            return null;
        string? folder = LibraryImportFolder(_settings.Settings.LibraryFolders, Layout, out bool isDefault);
        if (folder is null)
            return null;
        try
        {
            if (isDefault)
            {
                // The default folder becomes the library folder - in the settings and in the running engine - before the
                // file arrives, so the engine still reads and checks the file itself when it is imported.
                Directory.CreateDirectory(folder);
                _settings.Settings.LibraryFolders.Insert(0, folder);
                _settings.SaveNow();
                engine.AddLibraryFolder(folder);
            }

            string? copy = CopyGdtfInto(path, folder, out created);
            if (created)
                _log.Info("Library", InstallLoc.Format(InstallStr.ImportCopied, Path.GetFileName(path), folder));
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            string message = InstallLoc.Format(InstallStr.ImportCopyFailed, Path.GetFileName(path), e.Message);
            _log.Warn("Library", message);
            ReportProgramNote("app.gdtf-copy-failed", message);
            return null;
        }
    }

    private void DeleteCopy(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Library", $"The refused file '{path}' could not be removed from the library folder: {e.Message}");
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var x = new FileInfo(a);
            var y = new FileInfo(b);
            if (x.Length != y.Length)
                return false;
            using var sa = x.OpenRead();
            using var sb = y.OpenRead();
            Span<byte> ba = stackalloc byte[16 * 1024];
            Span<byte> bb = stackalloc byte[16 * 1024];
            while (true)
            {
                int na = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                int nb = sb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb || !ba[..na].SequenceEqual(bb[..nb]))
                    return false;
                if (na == 0)
                    return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
