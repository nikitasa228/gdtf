using System.Numerics;
using System.Xml.Linq;
using Candela.Core.Diagnostics;

namespace Candela.Mvr;

/// <summary>
/// An MVR file (My Virtual Rig, DIN SPEC 15801, versions 1.4 – 1.6) as an object model: the
/// <c>GeneralSceneDescription.xml</c> content (version, provider, user data, AUX data and layers with their object
/// trees) and the files embedded in the archive (GDTF fixtures, 3D models, images).
/// </summary>
/// <remarks>
/// The model is faithful to the file: names, uuids and values are kept as written, elements this reader does not know
/// are preserved in <see cref="MvrObject.UnknownElements"/> and written back by <see cref="MvrWriter.WriteDocument(MvrDocument, Stream)"/>.
/// Lengths are converted to the engine's SI units on read (MVR millimetres → metres) and back on write; matrices are
/// System.Numerics row-vector matrices (see <see cref="MvrMatrix"/>). Problems found while reading are in
/// <see cref="Issues"/>; a document is never rejected because of one bad element.
/// </remarks>
public sealed class MvrDocument
{
    private Dictionary<string, MvrEmbeddedFile>? _byPath;
    private Dictionary<string, MvrEmbeddedFile?>? _byFileName;

    /// <summary>Major version (<c>verMajor</c>); 1 for every published MVR version.</summary>
    public int VerMajor { get; set; } = 1;

    /// <summary>Minor version (<c>verMinor</c>): 4, 5 or 6 for the versions this module targets.</summary>
    public int VerMinor { get; set; } = 6;

    /// <summary>Application that wrote the file (<c>provider</c>, MVR 1.6), or null.</summary>
    public string? Provider { get; set; }

    /// <summary>Version of the writing application (<c>providerVersion</c>, MVR 1.6), or null.</summary>
    public string? ProviderVersion { get; set; }

    /// <summary>Application-specific <c>UserData/Data</c> blocks, kept verbatim.</summary>
    public List<MvrUserData> UserData { get; } = new();

    /// <summary>Shared definitions of the scene (<c>Scene/AUXData</c>).</summary>
    public MvrAuxData AuxData { get; } = new();

    /// <summary>Layers in file order; every scene object lives in exactly one layer.</summary>
    public List<MvrLayer> Layers { get; } = new();

    /// <summary>Every file of the archive except <c>GeneralSceneDescription.xml</c>, in archive order.</summary>
    public List<MvrEmbeddedFile> Files { get; } = new();

    /// <summary>Problems found while reading (or building) the document. Never contains exceptions.</summary>
    public List<Issue> Issues { get; } = new();

    /// <summary>Name of the source file when the document was read from disk, or null.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>Version as <c>major.minor</c>.</summary>
    public Version Version => new(System.Math.Max(0, VerMajor), System.Math.Max(0, VerMinor));

    /// <summary>All objects of all layers, depth first in file order (parents before their children).</summary>
    public IEnumerable<MvrObject> AllObjects()
    {
        foreach (var layer in Layers)
            foreach (var o in layer.Objects)
                foreach (var d in o.SelfAndDescendants())
                    yield return d;
    }

    /// <summary>
    /// Finds an embedded file as MVR references it (<c>GDTFSpec</c>, <c>Geometry3D fileName</c>, gobo image): exact
    /// archive path first, then ignoring case and folders. A GDTF reference may omit the <c>.gdtf</c> extension.
    /// Returns null when there is no such file or when the bare file name is ambiguous.
    /// </summary>
    public MvrEmbeddedFile? FindFile(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        EnsureFileIndex();
        string path = NormalizePath(reference);
        if (_byPath!.TryGetValue(path, out var hit)) return hit;
        if (_byFileName!.TryGetValue(FileNameOf(path), out var byName) && byName is not null) return byName;
        if (!path.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase))
        {
            if (_byPath.TryGetValue(path + ".gdtf", out hit)) return hit;
            if (_byFileName.TryGetValue(FileNameOf(path) + ".gdtf", out byName) && byName is not null) return byName;
        }
        return null;
    }

    /// <summary>Drops the file lookup index; call after changing <see cref="Files"/> once <see cref="FindFile"/> was used.</summary>
    public void InvalidateFileIndex()
    {
        _byPath = null;
        _byFileName = null;
    }

    /// <summary>Archive paths use forward slashes and no leading slash.</summary>
    internal static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    internal static string FileNameOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private void EnsureFileIndex()
    {
        if (_byPath is not null && _byFileName is not null) return;
        var byPath = new Dictionary<string, MvrEmbeddedFile>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, MvrEmbeddedFile?>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files)
        {
            string p = NormalizePath(f.Name);
            byPath.TryAdd(p, f);
            string n = FileNameOf(p);
            if (byName.TryGetValue(n, out var existing))
            {
                if (existing is not null && !ReferenceEquals(existing, f)) byName[n] = null; // ambiguous
            }
            else
            {
                byName[n] = f;
            }
        }
        _byPath = byPath;
        _byFileName = byName;
    }
}

/// <summary>One <c>UserData/Data</c> block: application data that other programs must keep but not interpret.</summary>
public sealed class MvrUserData
{
    /// <summary>Application that owns the block (<c>provider</c>).</summary>
    public string Provider { get; set; } = "";

    /// <summary>Version of the block's format (<c>ver</c>).</summary>
    public string Version { get; set; } = "";

    /// <summary>The whole <c>Data</c> element as read (content is application-defined).</summary>
    public XElement? Element { get; set; }
}

