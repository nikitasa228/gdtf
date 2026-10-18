using System.Numerics;
using System.Text.Json;
using Candela.Core.Geometry;
using Candela.Gdtf;

namespace Candela.Mvr;

/// <summary>What an <see cref="ImportedAsset"/> holds.</summary>
public enum ImportedAssetKind
{
    /// <summary>A 3D model referenced by <c>Geometry3D</c> (3DS, glTF or GLB).</summary>
    Model,
}

/// <summary>File format of an imported model.</summary>
public enum ModelFileFormat
{
    /// <summary>3D Studio (<c>.3ds</c>). MVR 3DS files are in millimetres, Z up.</summary>
    ThreeDs,

    /// <summary>glTF 2.0 JSON (<c>.gltf</c>) with its buffers and images in <see cref="ImportedAsset.AdditionalFiles"/>.</summary>
    Gltf,

    /// <summary>Binary glTF 2.0 (<c>.glb</c>). Metres, Y up as glTF requires.</summary>
    Glb,
}

/// <summary>
/// A file brought in by an MVR import that the project must store as an asset (the original bytes are kept). Scene
/// components reference it by <see cref="Id"/> (e.g. <c>MeshComponent.ModelAssetId</c>).
/// </summary>
public sealed class ImportedAsset
{
    /// <summary>Creates an asset record.</summary>
    public ImportedAsset(Guid id, string fileName, byte[] bytes, ImportedAssetKind kind, ModelFileFormat format)
    {
        Id = id;
        FileName = fileName;
        Bytes = bytes;
        Kind = kind;
        Format = format;
    }

    /// <summary>Asset id (derived from the file name and content, so re-importing the same file gives the same id).</summary>
    public Guid Id { get; }

    /// <summary>File name as stored in the MVR archive.</summary>
    public string FileName { get; }

    /// <summary>Original file content.</summary>
    public byte[] Bytes { get; }

    /// <summary>Kind of asset.</summary>
    public ImportedAssetKind Kind { get; }

    /// <summary>Model file format.</summary>
    public ModelFileFormat Format { get; }

    /// <summary>
    /// Other archive files the model needs (glTF buffers and images referenced by relative URI), keyed by their path
    /// relative to the model file.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> AdditionalFiles { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>Metres per file unit as MVR defines it: 0.001 for 3DS (millimetres), 1 for glTF/GLB.</summary>
    public float MetresPerUnit => Format == ModelFileFormat.ThreeDs ? 0.001f : 1f;

    /// <summary>True when the file is Y-up (glTF/GLB) and must be rotated to the engine's Z-up.</summary>
    public bool IsYUp => Format != ModelFileFormat.ThreeDs;

    /// <summary>Decodes the model into engine units (see <see cref="MvrModelFiles.Decode(ImportedAsset, Action{string}?)"/>).</summary>
    public ModelData Decode(Action<string>? warnings = null) => MvrModelFiles.Decode(this, warnings);

    /// <inheritdoc />
    public override string ToString() => $"{FileName} ({Id})";
}

