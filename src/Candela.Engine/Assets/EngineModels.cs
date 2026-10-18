using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.Serialization;

namespace Candela.Engine;

/// <summary>
/// Decoded 3D models of the project's model assets, as the engine uses them (rendering, bounds, MVR export). Models in
/// formats read by native code (OBJ, FBX, COLLADA, 3DS) are decoded by the isolated <see cref="ImportWorker"/>; the
/// managed formats (glTF/GLB, STL, PLY) through the <see cref="AssetStore"/>. The decoded model lives in the asset
/// store (<see cref="AssetStore.SetDecodedModel"/>; a worker failure is recorded there too) and on disk per content
/// (<c>{cache}/models/*.cmodel</c>), so a model passes through the native importer at most once per machine.
/// </summary>
/// <remarks>
/// Texture keys of the returned models are <c>asset:{id}/{relative path}</c>, resolved by <see cref="EngineImageSource"/>
/// (which reads embedded and generated images from these decoded models, never by decoding the native file again).
/// Thread-safe; a model is decoded once even under concurrent requests (decodes in flight are shared until their result
/// is in the store).
/// </remarks>
public sealed class EngineModels
{
    private readonly Func<AssetStore> _store;
    private readonly string? _cacheDirectory;
    private readonly Func<string?> _workerPath;
    private readonly ConcurrentDictionary<Guid, Lazy<Decoded>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Issue> _issues = new(StringComparer.Ordinal);

