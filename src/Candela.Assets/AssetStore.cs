using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Candela.Core.Diagnostics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Catalogue of a project's assets (models, images, textures, GDTF files) with lazily decoded, thread-safe caches.
/// <list type="bullet">
/// <item>Import: <see cref="ImportFile"/> / <see cref="ImportModel"/> / <see cref="ImportImage"/> read files from disk
/// (models with their companion files), validate them and store the original bytes. Importing identical content twice
/// returns the existing entry.</item>
/// <item>Persistence (used by Candela.Project): <see cref="Entries"/>, <see cref="Add"/> (from bytes, keeping the id),
/// <see cref="GetBytes"/>, <see cref="GetCompanionFiles"/>.</item>
/// <item>Decoding: <see cref="GetModel"/>, <see cref="GetImage"/> decode on first use (once, even under concurrent
/// requests) and cache the result or the error.</item>
/// <item><see cref="IImageSource"/>: <c>asset:{guid}</c> (image/texture assets), <c>asset:{guid}/{relative path}</c>
/// (textures of model assets: companion files, embedded and generated images) and <c>builtin:{id}/{channel}</c>
/// (delegated to <see cref="Materials"/>).</item>
/// </list>
/// Problems (undecodable files, missing textures) never throw from <see cref="Load"/>; they are reported once as
/// <see cref="Issues"/>. All members are thread-safe.
/// </summary>
public sealed class AssetStore : IImageSource
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Slot> _slots = new();
    private readonly List<Guid> _order = new();
    private readonly List<Issue> _issues = new();
    private readonly HashSet<string> _issueKeys = new(StringComparer.Ordinal);

    /// <summary>Creates an empty store whose built-in keys resolve through <paramref name="materials"/> (default: shared library).</summary>
    public AssetStore(MaterialLibrary? materials = null)
    {
        Materials = materials ?? MaterialLibrary.Default;
    }

    /// <summary>The built-in material library used for <c>builtin:</c> keys.</summary>
    public MaterialLibrary Materials { get; }

    /// <summary>Raised (on the calling thread) after an asset was added, removed or its import options changed.</summary>
    public event Action<Guid>? AssetChanged;

    /// <summary>Raised (on the decoding thread) when a new issue is recorded.</summary>
    public event Action<Issue>? IssueReported;

    /// <summary>Number of assets.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _slots.Count;
        }
    }

    /// <summary>Snapshot of all entries in the order they were added.</summary>
    public IReadOnlyList<AssetEntry> Entries
    {
        get
        {
            lock (_gate) return _order.Select(id => _slots[id].Entry).ToArray();
        }
    }

    /// <summary>Snapshot of the problems found while decoding (one entry per distinct problem).</summary>
    public IReadOnlyList<Issue> Issues
    {
        get
        {
            lock (_gate) return _issues.ToArray();
        }
    }

    /// <summary>The entry with this id, or null.</summary>
    public AssetEntry? Find(Guid id)
    {
        lock (_gate) return _slots.TryGetValue(id, out var s) ? s.Entry : null;
    }

    /// <summary>True when an asset with this id exists.</summary>
    public bool Contains(Guid id)
    {
        lock (_gate) return _slots.ContainsKey(id);
    }

    /// <summary>An entry with identical content and kind, or null.</summary>
    public AssetEntry? FindByContentHash(string contentHash, AssetKind? kind = null)
    {
        lock (_gate)
        {
            foreach (var id in _order)
            {
                var e = _slots[id].Entry;
                if (e.ContentHash == contentHash && (kind is null || e.Kind == kind)) return e;
            }
        }
        return null;
    }

    /// <summary>
    /// Imports a file from disk, choosing the kind from its extension (<paramref name="kind"/> forces one, e.g.
    /// <see cref="AssetKind.Texture"/>). Throws <see cref="AssetImportException"/> with a user-facing message (SketchUp
    /// files, unsupported formats, unreadable or corrupt files).
    /// </summary>
    public AssetEntry ImportFile(string path, AssetKind? kind = null, ImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (AssetFormats.GetModelFormat(path) == ModelFormat.SketchUp) throw new AssetImportException(AssetFormats.SketchUpMessage);
        var k = kind ?? AssetFormats.GuessKind(path);
        if (k is null)
        {
            ModelImporter.CheckExtension(path);
            throw new AssetImportException($"'{Path.GetFileName(path)}' is not a supported file type.");
        }
        return k.Value switch
        {
            AssetKind.Model => ImportModel(path, options),
            AssetKind.Image or AssetKind.Texture => ImportImage(path, k.Value),
            _ => ImportRaw(path, k.Value),
        };
    }

    /// <summary>
    /// Imports a 3D model with its companion files (see <see cref="ModelImporter"/>). The model is decoded immediately (to
    /// validate it and discover the files it needs) and the decoded result is cached; warnings and missing textures are
    /// reported as issues.
    /// </summary>
    public AssetEntry ImportModel(string path, ImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ModelImporter.CheckExtension(path);
        var id = Guid.NewGuid();
        var persistent = (options ?? ImportOptions.Default) with { TextureKeyPrefix = "" };
        var result = ModelImporter.Import(path, persistent with { TextureKeyPrefix = AssetKeys.TexturePrefix(id) });
        var companions = result.Files.Where(kv => kv.Key != result.MainFile).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var main = result.Files[result.MainFile];
        var hash = AssetEntry.ComputeHash(main, companions);
        lock (_gate)
        {
            foreach (var existingId in _order)
            {
                var e = _slots[existingId].Entry;
                if (e.Kind == AssetKind.Model && e.ContentHash == hash && Equals(e.ImportOptions, persistent)) return e;
            }
        }
        var entry = new AssetEntry(id, AssetKind.Model, Path.GetFileName(path), main, companions, Path.GetFullPath(path), persistent);
        var slot = new Slot(entry);
        slot.SetDecodedModel(result);
        ReportImport(entry, result);
        Insert(slot);
        return entry;
    }

    /// <summary>Imports an image (reference drawing, poster) or a texture; the header is validated, pixels decode lazily.</summary>
    public AssetEntry ImportImage(string path, AssetKind kind = AssetKind.Image)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (kind is not (AssetKind.Image or AssetKind.Texture)) throw new ArgumentOutOfRangeException(nameof(kind), "Images are Image or Texture assets.");
        var data = ReadFile(path);
        var hash = AssetEntry.ComputeHash(data);
        var existing = FindByContentHash(hash, kind);
        if (existing is not null) return existing;
        return Add(kind, Path.GetFileName(path), data, sourcePath: Path.GetFullPath(path));
    }

    /// <summary>
    /// Adds an asset from bytes (project loading, paste, drag and drop from another application). The content is validated
    /// cheaply (format/header) but not decoded. <paramref name="id"/> keeps the id stored in a project; a new id is
    /// created otherwise. Throws <see cref="AssetImportException"/> for invalid content and
    /// <see cref="InvalidOperationException"/> when the id is already used.
    /// </summary>
    public AssetEntry Add(AssetKind kind, string originalFileName, byte[] data, IReadOnlyDictionary<string, byte[]>? companions = null,
        string? sourcePath = null, Guid? id = null, ImportOptions? importOptions = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var name = string.IsNullOrWhiteSpace(originalFileName) ? "asset" : Path.GetFileName(originalFileName.Replace('\\', '/'));
        Validate(kind, name, data);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (companions is not null && kind == AssetKind.Model)
        {
            foreach (var (key, bytes) in companions)
            {
                var k = AssetKeys.NormalizeRelativePath(key);
                if (k.Length > 0 && bytes is not null) files[k] = bytes;
            }
        }
        var options = kind == AssetKind.Model ? (importOptions ?? ImportOptions.Default) with { TextureKeyPrefix = "" } : null;
        var entry = new AssetEntry(id ?? Guid.NewGuid(), kind, name, data, files, sourcePath, options);
        Insert(new Slot(entry));
        return entry;
    }

    /// <summary>Removes an asset; false when it does not exist.</summary>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_slots.Remove(id)) return false;
            _order.Remove(id);
        }
        AssetChanged?.Invoke(id);
        return true;
    }

    /// <summary>Removes all assets and issues.</summary>
    public void Clear()
    {
        Guid[] ids;
        lock (_gate)
        {
            ids = _order.ToArray();
            _slots.Clear();
            _order.Clear();
            _issues.Clear();
            _issueKeys.Clear();
        }
        foreach (var id in ids) AssetChanged?.Invoke(id);
    }

    /// <summary>Original bytes of the main file (shared; do not modify). Throws <see cref="KeyNotFoundException"/>.</summary>
    public byte[] GetBytes(Guid id) => GetSlot(id).Entry.Data;

    /// <summary>Companion files of a model asset keyed by relative path (empty for other kinds).</summary>
    public IReadOnlyDictionary<string, byte[]> GetCompanionFiles(Guid id) => GetSlot(id).Entry.CompanionFiles;

    /// <summary>
    /// Changes the import options of a model asset (axis/unit overrides). The decoded model and its textures are
    /// discarded and decoded again on next use. Returns the updated entry.
    /// </summary>
    public AssetEntry SetImportOptions(Guid id, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AssetEntry entry;
        lock (_gate)
        {
            if (!_slots.TryGetValue(id, out var slot)) throw new KeyNotFoundException($"No asset {id}.");
            if (slot.Entry.Kind != AssetKind.Model) throw new InvalidOperationException("Import options apply to model assets only.");
            entry = slot.Entry.WithImportOptions(options with { TextureKeyPrefix = "" });
            _slots[id] = new Slot(entry);
        }
        AssetChanged?.Invoke(id);
        return entry;
    }

    /// <summary>Drops every decoded model, image and texture (they decode again on demand).</summary>
    public void ClearDecodedCaches()
    {
        lock (_gate)
        {
            foreach (var id in _order) _slots[id] = new Slot(_slots[id].Entry);
        }
    }

    /// <summary>
    /// The decoded model of a model asset (decoded once and cached). Throws <see cref="KeyNotFoundException"/> for an
    /// unknown id and <see cref="AssetImportException"/> when the asset is not a model or cannot be decoded.
    /// </summary>
    public ImportedModel GetModel(Guid id)
    {
        if (TryGetModel(id, out var model, out var error)) return model;
        if (!Contains(id)) throw new KeyNotFoundException($"No asset {id}.");
        throw new AssetImportException(error ?? "The model could not be decoded.");
    }

    /// <summary>The decoded model, or false with a user-facing error (unknown id, not a model, decode failure).</summary>
    public bool TryGetModel(Guid id, [NotNullWhen(true)] out ImportedModel? model, out string? error)
    {
        model = null;
        var slot = FindSlot(id);
        if (slot is null)
        {
            error = $"Asset {id} does not exist.";
            return false;
        }
        if (slot.Entry.Kind != AssetKind.Model)
        {
            error = $"'{slot.Entry.OriginalFileName}' is not a 3D model.";
            return false;
        }
        var result = slot.ModelValue(this);
        model = result.Value;
        error = result.Error;
        return model is not null;
    }

    /// <summary>
    /// The decode result of a model asset when it is already known — decoded on use, attached with
    /// <see cref="SetDecodedModel"/>, or failed — without starting a decode: true with the model, or true with a null
    /// model and the user-facing reason it failed; false when the asset does not exist, is not a model, or has not been
    /// decoded yet. Lets a caller that decodes elsewhere (an isolated worker process) find out whether it still has to.
    /// </summary>
    public bool TryGetDecodedModel(Guid id, out ImportedModel? model, out string? error)
    {
        model = null;
        error = null;
        var slot = FindSlot(id);
        if (slot is null || slot.Entry.Kind != AssetKind.Model || !slot.TryPeekModel(out var result)) return false;
        model = result.Value;
        error = result.Error;
        return true;
    }

    /// <summary>
    /// Attaches a model decoded elsewhere — by an isolated import worker, from a disk cache, or by another importer such
    /// as the MVR reader — to model asset <paramref name="entry"/>, so <see cref="GetModel"/>, <see cref="TryGetModel"/>
    /// and the model's texture keys are served from it without decoding the file. Its texture keys should already be this
    /// store's (<c>asset:{id}/…</c>, <see cref="AssetKeys.TexturePrefix"/>). Returns false and changes nothing when the
    /// asset no longer exists, is not a model, or was replaced since <paramref name="entry"/> was read (its import options
    /// changed), so a late decode never overwrites a newer one.
    /// </summary>
    public bool SetDecodedModel(AssetEntry entry, ImportedModel model)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(model);
        var slot = FindSlot(entry.Id);
        if (slot is null || !ReferenceEquals(slot.Entry, entry) || entry.Kind != AssetKind.Model) return false;
        slot.SetDecodedModel(model);
        return true;
    }

    /// <summary>
    /// Records that model asset <paramref name="entry"/> cannot be decoded, with a user-facing <paramref name="error"/>
    /// (e.g. the import worker crashed on it), so it is not decoded again until the asset changes;
    /// <see cref="TryGetModel"/> then returns the error. Same conditions and return value as <see cref="SetDecodedModel"/>.
    /// </summary>
    public bool SetModelDecodeError(AssetEntry entry, string error)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(error);
        var slot = FindSlot(entry.Id);
        if (slot is null || !ReferenceEquals(slot.Entry, entry) || entry.Kind != AssetKind.Model) return false;
        slot.SetModelError(error);
        return true;
    }

    /// <summary>
    /// The decoded image of an image or texture asset (decoded once and cached). Throws <see cref="KeyNotFoundException"/>
    /// for an unknown id and <see cref="AssetImportException"/> when the asset is not an image or cannot be decoded.
    /// </summary>
    public ImageData GetImage(Guid id)
    {
        if (TryGetImage(id, out var image, out var error)) return image;
        if (!Contains(id)) throw new KeyNotFoundException($"No asset {id}.");
        throw new AssetImportException(error ?? "The image could not be decoded.");
    }

    /// <summary>The decoded image, or false with a user-facing error.</summary>
    public bool TryGetImage(Guid id, [NotNullWhen(true)] out ImageData? image, out string? error)
    {
        image = null;
        var slot = FindSlot(id);
        if (slot is null)
        {
            error = $"Asset {id} does not exist.";
            return false;
        }
        if (slot.Entry.Kind is not (AssetKind.Image or AssetKind.Texture))
        {
            error = $"'{slot.Entry.OriginalFileName}' is not an image.";
            return false;
        }
        var result = slot.ImageValue(this);
        image = result.Value;
        error = result.Error;
        return image is not null;
    }

    /// <summary>
    /// Resolves a texture key (see the class remarks). Returns null for unknown keys and undecodable images; the reason
    /// is recorded in <see cref="Issues"/>.
    /// </summary>
    public ImageData? Load(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith(AssetKeys.BuiltinScheme, StringComparison.Ordinal)) return Materials.LoadTexture(key);
        if (!AssetKeys.TryParseAssetKey(key, out var id, out var relative)) return null;
        var slot = FindSlot(id);
        if (slot is null)
        {
            Report(IssueSeverity.Warning, "asset.missing", $"A material refers to an asset that is not in the project ({key}).");
            return null;
        }
        if (relative is null) return slot.Entry.Kind is AssetKind.Image or AssetKind.Texture ? slot.ImageValue(this).Value : null;
        if (slot.Entry.Kind != AssetKind.Model) return null;
        return slot.Textures.GetOrAdd(relative, r => new Lazy<ImageData?>(() => ResolveModelTexture(slot, r), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private ImageData? ResolveModelTexture(Slot slot, string relative)
    {
        var entry = slot.Entry;
        byte[]? bytes = null;
        if (entry.CompanionFiles.TryGetValue(relative, out var direct)) bytes = direct;
        else
        {
            foreach (var (k, v) in entry.CompanionFiles)
            {
                if (string.Equals(k, relative, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = v;
                    break;
                }
            }
        }
        if (bytes is null)
        {
            // Embedded and generated images exist only in the decoded model.
            var model = slot.ModelValue(this).Value;
            if (model is null) return null;
            if (model.GeneratedImages.TryGetValue(relative, out var generated)) return generated;
            if (model.EmbeddedImages.TryGetValue(relative, out var embedded)) bytes = embedded;
            else if (model.Files.TryGetValue(relative, out var file)) bytes = file;
        }
        if (bytes is null)
        {
            Report(IssueSeverity.Warning, "asset.texture-missing", $"'{entry.OriginalFileName}': texture '{relative}' is missing.");
            return null;
        }
        if (ImageImporter.TryDecode(bytes, out var image, out var error)) return image;
        Report(IssueSeverity.Warning, "asset.texture-invalid", $"'{entry.OriginalFileName}': texture '{relative}' cannot be decoded: {error}");
        return null;
    }

    private Result<ImportedModel> DecodeModel(AssetEntry entry)
    {
        try
        {
            var options = (entry.ImportOptions ?? ImportOptions.Default) with { TextureKeyPrefix = AssetKeys.TexturePrefix(entry.Id) };
            var model = ModelImporter.Import(entry.OriginalFileName, entry.Data, entry.CompanionFiles, options);
            ReportImport(entry, model);
            return new Result<ImportedModel>(model, null);
        }
        catch (AssetImportException e)
        {
            Report(IssueSeverity.Error, "asset.decode-failed", $"'{entry.OriginalFileName}' cannot be loaded: {e.Message}");
            return new Result<ImportedModel>(null, e.Message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A bug or a native failure inside a reader must not take the whole project down.
            var message = $"Unexpected error while reading the model: {e.Message}";
            Report(IssueSeverity.Error, "asset.decode-failed", $"'{entry.OriginalFileName}' cannot be loaded: {message}");
            return new Result<ImportedModel>(null, message);
        }
    }

    private Result<ImageData> DecodeImage(AssetEntry entry)
    {
        if (ImageImporter.TryDecode(entry.Data, out var image, out var error)) return new Result<ImageData>(image, null);
        Report(IssueSeverity.Error, "asset.decode-failed", $"'{entry.OriginalFileName}' cannot be loaded: {error}");
        return new Result<ImageData>(null, error);
    }

    private void ReportImport(AssetEntry entry, ImportedModel model)
    {
        foreach (var w in model.Warnings) Report(IssueSeverity.Note, "asset.import-note", $"'{entry.OriginalFileName}': {w}");
        foreach (var m in model.MissingFiles)
            Report(IssueSeverity.Warning, "asset.file-missing", $"'{entry.OriginalFileName}': '{m}' was not found when the model was imported.");
    }

    private void Report(IssueSeverity severity, string code, string message)
    {
        // Issue.Entities lists scene entities; the engine relates asset problems to the entities that use the asset.
        var issue = new Issue(severity, IssueCategory.Scene, code, message, Array.Empty<Guid>());
        lock (_gate)
        {
            if (!_issueKeys.Add(code + "\n" + message)) return;
            _issues.Add(issue);
        }
        IssueReported?.Invoke(issue);
    }

    private static void Validate(AssetKind kind, string name, byte[] data)
    {
        switch (kind)
        {
            case AssetKind.Model:
                if (AssetFormats.IsSketchUp(name, data)) throw new AssetImportException(AssetFormats.SketchUpMessage);
                ModelImporter.CheckExtension(name);
                if (data.Length == 0) throw new AssetImportException($"'{name}' is empty.");
                break;
            case AssetKind.Image:
            case AssetKind.Texture:
                if (ImageImporter.Identify(data) is null) throw new AssetImportException($"'{name}': {ImageImporter.DescribeUnsupported(data)}");
                break;
            case AssetKind.Gdtf:
                if (data.Length < 4 || data[0] != (byte)'P' || data[1] != (byte)'K')
                    throw new AssetImportException($"'{name}' is not a GDTF file (GDTF files are zip archives).");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private AssetEntry ImportRaw(string path, AssetKind kind)
    {
        var data = ReadFile(path);
        var existing = FindByContentHash(AssetEntry.ComputeHash(data), kind);
        return existing ?? Add(kind, Path.GetFileName(path), data, sourcePath: Path.GetFullPath(path));
    }

    private static byte[] ReadFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new AssetImportException($"Cannot read '{Path.GetFileName(path)}': {e.Message}", e);
        }
    }

    private void Insert(Slot slot)
    {
        lock (_gate)
        {
            if (_slots.ContainsKey(slot.Entry.Id)) throw new InvalidOperationException($"An asset with id {slot.Entry.Id} already exists.");
            _slots[slot.Entry.Id] = slot;
            _order.Add(slot.Entry.Id);
        }
        AssetChanged?.Invoke(slot.Entry.Id);
    }

    private Slot? FindSlot(Guid id)
    {
        lock (_gate) return _slots.TryGetValue(id, out var s) ? s : null;
    }

    private Slot GetSlot(Guid id) => FindSlot(id) ?? throw new KeyNotFoundException($"No asset {id}.");

    private readonly record struct Result<T>(T? Value, string? Error) where T : class;

    /// <summary>An entry plus its decode caches; replaced as a whole when the entry changes.</summary>
    private sealed class Slot
    {
        private Lazy<Result<ImportedModel>>? _model;
        private Lazy<Result<ImageData>>? _image;

        public Slot(AssetEntry entry)
        {
            Entry = entry;
        }

        public AssetEntry Entry { get; }

        public ConcurrentDictionary<string, Lazy<ImageData?>> Textures { get; } = new(StringComparer.Ordinal);

        /// <summary>Seeds the model cache with a model decoded during import or elsewhere.</summary>
        public void SetDecodedModel(ImportedModel model) =>
            Volatile.Write(ref _model, new Lazy<Result<ImportedModel>>(new Result<ImportedModel>(model, null)));

        /// <summary>Seeds the model cache with a decode failure.</summary>
        public void SetModelError(string error) =>
            Volatile.Write(ref _model, new Lazy<Result<ImportedModel>>(new Result<ImportedModel>(null, error)));

        /// <summary>The model decode result if it is already known (no decoding is started).</summary>
        public bool TryPeekModel(out Result<ImportedModel> result)
        {
            var lazy = Volatile.Read(ref _model);
            if (lazy is { IsValueCreated: true })
            {
                result = lazy.Value;
                return true;
            }
            result = default;
            return false;
        }

        public Result<ImportedModel> ModelValue(AssetStore store)
        {
            var lazy = Volatile.Read(ref _model);
            if (lazy is null)
            {
                var created = new Lazy<Result<ImportedModel>>(() => store.DecodeModel(Entry), LazyThreadSafetyMode.ExecutionAndPublication);
                lazy = Interlocked.CompareExchange(ref _model, created, null) ?? created;
            }
            return lazy.Value;
        }

        public Result<ImageData> ImageValue(AssetStore store)
        {
            var lazy = Volatile.Read(ref _image);
            if (lazy is null)
            {
                var created = new Lazy<Result<ImageData>>(() => store.DecodeImage(Entry), LazyThreadSafetyMode.ExecutionAndPublication);
                lazy = Interlocked.CompareExchange(ref _image, created, null) ?? created;
            }
            return lazy.Value;
        }
    }
}
