namespace Candela.Project;

/// <summary>
/// Contents of <c>manifest.json</c>: what the archive is, which format version and application wrote it, and summary
/// counts that let an Open dialog or recent-files list describe a project without loading it
/// (<see cref="ProjectFile.ReadManifest(string)"/>). Times are UTC.
/// </summary>
public sealed record ProjectManifest
{
    /// <summary>Always <see cref="ProjectFile.FormatId"/> (<c>candela-project</c>).</summary>
    public string Format { get; init; } = ProjectFile.FormatId;

    /// <summary>Version of the archive layout and its JSON documents (<see cref="ProjectFile.FormatVersion"/> when written).</summary>
    public int FormatVersion { get; init; } = ProjectFile.FormatVersion;

    /// <summary>Version of the application that wrote the file (informational).</summary>
    public string AppVersion { get; init; } = "";

    /// <summary>When the project was first saved (UTC).</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>When the file was written (UTC).</summary>
    public DateTime ModifiedUtc { get; init; }

    /// <summary>Stable project identity (names the autosave files; kept by Save as).</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Name of the scene.</summary>
    public string SceneName { get; init; } = "";

    /// <summary>Number of entities in <c>scene.json</c>.</summary>
    public int EntityCount { get; init; }

    /// <summary>Number of entities carrying a fixture component.</summary>
    public int FixtureCount { get; init; }

    /// <summary>Number of assets stored under <c>assets/</c>.</summary>
    public int AssetCount { get; init; }

    /// <summary>Number of GDTF files stored under <c>fixtures/</c>.</summary>
    public int GdtfCount { get; init; }

    /// <summary>True when the archive holds <c>thumbnail.png</c>.</summary>
    public bool HasThumbnail { get; init; }
}