/// <summary>Decoding of MVR 3D files into Core models in engine units (metres, Z up).</summary>
public static class MvrModelFiles
{
    /// <summary>
    /// The model format of a file name, or null when MVR does not support it. A name without extension is 3DS, as MVR
    /// specifies for <c>Geometry3D fileName</c> ("if there is no extension, it will assume that the extension is 3ds").
    /// </summary>
    public static ModelFileFormat? FormatOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        "" or ".3ds" => ModelFileFormat.ThreeDs,
        ".gltf" => ModelFileFormat.Gltf,
        ".glb" => ModelFileFormat.Glb,
        _ => null,
    };

    /// <summary>
    /// Decodes an imported model: 3DS through the GDTF module's 3DS reader scaled from millimetres to metres; glTF/GLB
    /// through SharpGLTF with node transforms baked and rotated from Y up to Z up, <c>(x, y, z) → (x, −z, y)</c>.
    /// Texture keys are left unset (textures stay in the asset's files).
    /// </summary>
    /// <exception cref="InvalidDataException">The file cannot be decoded.</exception>
    public static ModelData Decode(ImportedAsset asset, Action<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        string folder = asset.FileName.Contains('/') ? asset.FileName[..(asset.FileName.LastIndexOf('/') + 1)] : "";
        return Decode(asset.FileName, asset.Bytes, path =>
        {
            string rel = path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? path[folder.Length..] : path;
            return asset.AdditionalFiles.TryGetValue(rel, out var b) ? b : null;
        }, warnings);
    }

    /// <summary>
    /// Decodes a 3DS / glTF / GLB file given its bytes (see <see cref="Decode(ImportedAsset, Action{string}?)"/>).
    /// <paramref name="resolveFile"/> returns other files by path relative to the archive root (glTF resources).
    /// </summary>
    /// <exception cref="InvalidDataException">The file cannot be decoded or has an unsupported extension.</exception>
    public static ModelData Decode(string fileName, byte[] bytes, Func<string, byte[]?>? resolveFile = null, Action<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(bytes);
        var format = FormatOf(fileName) ?? throw new InvalidDataException($"'{fileName}' is not a 3DS, glTF or GLB file.");
        ModelData model;
        try
        {
            if (format == ModelFileFormat.ThreeDs)
            {
                model = ThreeDsReader.Read(bytes, Path.GetFileNameWithoutExtension(fileName), warnings);
                Scale(model, 0.001f);
            }
            else
            {
                model = GltfModelReader.Read(bytes, fileName, resolveFile, textureKeyPrefix: null, warnings);
                RotateYUpToZUp(model);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // SharpGLTF reports malformed JSON and schema problems with its own exception types.
            throw new InvalidDataException($"'{fileName}' cannot be decoded: {e.Message}", e);
        }
        foreach (var part in model.Parts) part.Mesh.Validate();
        return model;
    }

    /// <summary>
    /// Archive files a glTF JSON file references by relative URI (buffers and images), resolved against the model's
    /// folder; data URIs and absolute URLs are skipped. Unparsable JSON yields an empty list.
    /// </summary>
    internal static IReadOnlyList<string> GltfDependencies(string gltfPath, byte[] json)
    {
        var result = new List<string>();
        string folder = gltfPath.Contains('/') ? gltfPath[..(gltfPath.LastIndexOf('/') + 1)] : "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var section in new[] { "buffers", "images" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String) continue;
                    string? u = uri.GetString();
                    if (string.IsNullOrEmpty(u) || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || u.Contains("://", StringComparison.Ordinal)) continue;
                    result.Add(folder + Uri.UnescapeDataString(u));
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            // Malformed JSON or a URI string that is not valid UTF-16: the decoder reports the broken file, and no
            // dependencies can be listed.
        }
        return result;
    }

    private static void Scale(ModelData model, float factor)
    {
        foreach (var part in model.Parts)
        {
            var p = part.Mesh.Positions;
            for (int i = 0; i < p.Length; i++) p[i] *= factor;
            var t = part.Transform;
            t.Translation *= factor;
            part.Transform = t;
        }
    }

    private static void RotateYUpToZUp(ModelData model)
    {
        foreach (var part in model.Parts)
        {
            var m = part.Mesh;
            for (int i = 0; i < m.Positions.Length; i++) m.Positions[i] = ToZUp(m.Positions[i]);
            for (int i = 0; i < m.Normals.Length; i++) m.Normals[i] = ToZUp(m.Normals[i]);
            if (m.Tangents is { } tangents)
                for (int i = 0; i < tangents.Length; i++)
                {
                    var t = tangents[i];
                    tangents[i] = new Vector4(ToZUp(new Vector3(t.X, t.Y, t.Z)), t.W);
                }
        }
    }

    private static Vector3 ToZUp(Vector3 p) => new(p.X, -p.Z, p.Y);
}
