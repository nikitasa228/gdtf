using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Result of <see cref="ModelImporter"/>: the model in Candela space (Z up, right-handed, metres) and the images its
/// materials reference. Texture keys in <see cref="Model"/> are <see cref="ImportOptions.TextureKeyPrefix"/> followed
/// by a relative path that is found in exactly one of <see cref="Files"/>, <see cref="EmbeddedImages"/> or
/// <see cref="GeneratedImages"/>.
/// </summary>
public sealed class ImportedModel
{
    /// <summary>The converted model (parts, materials).</summary>
    public required ModelData Model { get; init; }

    /// <summary>Detected container format.</summary>
    public required ModelFormat Format { get; init; }

    /// <summary>Relative path of the main file inside <see cref="Files"/>.</summary>
    public required string MainFile { get; init; }

    /// <summary>
    /// Every file read while importing — the main file and its companions (MTL, .bin buffers, external textures) — keyed
    /// by normalised relative path. A project stores these to re-import the model elsewhere.
    /// </summary>
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }

    /// <summary>Encoded images stored inside the model file (GLB, FBX), keyed <c>embedded/…</c>.</summary>
    public required IReadOnlyDictionary<string, byte[]> EmbeddedImages { get; init; }

    /// <summary>
    /// Images derived during import, keyed <c>generated/…</c>: packed metallic-roughness maps from separate maps,
    /// normal maps converted from bump maps, albedo with a separate opacity map merged into alpha.
    /// </summary>
    public required IReadOnlyDictionary<string, ImageData> GeneratedImages { get; init; }

    /// <summary>Referenced files that could not be found (textures, MTL), as written in the model.</summary>
    public required IReadOnlyList<string> MissingFiles { get; init; }

    /// <summary>Recoverable problems, written for the user (unsupported texture format, units guessed, …).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>The up axis the importer assumed for the file.</summary>
    public required SourceUpAxis SourceUpAxis { get; init; }

    /// <summary>Metres per file unit that was applied.</summary>
    public required float UnitScale { get; init; }

    /// <summary>True when the units came from file metadata or <see cref="ImportOptions.UnitScale"/>, false when assumed.</summary>
    public required bool UnitsKnown { get; init; }

    /// <summary>Total number of triangles over all parts.</summary>
    public int TriangleCount => Model.Parts.Sum(p => p.Mesh.TriangleCount);

    /// <summary>True when <paramref name="relativePath"/> names an image this result can provide.</summary>
    public bool ContainsImage(string relativePath) =>
        GeneratedImages.ContainsKey(relativePath) || EmbeddedImages.ContainsKey(relativePath) || Files.ContainsKey(relativePath);
}
