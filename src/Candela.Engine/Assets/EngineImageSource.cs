using System.Collections.Concurrent;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Gdtf;

namespace Candela.Engine;

/// <summary>
/// The image source the engine gives the renderer: resolves every texture key a material of the scene can carry —
/// <c>gdtf:{file}#{entry}</c> (fixture model textures and gobos, from the library's GDTF archives),
/// <c>asset:{id}</c> and <c>builtin:{material}/{channel}</c> (through the <see cref="AssetStore"/>), and
/// <c>asset:{id}/{path}</c> of model assets (from the decoded model held by <see cref="EngineModels"/>, so embedded and
/// generated textures of a model decoded in the import worker never require the native importer again).
/// </summary>
/// <remarks>Thread-safe. Decoded images are cached per key; <see cref="Clear"/> drops the cache and open archives.</remarks>
public sealed class EngineImageSource : IImageSource, IDisposable
{
    private readonly AssetStore _store;
    private readonly EngineModels _models;
    private readonly Func<FixtureLibrary> _library;
    private readonly ConcurrentDictionary<string, Lazy<ImageData?>> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GdtfArchive?> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly GdtfImageSource _gdtf;

    /// <summary>Creates the source.</summary>
    public EngineImageSource(AssetStore store, EngineModels models, Func<FixtureLibrary> library)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _gdtf = new GdtfImageSource(OpenArchive);
    }

    /// <inheritdoc />
    public ImageData? Load(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith(GdtfImageSource.Scheme, StringComparison.Ordinal))
            return _images.GetOrAdd(key, k => new Lazy<ImageData?>(() => _gdtf.Load(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (AssetKeys.TryParseAssetKey(key, out var id, out var relative) && relative is not null && _models.TryGetLoaded(id, out var model))
            return _images.GetOrAdd(key, _ => new Lazy<ImageData?>(() => FromModel(model, relative), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return _store.Load(key);
    }

    /// <summary>Drops cached images and closes the GDTF archives (library rescanned, project replaced).</summary>
    public void Clear()
    {
        _images.Clear();
        foreach (var a in _archives.Values) a?.Dispose();
        _archives.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Clear();

    /// <summary>The archive of a library GDTF by file name (opened lazily and kept), or null.</summary>
    internal GdtfArchive? OpenArchive(string fileName) => _archives.GetOrAdd(fileName, name =>
    {
        var entry = _library().FindByFileName(name);
        if (entry is null) return null;
        try
        {
            return GdtfArchive.Open(entry.FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    });

    private ImageData? FromModel(ImportedModel model, string relative)
    {
        if (model.GeneratedImages.TryGetValue(relative, out var generated)) return generated;
        byte[]? bytes = model.EmbeddedImages.GetValueOrDefault(relative) ?? model.Files.GetValueOrDefault(relative);
        if (bytes is null)
        {
            foreach (var (k, v) in model.Files)
            {
                if (string.Equals(k, relative, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = v;
                    break;
                }
            }
        }
        return bytes is not null && ImageImporter.TryDecode(bytes, out var image, out _) ? image : null;
    }
}
