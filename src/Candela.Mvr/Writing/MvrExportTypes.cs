using Candela.Core.Diagnostics;
using Candela.Core.Geometry;
using Candela.Gdtf;

namespace Candela.Mvr;

/// <summary>Options of <see cref="MvrWriter.Write(Candela.Core.SceneGraph.Scene, Stream, MvrExportOptions?)"/>.</summary>
public sealed class MvrExportOptions
{
    /// <summary>Value of the <c>provider</c> attribute (the application that wrote the file).</summary>
    public const string DefaultProvider = "Candela";

    /// <summary>
    /// Returns the bytes of a GDTF file given the <c>FixtureComponent.GdtfSpec</c> of a fixture (a GDTF file name), or
    /// null when it is not available. Every distinct spec is requested once and embedded in the archive, as MVR
    /// requires. Null: GDTF files are referenced but not embedded (reported).
    /// </summary>
    public Func<string, byte[]?>? GdtfProvider { get; init; }

    /// <summary>
    /// Returns the model of an asset id (<c>MeshComponent.ModelAssetId</c>, <c>TrussComponent.ModelAssetId</c>) in engine
    /// units (metres, Z up), or null when it is not available. Each model is exported once as a binary glTF file and
    /// referenced by <c>Geometry3D</c>. Null: objects are written without geometry (reported).
    /// </summary>
    public Func<Guid, ModelData?>? ModelProvider { get; init; }

    /// <summary>Application name written as <c>provider</c> (default "Candela").</summary>
    public string Provider { get; init; } = DefaultProvider;

    /// <summary>
    /// Application version written as <c>providerVersion</c> (default: the version of this assembly; null or empty to
    /// omit). It is part of the output, so keep it fixed when byte-identical exports are compared.
    /// </summary>
    public string? ProviderVersion { get; init; } = typeof(MvrExportOptions).Assembly.GetName().Version?.ToString(3);

    /// <summary>
    /// Export <c>WallComponent</c> walls, floors and ceilings as scene objects with a generated box model (default true),
    /// so consoles and visualisers see the venue.
    /// </summary>
    public bool ExportWalls { get; init; } = true;

    /// <summary>
    /// A GDTF provider that reads files of a fixture library by file name (<c>FixtureLibrary.FindByFileName</c>), then
    /// from the extra folders in order (e.g. a project's <c>fixtures/</c> folder). Unreadable files give null.
    /// </summary>
    public static Func<string, byte[]?> GdtfFromLibrary(FixtureLibrary? library, params string[] extraFolders)
    {
        ArgumentNullException.ThrowIfNull(extraFolders);
        var folders = extraFolders.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        return spec =>
        {
            string name = GdtfResolver.EnsureGdtfExtension(MvrDocument.FileNameOf(MvrDocument.NormalizePath(spec)));
            var candidates = new List<string>(folders.Length + 1);
            if (library?.FindByFileName(name) is { } entry) candidates.Add(entry.FilePath);
            foreach (var folder in folders) candidates.Add(Path.Combine(folder, name));
            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path)) return File.ReadAllBytes(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Try the next location; the writer reports a GDTF that nobody could supply.
                }
            }
            return null;
        };
    }
}

/// <summary>Result of an MVR export.</summary>
public sealed class MvrExportResult
{
    internal MvrExportResult(MvrDocument document, IReadOnlyList<Issue> issues, int objectCount, int focusPointCount, int connectionCount)
    {
        Document = document;
        Issues = issues;
        ObjectCount = objectCount;
        FocusPointCount = focusPointCount;
        ConnectionCount = connectionCount;
    }

    /// <summary>The MVR object model that was (or would be) written, including the embedded files.</summary>
    public MvrDocument Document { get; }

    /// <summary>What could not be exported or was exported approximately (never contains exceptions).</summary>
    public IReadOnlyList<Issue> Issues { get; }

    /// <summary>Number of MVR objects written for scene entities (focus points not included).</summary>
    public int ObjectCount { get; }

    /// <summary>Number of <c>FocusPoint</c> objects written for fixtures with a focus point.</summary>
    public int FocusPointCount { get; }

    /// <summary>Number of <c>Connection</c> elements written for cables between devices.</summary>
    public int ConnectionCount { get; }
}
