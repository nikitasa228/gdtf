using System.Security.Cryptography;
using System.Text;

namespace Candela.Assets;

/// <summary>
/// One asset of a project: the original file (bytes kept unchanged so the project can be re-saved and opened elsewhere)
/// plus, for models, the companion files it needs. Immutable; byte arrays are shared and must not be modified.
/// </summary>
public sealed class AssetEntry
{
    internal AssetEntry(Guid id, AssetKind kind, string originalFileName, byte[] data, IReadOnlyDictionary<string, byte[]> companions,
        string? sourcePath, ImportOptions? importOptions)
    {
        Id = id;
        Kind = kind;
        OriginalFileName = originalFileName;
        Data = data;
        CompanionFiles = companions;
        SourcePath = sourcePath;
        ImportOptions = importOptions;
        ContentHash = ComputeHash(data, companions);
        TotalSize = data.LongLength + companions.Values.Sum(b => b.LongLength);
    }

    /// <summary>Asset id (referenced by MeshComponent.ModelAssetId, ImageComponent.ImageAssetId and texture keys).</summary>
    public Guid Id { get; }

    /// <summary>What the asset is.</summary>
    public AssetKind Kind { get; }

    /// <summary>File name the asset was imported from (no folder), e.g. "chair.obj".</summary>
    public string OriginalFileName { get; }

    /// <summary>Full path the file was imported from, when known (informational; never used to load).</summary>
    public string? SourcePath { get; }

    /// <summary>Original bytes of the main file. Shared; do not modify.</summary>
    public byte[] Data { get; }

    /// <summary>Other files of a model (MTL, .bin, textures) keyed by relative path; empty for other kinds.</summary>
    public IReadOnlyDictionary<string, byte[]> CompanionFiles { get; }

    /// <summary>Import options of a model (axis/unit overrides) without the texture prefix; null for other kinds.</summary>
    public ImportOptions? ImportOptions { get; }

    /// <summary>
    /// Lower-case hex SHA-256 over the main file and the companions (sorted by path). Identical imports have identical
    /// hashes; used as the thumbnail cache key.
    /// </summary>
    public string ContentHash { get; }

    /// <summary>Bytes of the main file and companions together.</summary>
    public long TotalSize { get; }

    /// <summary>Texture key of the asset's own image (<c>asset:{id}</c>).</summary>
    public string ImageKey => AssetKeys.ForAsset(Id);

    internal AssetEntry WithImportOptions(ImportOptions? options) =>
        new(Id, Kind, OriginalFileName, Data, CompanionFiles, SourcePath, options);

    /// <summary>The hash <see cref="ContentHash"/> uses, for content that is not (yet) an asset.</summary>
    public static string ComputeHash(ReadOnlySpan<byte> data, IReadOnlyDictionary<string, byte[]>? companions = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(data);
        if (companions is { Count: > 0 })
        {
            Span<byte> length = stackalloc byte[8];
            foreach (var key in companions.Keys.Order(StringComparer.Ordinal))
            {
                var bytes = companions[key];
                hash.AppendData(Encoding.UTF8.GetBytes(key));
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
