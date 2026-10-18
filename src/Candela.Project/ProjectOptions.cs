using System.IO.Compression;
using Candela.Assets;
using Candela.Core.Diagnostics;

namespace Candela.Project;

/// <summary>
/// Supplies the bytes of a GDTF file by the name <see cref="Candela.Core.SceneGraph.Components.FixtureComponent.GdtfSpec"/>
/// uses (typically read from the fixture library). Return null when the file is not available: the project is then
/// saved without it and an issue says so. An exception aborts the whole save and leaves the previous file untouched.
/// When used for autosave the provider is called on a background thread and must be thread-safe.
/// </summary>
public delegate byte[]? GdtfProvider(string gdtfSpec);

/// <summary>Options of <see cref="ProjectFile.Save(string, ProjectData, ProjectSaveOptions?)"/> and <see cref="ProjectSnapshot.Capture"/>.</summary>
public sealed class ProjectSaveOptions
{
    /// <summary>Shared default options.</summary>
    public static ProjectSaveOptions Default { get; } = new();

    /// <summary>
    /// Source of the GDTF files copied into <c>fixtures/</c>. It is asked first for every GDTF the scene uses; when it
    /// returns null (or is not set) the project's own copy (<see cref="ProjectData.GdtfFiles"/>) and then GDTF assets of
    /// the asset store with the same file name are used.
    /// </summary>
    public GdtfProvider? GdtfProvider { get; init; }

    /// <summary>Application version written to the manifest (default: <see cref="ProjectFile.DefaultAppVersion"/>).</summary>
    public string? AppVersion { get; init; }

    /// <summary>
    /// Compression of JSON documents and compressible assets. Files that are already compressed (PNG, JPEG, GIF,
    /// GDTF/zip, …) are always stored without recompression.
    /// </summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    /// <summary>
    /// Write the path each asset was imported from (informational; never used to load). Turn off to keep local folder
    /// names out of projects that are shared.
    /// </summary>
    public bool IncludeSourcePaths { get; init; } = true;

    /// <summary>Clock for the manifest's modification time (tests inject a fixed clock).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Re-open the written temporary file and check its directory before it replaces the target (default true).</summary>
    public bool Verify { get; init; } = true;
}

/// <summary>Options of <see cref="ProjectFile.Load(string, ProjectLoadOptions?)"/>.</summary>
public sealed class ProjectLoadOptions
{
    /// <summary>Shared default options.</summary>
    public static ProjectLoadOptions Default { get; } = new();

    /// <summary>Material library of the new asset store (default: the shared built-in library).</summary>
    public MaterialLibrary? Materials { get; init; }

    /// <summary>Migrations applied to files of older format versions (default: <see cref="ProjectMigrationRegistry.Default"/>).</summary>
    public ProjectMigrationRegistry? Migrations { get; init; }

    /// <summary>
    /// Largest uncompressed size accepted for one archive entry in bytes (default and maximum: the largest byte array,
    /// about 2 GiB). Bigger entries are reported as damaged, whatever size the zip directory declares.
    /// </summary>
    public long MaxEntryBytes { get; init; } = Array.MaxLength;

    /// <summary>Largest uncompressed size of all entries together in bytes (default 16 GiB); later entries are skipped.</summary>
    public long MaxTotalBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>Problems of one kind listed individually before they are summarised in one issue (default 50).</summary>
    public int MaxIssuesPerCode { get; init; } = 50;

    /// <summary>
    /// Check every stored file against the SHA-256 recorded in the index (default true). Damaged files are skipped with
    /// an issue instead of reaching a decoder.
    /// </summary>
    public bool VerifyChecksums { get; init; } = true;
}

/// <summary>Outcome of a successful save.</summary>
public sealed class ProjectSaveResult
{
    internal ProjectSaveResult(ProjectManifest manifest, IReadOnlyList<Issue> issues, IReadOnlyList<string> storedGdtfs,
        IReadOnlyList<string> missingGdtfs, long bytesWritten, string? path)
    {
        Manifest = manifest;
        Issues = issues;
        StoredGdtfs = storedGdtfs;
        MissingGdtfs = missingGdtfs;
        BytesWritten = bytesWritten;
        Path = path;
    }

    /// <summary>The manifest that was written.</summary>
    public ProjectManifest Manifest { get; }

    /// <summary>What the user should know: GDTF files that could not be stored, missing assets, an invalid thumbnail.</summary>
    public IReadOnlyList<Issue> Issues { get; }

    /// <summary>GDTF names stored under <c>fixtures/</c>.</summary>
    public IReadOnlyList<string> StoredGdtfs { get; }

    /// <summary>GDTF names used by the scene but not stored (unavailable or invalid).</summary>
    public IReadOnlyList<string> MissingGdtfs { get; }

    /// <summary>Size of the archive in bytes.</summary>
    public long BytesWritten { get; }

    /// <summary>Full path of the written file, or null when written to a stream.</summary>
    public string? Path { get; }

    /// <summary>When the file was written (UTC).</summary>
    public DateTime SavedUtc => Manifest.ModifiedUtc;
}
