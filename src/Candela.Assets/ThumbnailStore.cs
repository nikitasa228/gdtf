using Candela.Core.Geometry;
using Candela.Core.Settings;

namespace Candela.Assets;

/// <summary>
/// On-disk cache of library and asset thumbnails (PNG) in <c>{ConfigPaths.CacheDir}/thumbnails</c>. The renderer
/// produces the images; this class only stores and loads them. Keys are a content hash (e.g.
/// <see cref="AssetEntry.ContentHash"/> or a GDTF file hash: 8–128 hex digits), the square size in pixels and an
/// optional variant ("fixture", "preview-dark", …), so a changed file or a different size never hits a stale image.
/// Files live in two-character fan-out folders: <c>ab/abcdef…_128[_variant].png</c>. Writes are atomic (temporary
/// file + rename); unreadable or corrupt files are treated as missing and deleted. Thread-safe and safe across processes.
/// </summary>
public sealed class ThumbnailStore
{
    /// <summary>Largest thumbnail edge accepted (pixels).</summary>
    public const int MaxSize = 2048;

    /// <summary>Creates a store in <paramref name="directory"/> (default: <c>ConfigPaths.CacheDir/thumbnails</c>).</summary>
    public ThumbnailStore(string? directory = null)
    {
        Directory = Path.GetFullPath(directory ?? Path.Combine(ConfigPaths.CacheDir, "thumbnails"));
    }

    /// <summary>Root folder of the cache.</summary>
    public string Directory { get; }

    /// <summary>Path of the PNG for a key (the file may not exist). Throws <see cref="ArgumentException"/> for invalid keys.</summary>
    public string GetPath(string contentHash, int size, string? variant = null)
    {
        var hash = NormalizeHash(contentHash);
        if (size <= 0 || size > MaxSize) throw new ArgumentOutOfRangeException(nameof(size), $"Thumbnail size must be 1 … {MaxSize} pixels.");
        var name = $"{hash}_{size}";
        if (!string.IsNullOrEmpty(variant)) name += "_" + NormalizeVariant(variant);
        return Path.Combine(Directory, hash[..2], name + ".png");
    }

    /// <summary>True when a thumbnail is stored for the key.</summary>
    public bool Contains(string contentHash, int size, string? variant = null) => File.Exists(GetPath(contentHash, size, variant));

    /// <summary>Stores a thumbnail (replacing an older one). The image is written as 8-bit RGBA PNG.</summary>
    public void Save(string contentHash, int size, ImageData image, string? variant = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageImporter.SavePng(image, GetPath(contentHash, size, variant));
    }

    /// <summary>Stores already-encoded PNG bytes (e.g. produced by the renderer's own encoder).</summary>
    public void SavePng(string contentHash, int size, ReadOnlySpan<byte> png, string? variant = null)
    {
        if (ImageImporter.DetectFormat(png) != "png") throw new ArgumentException("The data is not a PNG image.", nameof(png));
        var path = GetPath(contentHash, size, variant);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = File.Create(tmp)) fs.Write(png);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Loads a thumbnail, or null when it is missing or unreadable (a corrupt file is deleted).</summary>
    public ImageData? Load(string contentHash, int size, string? variant = null)
    {
        var path = GetPath(contentHash, size, variant);
        byte[] data;
        try
        {
            if (!File.Exists(path)) return null;
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (ImageImporter.TryDecode(data, out var image, out _, applyExifOrientation: false))
        {
            Touch(path);
            return image;
        }
        TryDelete(path);
        return null;
    }

    /// <summary>Encoded PNG bytes of a thumbnail (for UI toolkits that decode themselves), or null.</summary>
    public byte[]? LoadPng(string contentHash, int size, string? variant = null)
    {
        var path = GetPath(contentHash, size, variant);
        try
        {
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            if (ImageImporter.DetectFormat(data) != "png")
            {
                TryDelete(path);
                return null;
            }
            Touch(path);
            return data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes every size and variant stored for a hash. Returns the number of files removed.</summary>
    public int Remove(string contentHash)
    {
        var hash = NormalizeHash(contentHash);
        var dir = Path.Combine(Directory, hash[..2]);
        if (!System.IO.Directory.Exists(dir)) return 0;
        int removed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, hash + "_*.png"))
            if (TryDelete(file)) removed++;
        return removed;
    }

    /// <summary>Deletes the whole cache.</summary>
    public void Clear()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Files in use by another process stay; the cache is advisory.
        }
    }

    /// <summary>Total size of the cache in bytes.</summary>
    public long SizeOnDisk()
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        long total = 0;
        foreach (var f in new DirectoryInfo(Directory).EnumerateFiles("*.png", SearchOption.AllDirectories)) total += f.Length;
        return total;
    }

    /// <summary>
    /// Deletes the least recently used thumbnails until the cache is at most <paramref name="maxBytes"/> large (loading a
    /// thumbnail counts as a use). Returns the number of files removed.
    /// </summary>
    public int Trim(long maxBytes)
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        var files = new DirectoryInfo(Directory).EnumerateFiles("*.png", SearchOption.AllDirectories).ToList();
        long total = files.Sum(f => f.Length);
        if (total <= maxBytes) return 0;
        int removed = 0;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.Ordinal))
        {
            if (total <= maxBytes) break;
            long length = f.Length;
            if (TryDelete(f.FullName))
            {
                total -= length;
                removed++;
            }
        }
        return removed;
    }

    private static string NormalizeHash(string contentHash)
    {
        if (string.IsNullOrEmpty(contentHash) || contentHash.Length is < 8 or > 128)
            throw new ArgumentException("A content hash of 8 … 128 hex digits is required.", nameof(contentHash));
        foreach (var c in contentHash)
            if (!char.IsAsciiHexDigit(c)) throw new ArgumentException("The content hash must be hexadecimal.", nameof(contentHash));
        return contentHash.ToLowerInvariant();
    }

    private static string NormalizeVariant(string variant)
    {
        var chars = variant.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        if (s.Length == 0 || s.Length > 64) throw new ArgumentException("The variant must be 1 … 64 letters, digits or dashes.", nameof(variant));
        return s;
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only caches still work; only LRU order suffers.
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
