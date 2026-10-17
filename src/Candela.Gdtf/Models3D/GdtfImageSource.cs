using Candela.Core.Geometry;
using StbImageSharp;

namespace Candela.Gdtf;

/// <summary>
/// Resolves texture keys that point into GDTF archives and decodes them to RGBA8 (<see cref="ImageData"/>):
/// <list type="bullet">
/// <item><c>gdtf:{fixture file}#{entry}</c> — a file in the archive (wheel gobo <c>wheels/gobo1.png</c>, thumbnail,
/// texture referenced by a model);</item>
/// <item><c>gdtf:{fixture file}#{model entry}?image={n}</c> — image <c>n</c> embedded in a GLB buffer.</item>
/// </list>
/// PNG, JPEG, BMP, TGA and GIF are decoded; SVG and unknown formats return null (raw bytes via <see cref="LoadBytes"/>).
/// </summary>
public sealed class GdtfImageSource : IImageSource
{
    /// <summary>Key scheme prefix.</summary>
    public const string Scheme = "gdtf:";

    private const string EmbeddedMarker = "?image=";
    private readonly Func<string, GdtfArchive?> _resolveArchive;

    /// <summary>Creates a source; <paramref name="resolveArchive"/> maps a fixture file name to an open archive (or null).</summary>
    public GdtfImageSource(Func<string, GdtfArchive?> resolveArchive) =>
        _resolveArchive = resolveArchive ?? throw new ArgumentNullException(nameof(resolveArchive));

    /// <summary>Creates a source that serves keys of one archive.</summary>
    public GdtfImageSource(GdtfArchive archive)
        : this(name => string.Equals(name, archive.FileName, StringComparison.OrdinalIgnoreCase) ? archive : null)
    {
    }

    /// <summary>Builds <c>gdtf:{fixtureFileName}#{entryPath}</c>.</summary>
    public static string MakeKey(string fixtureFileName, string entryPath) => $"{Scheme}{fixtureFileName}#{entryPath}";

    /// <summary>Splits a key into fixture file name and entry path (the entry may carry <c>?image=n</c>).</summary>
    public static bool TryParseKey(string key, out string fixtureFileName, out string entryPath)
    {
        fixtureFileName = entryPath = "";
        if (key is null || !key.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        int hash = key.IndexOf('#', Scheme.Length);
        if (hash < 0) return false;
        fixtureFileName = key[Scheme.Length..hash];
        entryPath = key[(hash + 1)..];
        return fixtureFileName.Length > 0 && entryPath.Length > 0;
    }

    /// <inheritdoc />
    public ImageData? Load(string key)
    {
        var bytes = LoadBytes(key);
        return bytes is null ? null : Decode(bytes);
    }

    /// <summary>The encoded bytes behind a key (PNG, JPEG, SVG …), or null when the key cannot be resolved.</summary>
    public byte[]? LoadBytes(string key)
    {
        if (!TryParseKey(key, out var file, out var entry)) return null;
        var archive = _resolveArchive(file);
        if (archive is null) return null;

        int marker = entry.IndexOf(EmbeddedMarker, StringComparison.Ordinal);
        if (marker < 0) return archive.TryReadBytes(entry);

        string modelEntry = entry[..marker];
        if (!int.TryParse(entry.AsSpan(marker + EmbeddedMarker.Length), out int index) || index < 0) return null;
        var data = archive.TryReadBytes(modelEntry);
        if (data is null) return null;
        try
        {
            var root = GltfModelReader.Parse(data, modelEntry, archive.TryReadBytes);
            if (index >= root.LogicalImages.Count) return null;
            return root.LogicalImages[index].Content.Content.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Decodes PNG/JPEG/BMP/TGA/GIF bytes to RGBA8 (top row first); null when the format is not supported.</summary>
    public static ImageData? Decode(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length < 8) return null;
        try
        {
            var result = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
            return new ImageData { Width = result.Width, Height = result.Height, Format = PixelFormat.Rgba8, Pixels = result.Data };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        {
            return null;
        }
        catch (Exception e) when (e.GetType().Name.Contains("Stb", StringComparison.Ordinal))
        {
            return null;
        }
    }
}
