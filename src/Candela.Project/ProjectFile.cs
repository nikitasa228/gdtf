using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Candela.Core.Serialization;
using Candela.Project.Internal;

namespace Candela.Project;

/// <summary>
/// Reads and writes <c>.candela</c> project archives (see <c>docs/project-format.md</c>): a zip with
/// <c>manifest.json</c>, <c>scene.json</c>, <c>settings.json</c>, <c>assets/</c> (original files of imported models and
/// images), <c>fixtures/</c> (copies of every GDTF file the scene uses) and an optional <c>thumbnail.png</c>.
/// <list type="bullet">
/// <item><b>Save</b> is atomic: the archive is written to a hidden temporary file in the target folder, flushed, checked,
/// and only then moved over the target. When anything fails (including the GDTF provider throwing) the previous file is
/// left exactly as it was.</item>
/// <item><b>Load</b> is tolerant: a damaged asset, GDTF file, entity or component becomes a <see cref="ProjectData.LoadIssues"/>
/// entry and the rest of the project loads. Only an unreadable archive, a missing or unreadable <c>scene.json</c>, or an
/// unsupported format version throw (<see cref="ProjectFormatException"/>, <see cref="ProjectVersionException"/>).</item>
/// <item><b>Versions</b>: files of older format versions are converted by the <see cref="ProjectMigrationRegistry"/>;
/// files of newer versions are refused with a message that names the version and the program that wrote them.</item>
/// </list>
/// All methods are thread-safe in the sense that they share no state; a <see cref="ProjectData"/> passed to
/// <see cref="Save(string, ProjectData, ProjectSaveOptions?)"/> must not be modified concurrently (save from a background
/// thread through <see cref="ProjectSnapshot"/>).
/// </summary>
public static class ProjectFile
{
    /// <summary>Value of the manifest's <c>format</c> field.</summary>
    public const string FormatId = "candela-project";

    /// <summary>Format version written by this program and the newest one it reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>File extension of project files, including the dot.</summary>
    public const string Extension = ".candela";

    /// <summary>Application version written to new manifests when <see cref="ProjectSaveOptions.AppVersion"/> is not set.</summary>
    public static string DefaultAppVersion { get; } = ReadAppVersion();

    /// <summary>
    /// Saves <paramref name="project"/> to <paramref name="path"/> atomically and, on success, sets
    /// <see cref="ProjectData.FilePath"/> and <see cref="ProjectData.Manifest"/>. Call on the main thread (the scene is
    /// serialised directly); for background saving capture a <see cref="ProjectSnapshot"/> and use
    /// <see cref="Save(string, ProjectSnapshot)"/>.
    /// </summary>
    /// <returns>What was written and the problems the user should know about (GDTF files that could not be stored…).</returns>
    /// <exception cref="IOException">The file could not be written; the previous file is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission to write the folder; the previous file is unchanged.</exception>
    public static ProjectSaveResult Save(string path, ProjectData project, ProjectSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var snapshot = ProjectSnapshot.Capture(project, options);
        var result = Save(path, snapshot);
        project.FilePath = result.Path;
        project.Manifest = result.Manifest;
        if (project.CreatedUtc == default) project.CreatedUtc = result.Manifest.CreatedUtc;
        return result;
    }

    /// <summary>
    /// Writes a snapshot to <paramref name="path"/> atomically. May run on any thread; calls the snapshot's GDTF provider
    /// on this thread. The project the snapshot came from is not modified.
    /// </summary>
    public static ProjectSaveResult Save(string path, ProjectSnapshot snapshot) => Save(path, snapshot, null);

    /// <summary>Writes the archive of <paramref name="project"/> to a stream (not atomic; the stream is left open).</summary>
    public static ProjectSaveResult Save(Stream destination, ProjectData project, ProjectSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Save(destination, ProjectSnapshot.Capture(project, options));
    }

