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
    /// referenced by <c>Geometry3D</c>; its material textures come from <see cref="TextureProvider"/>. Null: objects are
    /// written without geometry (reported).
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
    /// Tells which GDTF specs (<c>FixtureComponent.GdtfSpec</c>) are Candela's own generic devices — the DMX splitters,
    /// Art-Net / sACN nodes and dimmer packs whose GDTF files Candela generates (<c>Candela.Electrical.GenericDevices</c>).
    /// Their objects are written as ordinary MVR fixtures with the GDTF embedded, so other programs see valid fixtures,
    /// and are also listed in Candela's <c>UserData</c> block (<see cref="CandelaUserData"/>), so Candela imports them
    /// as those devices again. Null: no object is marked.
    /// </summary>
    public Func<string, bool>? IsCandelaDevice { get; init; }

    /// <summary>
    /// Tells, for a fixture entity, which model it stands in for: a fixture drawn with a similar model's GDTF because
    /// the library had no file of the model the scene asks for (e.g. an MVR import of a Depence stub). The writer then
    /// exports the requested <c>GDTFSpec</c> and <c>GDTFMode</c> — what the scene really holds — and embeds the GDTF
    /// <see cref="GdtfProvider"/> supplies for the requested name, unless <see cref="WriteStandIns"/> is set. Returns
    /// null for every other fixture. Null: no fixture is a stand-in.
    /// </summary>
    public Func<Guid, MvrStandIn?>? StandInOf { get; init; }

    /// <summary>
    /// Write stand-in fixtures (<see cref="StandInOf"/>) with the stand-in's GDTF and mode instead of the model and mode
    /// the scene asks for (default false). Other programs then receive the stand-in's fixture type and DMX layout, so
    /// every such fixture is reported as an <see cref="MvrIssueCodes.SubstituteExported"/> warning.
    /// </summary>
    public bool WriteStandIns { get; init; }

    /// <summary>
    /// Returns the encoded image of a material texture key (<c>MaterialData.BaseColorTexture</c> etc.) as PNG or JPEG
    /// bytes — the formats glTF can embed — or null when it is not available. Each key is requested once per export and
    /// the image is embedded in the binary glTF of every model that uses it. Null: models are written with their
    /// material factors only (reported when they have textures).
    /// </summary>
    public Func<string, byte[]?>? TextureProvider { get; init; }

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

/// <summary>
/// The model a stand-in fixture represents (see <see cref="MvrExportOptions.StandInOf"/>): the <c>GDTFSpec</c> and
/// <c>GDTFMode</c> the scene asks for, while the fixture is drawn with another model's GDTF.
/// </summary>
/// <param name="RequestedSpec">GDTF file name of the requested model as the source scene wrote it (e.g. <c>Ayrton@MagicBlade R.gdtf</c>).</param>
/// <param name="RequestedMode">DMX mode the source scene asks for, or null for the GDTF's first mode.</param>
public sealed record MvrStandIn(string RequestedSpec, string? RequestedMode);

/// <summary>Result of an MVR export.</summary>
public sealed class MvrExportResult
{
    internal MvrExportResult(MvrDocument document, IReadOnlyList<Issue> issues, int objectCount, int focusPointCount, int connectionCount,
        int standInCount, int layerPartCount, int textureCount)
    {
        Document = document;
        Issues = issues;
        ObjectCount = objectCount;
        FocusPointCount = focusPointCount;
        ConnectionCount = connectionCount;
        StandInCount = standInCount;
        LayerPartCount = layerPartCount;
        TextureCount = textureCount;
    }

    /// <summary>
    /// Number of stand-in fixtures (<see cref="MvrExportOptions.StandInOf"/>): written with the requested model and
    /// mode, or with the stand-in's when <see cref="MvrExportOptions.WriteStandIns"/> is set.
    /// </summary>
    public int StandInCount { get; }

    /// <summary>
    /// Number of GroupObjects written to repeat a parent on another layer, because some of its children are on that layer
    /// (<see cref="MvrIssueCodes.LayerSplit"/>).
    /// </summary>
    public int LayerPartCount { get; }

    /// <summary>Number of distinct texture images embedded in the exported binary glTF models.</summary>
    public int TextureCount { get; }

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