    /// <summary>Creates the service over <paramref name="store"/>.</summary>
    /// <param name="store">Returns the open project's assets (the store is replaced with the project).</param>
    /// <param name="cacheDirectory">Folder of the on-disk cache (null: no disk cache); temporary worker files go below it too.</param>
    /// <param name="workerPath">Returns the worker program, or null when none can be found.</param>
    /// <param name="isolateNativeImports">False decodes native formats in-process (trusted files only).</param>
    /// <param name="timeout">Longest time one worker import may take.</param>
    public EngineModels(Func<AssetStore> store, string? cacheDirectory, Func<string?> workerPath, bool isolateNativeImports, TimeSpan timeout)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cacheDirectory = cacheDirectory;
        _workerPath = workerPath ?? throw new ArgumentNullException(nameof(workerPath));
        IsolateNativeImports = isolateNativeImports;
        Timeout = timeout;
    }

    /// <summary>True when native formats are decoded in the worker process.</summary>
    public bool IsolateNativeImports { get; }

    /// <summary>Longest time one worker import may take.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Problems met while decoding (engine.import-unsafe, engine.import-in-process, engine.asset-unreadable).</summary>
    public IReadOnlyCollection<Issue> Issues => _issues.Values.ToArray();

    /// <summary>Raised (on the decoding thread) when a new problem is recorded.</summary>
    public event Action<Issue>? IssueReported;

    /// <summary>
    /// The decoded model of a model asset, or false with a user-facing reason (not a model, missing, could not be
    /// imported safely, damaged). Decodes on the calling thread when the model is not decoded yet — through the worker
    /// for native formats, which can take up to <see cref="Timeout"/>; <see cref="DecodeAsync"/> does it in the background.
    /// </summary>
    public bool TryGet(Guid assetId, [NotNullWhen(true)] out ImportedModel? model, out string? error)
    {
        var store = _store();
        var entry = store.Find(assetId);
        if (entry is null)
        {
            model = null;
            error = $"The project has no asset {assetId}.";
            return false;
        }
        if (entry.Kind != AssetKind.Model)
        {
            model = null;
            error = $"'{entry.OriginalFileName}' is not a 3D model.";
            return false;
        }
        if (store.TryGetDecodedModel(assetId, out model, out error)) return model is not null;
        if (!NeedsWorker(entry)) return store.TryGetModel(assetId, out model, out error);

        var lazy = _inFlight.GetOrAdd(assetId, _ => new Lazy<Decoded>(() => Decode(entry), LazyThreadSafetyMode.ExecutionAndPublication));
        var decoded = lazy.Value;
        bool current = decoded.Model is not null ? store.SetDecodedModel(decoded.Entry, decoded.Model) : store.SetModelDecodeError(decoded.Entry, decoded.Error!);
        _inFlight.TryRemove(new KeyValuePair<Guid, Lazy<Decoded>>(assetId, lazy));
        // the asset was replaced while it decoded (new import options): decode the new one
        if (!current || !ReferenceEquals(decoded.Entry, entry)) return TryGet(assetId, out model, out error);
        model = decoded.Model;
        error = decoded.Error;
        return model is not null;
    }

    /// <summary>
    /// True when the model of <paramref name="assetId"/> is decoded (or known to fail): <see cref="TryGet"/> returns at
    /// once, without decoding.
    /// </summary>
    public bool IsDecoded(Guid assetId) => _store().TryGetDecodedModel(assetId, out _, out _);

    /// <summary>
    /// Decodes the model of <paramref name="assetId"/> on a background thread (a no-op task when it is decoded already);
    /// the task's result is <see cref="TryGet"/>'s. Lets the renderer binding keep drawing frames while the import worker
    /// runs.
    /// </summary>
    public Task<bool> DecodeAsync(Guid assetId)
    {
        if (IsDecoded(assetId)) return Task.FromResult(_store().TryGetDecodedModel(assetId, out var m, out _) && m is not null);
        return Task.Run(() =>
        {
            try
            {
                return TryGet(assetId, out _, out _);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Record(new Issue(IssueSeverity.Error, IssueCategory.Scene, EngineIssueCodes.AssetUnreadable,
                    $"The model {assetId} cannot be loaded: {e.Message}"));
                return false;
            }
        });
    }

    /// <summary>The decoded model data, or null (see <see cref="TryGet"/>).</summary>
    public ModelData? GetModelData(Guid assetId) => TryGet(assetId, out var m, out _) ? m.Model : null;

    /// <summary>Local bounds of a model asset, or null when it cannot be decoded.</summary>
    public Aabb? GetBounds(Guid assetId)
    {
        if (!TryGet(assetId, out var m, out _)) return null;
        var b = m.Model.ComputeBounds();
        return b.IsEmpty ? null : b;
    }

    /// <summary>The decoded model if it is already in memory (no decoding is started).</summary>
    internal bool TryGetLoaded(Guid assetId, [NotNullWhen(true)] out ImportedModel? model) =>
        _store().TryGetDecodedModel(assetId, out model, out _) && model is not null;

    /// <summary>
    /// Imports a model file into the store: native formats through the worker (falling back to in-process only when no
    /// worker exists, reported as <see cref="EngineIssueCodes.ImportInProcess"/>), managed formats directly. Throws
    /// <see cref="AssetImportException"/> with a user-facing message when the file cannot be imported (including
    /// "could not be imported safely" after a worker crash or timeout).
    /// </summary>
    internal AssetEntry Import(string path, ImportOptions? options, out IReadOnlyList<Issue> issues)
    {
        var found = new List<Issue>();
        issues = found;
        var store = _store();
        if (!IsolateNativeImports || !ImportWorker.UsesNativeImporter(path)) return store.ImportModel(path, options);

        string? worker = _workerPath();
        if (worker is null)
        {
            found.Add(Note(EngineIssueCodes.ImportInProcess,
                $"'{Path.GetFileName(path)}' was imported inside the application because the import worker (candela) was not found."));
            return store.ImportModel(path, options);
        }

        var result = ImportWorker.Run(worker, path, options, Timeout, TempRoot());
        switch (result.Status)
        {
            case ImportWorkerStatus.Succeeded:
                break;
            case ImportWorkerStatus.Rejected:
                throw new AssetImportException(result.Message);
            case ImportWorkerStatus.Unavailable:
                found.Add(Note(EngineIssueCodes.ImportInProcess,
                    $"'{Path.GetFileName(path)}' was imported inside the application: {result.Message}."));
                return store.ImportModel(path, options);
            default:
                var unsafeIssue = Unsafe(Path.GetFileName(path), result.Message);
                found.Add(unsafeIssue);
                throw new AssetImportException(unsafeIssue.Message);
        }

        var imported = result.Model!;
        var main = imported.Files[imported.MainFile];
        var companions = imported.Files.Where(kv => kv.Key != imported.MainFile)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var persistent = (options ?? ImportOptions.Default) with { TextureKeyPrefix = "" };
        string hash = AssetEntry.ComputeHash(main, companions);
        foreach (var existing in store.Entries)
            if (existing.Kind == AssetKind.Model && existing.ContentHash == hash && Equals(existing.ImportOptions, persistent)) return existing;

        var entry = store.Add(AssetKind.Model, Path.GetFileName(path), main, companions, Path.GetFullPath(path), importOptions: persistent);
        SaveToDiskCache(entry, imported);
        foreach (var w in imported.Warnings) found.Add(new Issue(IssueSeverity.Note, IssueCategory.Scene, "asset.import-note", $"'{entry.OriginalFileName}': {w}"));
        foreach (var m in imported.MissingFiles)
            found.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, "asset.file-missing",
                $"'{entry.OriginalFileName}': '{m}' was not found when the model was imported."));
        store.SetDecodedModel(entry, WithPrefix(imported, entry.Id));
        return entry;
    }

    /// <summary>
    /// Uses a model another importer already decoded (the MVR importer's managed 3DS/glTF readers) for an asset, in
    /// memory and in the disk cache, so the asset is not decoded a second time.
    /// </summary>
    internal void Seed(AssetEntry entry, ModelData model, IReadOnlyDictionary<string, byte[]>? companions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(model);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [entry.OriginalFileName] = entry.Data };
        if (companions is not null)
            foreach (var (k, v) in companions)
                if (!string.IsNullOrEmpty(k)) files.TryAdd(AssetKeys.NormalizeRelativePath(k), v);
        var options = entry.ImportOptions ?? ImportOptions.Default;
        var imported = new ImportedModel
        {
            Model = model,
            Format = AssetFormats.GetModelFormat(entry.OriginalFileName),
            MainFile = entry.OriginalFileName,
            Files = files,
            EmbeddedImages = new Dictionary<string, byte[]>(StringComparer.Ordinal),
            GeneratedImages = new Dictionary<string, ImageData>(StringComparer.Ordinal),
            MissingFiles = Array.Empty<string>(),
            Warnings = Array.Empty<string>(),
            SourceUpAxis = options.UpAxis,
            UnitScale = options.UnitScale ?? 1f,
            UnitsKnown = options.UnitScale is not null,
        };
        SaveToDiskCache(entry, imported);
        _store().SetDecodedModel(entry, WithPrefix(imported, entry.Id));
    }

    /// <summary>Forgets the decoding problems and decodes in flight (scene replaced; its new asset store holds no decoded models).</summary>
    public void Clear()
    {
        _inFlight.Clear();
        _issues.Clear();
    }

    /// <summary>
    /// Forgets a decode in flight for one asset (its import options changed; the store dropped its decoded model with the
    /// old entry, so the next request decodes the new one).
    /// </summary>
    public void Invalidate(Guid assetId) => _inFlight.TryRemove(assetId, out _);

    /// <summary>True when the asset's format is read by native code and is therefore decoded in the worker process.</summary>
    private bool NeedsWorker(AssetEntry entry) => IsolateNativeImports && ImportWorker.UsesNativeImporter(entry.OriginalFileName);

    /// <summary>Decodes a native-format model: the disk cache, else the worker (in-process only when there is no worker).</summary>
    private Decoded Decode(AssetEntry entry)
    {
        if (LoadFromDiskCache(entry) is { } cached) return new Decoded(entry, WithPrefix(cached, entry.Id), null);

        string? worker = _workerPath();
        if (worker is null)
        {
            Record(Note(EngineIssueCodes.ImportInProcess,
                $"'{entry.OriginalFileName}' was decoded inside the application because the import worker (candela) was not found."));
            return _store().TryGetModel(entry.Id, out var m, out var error)
                ? new Decoded(entry, m, null)
                : Failed(entry, error ?? "the model cannot be decoded");
        }

        // The worker reads files: write the stored model and its companion files to a private temporary folder.
        string folder = Path.Combine(TempRoot(), "asset-" + Guid.NewGuid().ToString("N"));
        try
        {
            string main = WriteFiles(folder, entry);
            var result = ImportWorker.Run(worker, main, entry.ImportOptions, Timeout, TempRoot());
            switch (result.Status)
            {
                case ImportWorkerStatus.Succeeded:
                    SaveToDiskCache(entry, result.Model!);
                    return new Decoded(entry, WithPrefix(result.Model!, entry.Id), null);
                case ImportWorkerStatus.Rejected:
                    return Failed(entry, result.Message);
                case ImportWorkerStatus.Unavailable:
                    Record(Note(EngineIssueCodes.ImportInProcess, $"'{entry.OriginalFileName}' was decoded inside the application: {result.Message}."));
                    return _store().TryGetModel(entry.Id, out var m, out var error)
                        ? new Decoded(entry, m, null)
                        : Failed(entry, error ?? "the model cannot be decoded");
                default:
                    var issue = Unsafe(entry.OriginalFileName, result.Message);
                    Record(issue);
                    return new Decoded(entry, null, issue.Message);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(entry, $"the model could not be prepared for import: {e.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left for the OS to clean.
            }
        }
    }

    private static string WriteFiles(string folder, AssetEntry entry)
    {
        Directory.CreateDirectory(folder);
        string root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        string mainName = SafeFileName(entry.OriginalFileName);
        string main = Path.Combine(folder, mainName);
        File.WriteAllBytes(main, entry.Data);
        foreach (var (relative, bytes) in entry.CompanionFiles)
        {
            string normalized = AssetKeys.NormalizeRelativePath(relative);
            if (normalized.Length == 0) continue;
            string path = Path.GetFullPath(Path.Combine(folder, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        return main;
    }

    private static string SafeFileName(string name)
    {
        string file = Path.GetFileName(name.Replace('\\', '/').Split('/').LastOrDefault() ?? "");
        foreach (char c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        return string.IsNullOrWhiteSpace(file) || file is "." or ".." ? "model" + Path.GetExtension(name) : file;
    }

    private Decoded Failed(AssetEntry entry, string error)
    {
        Record(new Issue(IssueSeverity.Error, IssueCategory.Scene, EngineIssueCodes.AssetUnreadable,
            $"The model '{entry.OriginalFileName}' cannot be loaded: {error}"));
        return new Decoded(entry, null, error);
    }

    private static Issue Note(string code, string message) => new(IssueSeverity.Note, IssueCategory.Program, code, message);

    private static Issue Unsafe(string file, string why) => new(IssueSeverity.Error, IssueCategory.Scene, EngineIssueCodes.ImportUnsafe,
        $"'{file}' could not be imported safely ({why}).");

    private void Record(Issue issue)
    {
        if (_issues.TryAdd(issue.Code + "\n" + issue.Message, issue)) IssueReported?.Invoke(issue);
    }

    private string TempRoot()
    {
        string root = Path.Combine(_cacheDirectory ?? Path.GetTempPath(), "import-tmp");
        Directory.CreateDirectory(root);
        return root;
    }

    private string? CachePath(AssetEntry entry)
    {
        if (_cacheDirectory is null) return null;
        var options = (entry.ImportOptions ?? ImportOptions.Default) with { TextureKeyPrefix = "" };
        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CandelaJson.Serialize(options, indented: false))))[..12].ToLowerInvariant();
        return Path.Combine(_cacheDirectory, "models", entry.ContentHash[..2], $"{entry.ContentHash}_{signature}{ModelPackage.Extension}");
    }

    private ImportedModel? LoadFromDiskCache(AssetEntry entry)
    {
        string? path = CachePath(entry);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return ModelPackage.ReadFile(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                // A damaged cache file that cannot be deleted is simply not used.
            }
            return null;
        }
    }

    private void SaveToDiskCache(AssetEntry entry, ImportedModel model)
    {
        string? path = CachePath(entry);
        if (path is null) return;
        try
        {
            ModelPackage.WriteFile(model, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation: a full or read-only disk only costs another worker run next time.
        }
    }

    /// <summary>
    /// A copy of <paramref name="model"/> whose materials use texture keys of asset <paramref name="id"/> (the worker
    /// and the disk cache keep keys relative, so one cached decode serves any asset id).
    /// </summary>
    private static ImportedModel WithPrefix(ImportedModel model, Guid id)
    {
        string prefix = AssetKeys.TexturePrefix(id);
        var copy = new ModelData { Name = model.Model.Name };
        copy.Parts.AddRange(model.Model.Parts);
        foreach (var m in model.Model.Materials)
        {
            var c = CandelaJson.DeepClone(m);
            c.BaseColorTexture = Prefix(prefix, c.BaseColorTexture);
            c.NormalTexture = Prefix(prefix, c.NormalTexture);
            c.MetallicRoughnessTexture = Prefix(prefix, c.MetallicRoughnessTexture);
            c.OcclusionTexture = Prefix(prefix, c.OcclusionTexture);
            c.EmissiveTexture = Prefix(prefix, c.EmissiveTexture);
            copy.Materials.Add(c);
        }
        return new ImportedModel
        {
            Model = copy,
            Format = model.Format,
            MainFile = model.MainFile,
            Files = model.Files,
            EmbeddedImages = model.EmbeddedImages,
            GeneratedImages = model.GeneratedImages,
            MissingFiles = model.MissingFiles,
            Warnings = model.Warnings,
            SourceUpAxis = model.SourceUpAxis,
            UnitScale = model.UnitScale,
            UnitsKnown = model.UnitsKnown,
        };
    }

    private static string? Prefix(string prefix, string? key)
    {
        if (string.IsNullOrEmpty(key) || key.StartsWith(AssetKeys.AssetScheme, StringComparison.Ordinal)
            || key.StartsWith(AssetKeys.BuiltinScheme, StringComparison.Ordinal)) return key;
        return prefix + AssetKeys.NormalizeRelativePath(key);
    }

    private sealed record Decoded(AssetEntry Entry, ImportedModel? Model, string? Error);
}