/// <summary>Shared definitions of a scene (<c>AUXData</c>): classes, positions, symbol definitions, mapping definitions.</summary>
public sealed class MvrAuxData
{
    /// <summary>Classes (visibility groups such as "Lighting", "Rigging"), referenced by <see cref="MvrObject.Classing"/>.</summary>
    public List<MvrClass> Classes { get; } = new();

    /// <summary>Named hanging positions, referenced by <see cref="MvrDeviceObject.Position"/>.</summary>
    public List<MvrPositionDefinition> Positions { get; } = new();

    /// <summary>Symbol definitions (re-usable geometry), instanced by <see cref="MvrSymbol"/>.</summary>
    public List<MvrSymdef> Symdefs { get; } = new();

    /// <summary>Video mapping definitions, referenced by <see cref="MvrMapping.LinkedDefinition"/>.</summary>
    public List<MvrMappingDefinition> MappingDefinitions { get; } = new();

    /// <summary>Elements of <c>AUXData</c> this reader does not know, kept verbatim.</summary>
    public List<XElement> UnknownElements { get; } = new();
}

/// <summary>A class (<c>AUXData/Class</c>).</summary>
public sealed class MvrClass
{
    /// <summary>Identity.</summary>
    public Guid Uuid { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>A hanging position (<c>AUXData/Position</c>), e.g. "1st Electric".</summary>
public sealed class MvrPositionDefinition
{
    /// <summary>Identity.</summary>
    public Guid Uuid { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// A symbol definition (<c>AUXData/Symdef</c>): geometry defined once and placed by <see cref="MvrSymbol"/> instances.
/// Its children may themselves be symbols (nested definitions).
/// </summary>
public sealed class MvrSymdef
{
    /// <summary>Identity, referenced by <see cref="MvrSymbol.SymdefId"/>.</summary>
    public Guid Uuid { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Geometry of the definition (<c>Symdef/ChildList</c>), relative to the symbol's origin.</summary>
    public List<MvrGeometry> Geometries { get; } = new();
}

/// <summary>A video mapping definition (<c>AUXData/MappingDefinition</c>): the input a pixel-mapped device shows.</summary>
public sealed class MvrMappingDefinition
{
    /// <summary>Identity.</summary>
    public Guid Uuid { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Width of the mapped content in pixels.</summary>
    public int SizeX { get; set; }

    /// <summary>Height of the mapped content in pixels.</summary>
    public int SizeY { get; set; }

    /// <summary>Video source, or null.</summary>
    public MvrSource? Source { get; set; }

    /// <summary>How the content is fitted (<c>ScaleHandeling</c>), or null when not written.</summary>
    public MvrScaleHandling? ScaleHandling { get; set; }
}

/// <summary>
/// A layer (<c>Layers/Layer</c>): a named group of scene objects. Layers may carry a matrix that places all their
/// objects (rare; identity when absent).
/// </summary>
public sealed class MvrLayer
{
    /// <summary>Identity (kept as the Core layer id on import).</summary>
    public Guid Uuid { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Layer transform in metres (row-vector convention), identity when the file has none.</summary>
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    /// <summary>True when the file wrote a <c>Matrix</c> for the layer.</summary>
    public bool HasMatrix { get; set; }

    /// <summary>Objects of the layer (<c>ChildList</c>) in file order.</summary>
    public List<MvrObject> Objects { get; } = new();

    /// <summary>Elements of the layer this reader does not know, kept verbatim.</summary>
    public List<XElement> UnknownElements { get; } = new();

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Kind of a file embedded in an MVR archive, from its extension.</summary>
public enum MvrFileKind
{
    /// <summary>A GDTF fixture type (<c>.gdtf</c>).</summary>
    Gdtf,

    /// <summary>A 3D Studio mesh (<c>.3ds</c>; MVR: millimetres, Z up).</summary>
    ThreeDs,

    /// <summary>A glTF 2.0 JSON file (<c>.gltf</c>; metres, Y up as glTF requires).</summary>
    Gltf,

    /// <summary>A binary glTF 2.0 file (<c>.glb</c>; metres, Y up as glTF requires).</summary>
    Glb,

    /// <summary>An image (textures, gobos, video stills).</summary>
    Image,

    /// <summary>Anything else (glTF buffers, documents, …).</summary>
    Other,
}

/// <summary>A file stored in the MVR archive next to <c>GeneralSceneDescription.xml</c>.</summary>
public sealed class MvrEmbeddedFile
{
    /// <summary>Creates a file record.</summary>
    public MvrEmbeddedFile(string name, byte[]? bytes, string? error = null)
    {
        Name = MvrDocument.NormalizePath(name);
        Bytes = bytes;
        Error = error;
        Kind = KindOf(Name);
    }

    /// <summary>Path inside the archive (forward slashes).</summary>
    public string Name { get; }

    /// <summary>Kind from the extension.</summary>
    public MvrFileKind Kind { get; }

    /// <summary>Content, or null when the entry could not be read (see <see cref="Error"/>).</summary>
    public byte[]? Bytes { get; }

    /// <summary>Why the entry could not be read, or null.</summary>
    public string? Error { get; }

    /// <summary>True when the content is available.</summary>
    public bool IsReadable => Bytes is not null;

    /// <summary>Classifies a file name by its extension.</summary>
    public static MvrFileKind KindOf(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".gdtf" => MvrFileKind.Gdtf,
            ".3ds" => MvrFileKind.ThreeDs,
            ".gltf" => MvrFileKind.Gltf,
            ".glb" => MvrFileKind.Glb,
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".gif" or ".svg" or ".webp" or ".ktx2" => MvrFileKind.Image,
            _ => MvrFileKind.Other,
        };
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
