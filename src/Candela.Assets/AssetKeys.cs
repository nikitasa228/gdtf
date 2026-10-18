using System.Diagnostics.CodeAnalysis;

namespace Candela.Assets;

/// <summary>
/// Texture/image keys understood by <see cref="AssetStore"/> (see <see cref="Candela.Core.Geometry.MaterialData"/>):
/// <list type="bullet">
/// <item><c>asset:{guid}</c> — the image of an <see cref="AssetKind.Image"/> or <see cref="AssetKind.Texture"/> asset.</item>
/// <item><c>asset:{guid}/{relative path}</c> — a file inside an imported model (texture next to an OBJ, embedded GLB image).</item>
/// <item><c>builtin:{material id}/{channel}</c> — a procedural texture of the <see cref="MaterialLibrary"/>.</item>
/// </list>
/// Relative paths always use forward slashes and never start with a slash or contain <c>..</c>.
/// </summary>
public static class AssetKeys
{
    /// <summary>Prefix of keys that address project assets.</summary>
    public const string AssetScheme = "asset:";

    /// <summary>Prefix of keys that address the built-in material library (same prefix as the ids in MaterialIds).</summary>
    public const string BuiltinScheme = "builtin:";

    /// <summary>Key of the asset's own image: <c>asset:{guid}</c>.</summary>
    public static string ForAsset(Guid id) => AssetScheme + id.ToString("D");

    /// <summary>Key of a file inside a model asset: <c>asset:{guid}/{relative path}</c>.</summary>
    public static string ForFile(Guid id, string relativePath) => ForAsset(id) + "/" + NormalizeRelativePath(relativePath);

    /// <summary>Prefix that <see cref="ModelImporter"/> puts in front of texture paths for a model stored as <paramref name="id"/>.</summary>
    public static string TexturePrefix(Guid id) => ForAsset(id) + "/";

    /// <summary>
    /// Splits an <c>asset:</c> key into the asset id and the optional relative path (null for the asset itself).
    /// Returns false for any other key or a malformed id.
    /// </summary>
    public static bool TryParseAssetKey(string key, out Guid id, out string? relativePath)
    {
        id = default;
        relativePath = null;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(AssetScheme, StringComparison.Ordinal)) return false;
        var rest = key.AsSpan(AssetScheme.Length);
        int slash = rest.IndexOf('/');
        var idPart = slash < 0 ? rest : rest[..slash];
        if (!Guid.TryParse(idPart, out id)) return false;
        if (slash >= 0)
        {
            var path = NormalizeRelativePath(rest[(slash + 1)..].ToString());
            if (path.Length == 0) return false;
            relativePath = path;
        }
        return true;
    }

    /// <summary>
    /// Splits a <c>builtin:</c> texture key (<c>builtin:concrete/albedo</c>) into the material id
    /// (<c>builtin:concrete</c>) and the channel (<c>albedo</c>).
    /// </summary>
    public static bool TryParseBuiltinKey(string key, [NotNullWhen(true)] out string? materialId, [NotNullWhen(true)] out string? channel)
    {
        materialId = null;
        channel = null;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(BuiltinScheme, StringComparison.Ordinal)) return false;
        int slash = key.IndexOf('/', BuiltinScheme.Length);
        if (slash < 0 || slash == key.Length - 1) return false;
        materialId = key[..slash];
        channel = key[(slash + 1)..];
        return true;
    }

    /// <summary>
    /// Normalises a path found in a model file or archive to the canonical relative form: forward slashes, no leading
    /// <c>./</c> or slash, <c>.</c> segments removed and <c>..</c> resolved. A path that climbs above its root keeps only
    /// the segments below the root (so a key can never escape the asset).
    /// </summary>
    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p == ".") continue;
            if (p == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(p);
        }
        return string.Join('/', stack);
    }
}
