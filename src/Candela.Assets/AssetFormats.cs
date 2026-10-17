namespace Candela.Assets;

/// <summary>3D model container formats Candela distinguishes (axis/unit conventions differ per format).</summary>
public enum ModelFormat
{
    /// <summary>Not a model format Candela knows.</summary>
    Unknown,

    /// <summary>Wavefront OBJ (+ MTL). No unit metadata; conventionally Y-up.</summary>
    Obj,

    /// <summary>Autodesk FBX (binary or ASCII). Axis system and UnitScaleFactor are in the file.</summary>
    Fbx,

    /// <summary>COLLADA (.dae). &lt;unit meter&gt; and &lt;up_axis&gt; are in the file.</summary>
    Collada,

    /// <summary>3D Studio (.3ds). Z-up, units unknown.</summary>
    ThreeDs,

    /// <summary>Stereolithography (.stl, ASCII or binary). Z-up (CAD), units unknown (often millimetres).</summary>
    Stl,

    /// <summary>Stanford polygon file (.ply). Treated as Z-up, units unknown.</summary>
    Ply,

    /// <summary>glTF 2.0 JSON (.gltf) with external or embedded buffers. Y-up metres by specification.</summary>
    Gltf,

    /// <summary>Binary glTF 2.0 (.glb). Y-up metres by specification.</summary>
    Glb,

    /// <summary>3D Manufacturing Format (.3mf). Z-up; the unit is in the file (default millimetres).</summary>
    ThreeMf,

    /// <summary>Any other format AssimpNet reads (.x, .ase, .lwo, .off, .ac, .x3d, .ms3d, .zae); read in Assimp's Y-up convention.</summary>
    OtherAssimp,

    /// <summary>SketchUp (.skp): closed format, detected only to be rejected with a helpful message.</summary>
    SketchUp,
}

/// <summary>File-type detection by extension and magic bytes.</summary>
public static class AssetFormats
{
    /// <summary>Message shown when a SketchUp file is imported.</summary>
    public const string SketchUpMessage = "SketchUp files cannot be read; export from SketchUp as .dae, .fbx or .glb";

    private static readonly Dictionary<string, ModelFormat> s_modelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".obj"] = ModelFormat.Obj,
        [".fbx"] = ModelFormat.Fbx,
        [".dae"] = ModelFormat.Collada,
        [".3ds"] = ModelFormat.ThreeDs,
        [".stl"] = ModelFormat.Stl,
        [".ply"] = ModelFormat.Ply,
        [".gltf"] = ModelFormat.Gltf,
        [".glb"] = ModelFormat.Glb,
        [".skp"] = ModelFormat.SketchUp,
        // Further formats handled by the bundled Assimp build that are reasonably common for set and prop models.
        [".3mf"] = ModelFormat.ThreeMf,
        [".x"] = ModelFormat.OtherAssimp,
        [".ase"] = ModelFormat.OtherAssimp,
        [".lwo"] = ModelFormat.OtherAssimp,
        [".off"] = ModelFormat.OtherAssimp,
        [".ac"] = ModelFormat.OtherAssimp,
        [".x3d"] = ModelFormat.OtherAssimp,
        [".ms3d"] = ModelFormat.OtherAssimp,
        [".zae"] = ModelFormat.OtherAssimp,
    };

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".tga", ".bmp", ".psd", ".gif", ".hdr",
    };

    /// <summary>Model file extensions that can be imported (lower case, with dot; SketchUp excluded).</summary>
    public static IReadOnlyList<string> ModelExtensions { get; } =
        s_modelExtensions.Where(kv => kv.Value != ModelFormat.SketchUp).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Image file extensions that can be imported (lower case, with dot).</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } = s_imageExtensions.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Model format from a file name or extension (<see cref="ModelFormat.Unknown"/> when not a model).</summary>
    public static ModelFormat GetModelFormat(string fileNameOrExtension)
    {
        var ext = fileNameOrExtension.StartsWith('.') ? fileNameOrExtension : Path.GetExtension(fileNameOrExtension);
        return s_modelExtensions.TryGetValue(ext, out var f) ? f : ModelFormat.Unknown;
    }

    /// <summary>True when the extension is an importable model format.</summary>
    public static bool IsModelFile(string fileNameOrExtension)
    {
        var f = GetModelFormat(fileNameOrExtension);
        return f != ModelFormat.Unknown && f != ModelFormat.SketchUp;
    }

    /// <summary>True when the extension is an image format <see cref="ImageImporter"/> decodes.</summary>
    public static bool IsImageFile(string fileNameOrExtension)
    {
        var ext = fileNameOrExtension.StartsWith('.') ? fileNameOrExtension : Path.GetExtension(fileNameOrExtension);
        return s_imageExtensions.Contains(ext);
    }

    /// <summary>True for a SketchUp file by extension or by its header (the file starts with "SketchUp Model" in UTF-16).</summary>
    public static bool IsSketchUp(string fileName, ReadOnlySpan<byte> header)
    {
        if (GetModelFormat(fileName) == ModelFormat.SketchUp) return true;
        return LooksLikeSketchUp(header);
    }

    /// <summary>Recognises the SketchUp header: 0xFF 0xFE 0xFF, a length byte, then "SketchUp Model" in UTF-16LE.</summary>
    public static bool LooksLikeSketchUp(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> marker = "S\0k\0e\0t\0c\0h\0U\0p\0"u8;
        int window = System.Math.Min(header.Length, 64);
        return window >= marker.Length && header[..window].IndexOf(marker) >= 0;
    }

    /// <summary>True for a binary glTF container (magic "glTF").</summary>
    public static bool IsGlb(ReadOnlySpan<byte> data) => data.Length >= 12 && data[..4].SequenceEqual("glTF"u8);

    /// <summary>
    /// Kind of asset a file becomes when imported, or null when the file type is not supported.
    /// Image files become <see cref="AssetKind.Image"/>; pass <see cref="AssetKind.Texture"/> explicitly for textures.
    /// </summary>
    public static AssetKind? GuessKind(string fileName)
    {
        if (IsModelFile(fileName)) return AssetKind.Model;
        if (IsImageFile(fileName)) return AssetKind.Image;
        if (string.Equals(Path.GetExtension(fileName), ".gdtf", StringComparison.OrdinalIgnoreCase)) return AssetKind.Gdtf;
        return null;
    }
}
