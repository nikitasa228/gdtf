namespace Candela.Project;

/// <summary>
/// The file is not a readable Candela project: not a zip archive, no manifest or scene, a damaged <c>scene.json</c>, or a
/// format this program cannot read. The message is written for the user. Problems with single elements (an asset, a
/// GDTF file, one entity) never throw; they are reported as load issues instead.
/// </summary>
public class ProjectFormatException : Exception
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public ProjectFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a user-facing message and the underlying cause.</summary>
    public ProjectFormatException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The project was written in a format version this program cannot read: a newer version (written by a newer Candela)
/// or an older version for which no migration is registered.
/// </summary>
public sealed class ProjectVersionException : ProjectFormatException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing message.</param>
    /// <param name="fileFormatVersion">Format version stored in the file.</param>
    /// <param name="supportedFormatVersion">Newest format version this program reads (<see cref="ProjectFile.FormatVersion"/>).</param>
    /// <param name="writtenBy">Application version that wrote the file, when known.</param>
    public ProjectVersionException(string message, int fileFormatVersion, int supportedFormatVersion, string? writtenBy = null)
        : base(message)
    {
        FileFormatVersion = fileFormatVersion;
        SupportedFormatVersion = supportedFormatVersion;
        WrittenBy = writtenBy;
    }

    /// <summary>Format version stored in the file.</summary>
    public int FileFormatVersion { get; }

    /// <summary>Newest format version this program reads.</summary>
    public int SupportedFormatVersion { get; }

    /// <summary>Application version that wrote the file (manifest <c>appVersion</c>), when known.</summary>
    public string? WrittenBy { get; }

    /// <summary>True when the file is newer than this program (an update is needed to open it).</summary>
    public bool IsNewer => FileFormatVersion > SupportedFormatVersion;
}