    /// <summary>Writes a snapshot to a stream (not atomic; the stream is left open). May run on any thread.</summary>
    public static ProjectSaveResult Save(Stream destination, ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!destination.CanWrite) throw new ArgumentException("The stream is not writable.", nameof(destination));
        return ProjectWriter.Write(snapshot, destination, null);
    }

    internal static ProjectSaveResult Save(string path, ProjectSnapshot snapshot, CompressionLevel? compressionOverride,
        Action? beforeReplace = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        var full = Path.GetFullPath(path);
        ProjectSaveResult? result = null;
        AtomicFile.Write(full,
            stream => result = ProjectWriter.Write(snapshot, stream, full, compressionOverride),
            snapshot.Options.Verify ? VerifyWritten : null,
            beforeReplace);
        return result!;
    }

    /// <summary>
    /// Opens a project. May run on a background thread (it builds a new, unshared <see cref="ProjectData"/>). Problems
    /// with single elements are listed in <see cref="ProjectData.LoadIssues"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ProjectFormatException">Not a readable Candela project (the message is written for the user).</exception>
    /// <exception cref="ProjectVersionException">Written in a format version this program cannot read.</exception>
    public static ProjectData Load(string path, ProjectLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        using var stream = OpenRead(full);
        var project = Load(stream, options);
        project.FilePath = full;
        return project;
    }

    /// <summary>Reads a project from a seekable stream (left open). See <see cref="Load(string, ProjectLoadOptions?)"/>.</summary>
    public static ProjectData Load(Stream source, ProjectLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("The stream must be readable and seekable.", nameof(source));
        return ProjectReader.Read(source, options ?? ProjectLoadOptions.Default);
    }

    /// <summary>
    /// Reads only <c>manifest.json</c> (scene name, counts, times, versions) for Open dialogs and recent-file lists. Does
    /// not check the format version, so a newer project can still be described.
    /// </summary>
    /// <exception cref="ProjectFormatException">Not a Candela project, or the manifest is missing or damaged.</exception>
    public static ProjectManifest ReadManifest(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = OpenRead(Path.GetFullPath(path));
        return ProjectReader.ReadManifestOnly(stream);
    }

    /// <summary>The <c>thumbnail.png</c> of a project (for Open dialogs), or null when it has none or it is not a PNG.</summary>
    /// <exception cref="ProjectFormatException">The file is not a zip archive.</exception>
    public static byte[]? ReadThumbnail(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = OpenRead(Path.GetFullPath(path));
        return ProjectReader.ReadThumbnailOnly(stream);
    }

    /// <summary>True when the file starts like a zip archive and its directory holds a <c>manifest.json</c> naming this format.</summary>
    public static bool IsProjectFile(string path)
    {
        try
        {
            return ReadManifest(path).Format == FormatId;
        }
        catch (Exception e) when (e is ProjectFormatException or IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return false;
        }
    }

    private static FileStream OpenRead(string fullPath)
    {
        try
        {
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        }
        catch (DirectoryNotFoundException e)
        {
            throw new FileNotFoundException($"The project '{fullPath}' does not exist.", fullPath, e);
        }
    }

    /// <summary>Re-reads the written temporary file: a valid zip whose manifest names this format and that holds a scene.</summary>
    private static void VerifyWritten(string tempPath)
    {
        using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifest = zip.GetEntry(ArchivePaths.Manifest) ?? throw new IOException("The written project has no manifest.");
        if (zip.GetEntry(ArchivePaths.Scene) is null) throw new IOException("The written project has no scene.");
        using var m = manifest.Open();
        var parsed = JsonSerializer.Deserialize<ProjectManifest>(m, CandelaJson.Options);
        if (parsed?.Format != FormatId) throw new IOException("The written project manifest is damaged.");
    }

    private static string ReadAppVersion()
    {
        var attr = typeof(ProjectFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(attr)) return typeof(ProjectFile).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        int plus = attr.IndexOf('+');
        return plus > 0 ? attr[..plus] : attr;
    }
}
