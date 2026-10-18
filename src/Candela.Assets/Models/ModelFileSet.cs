using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Candela.Assets;

/// <summary>
/// The files a model consists of — the main file plus companions (MTL, .bin buffers, textures) — addressed by
/// normalised relative paths (<see cref="AssetKeys.NormalizeRelativePath"/>). Two flavours:
/// <list type="bullet">
/// <item><see cref="FromFile"/>: reads companions lazily from disk around the model and records every file it serves,
/// so an import discovers exactly the files a project has to keep.</item>
/// <item><see cref="FromMemory"/>: serves files kept in a project (no disk access).</item>
/// </list>
/// References are resolved the same way in both, so a model imported from disk and later from the project archive
/// produces the same texture keys: relative references against the main file's folder (".." cannot leave the set),
/// absolute references (other machines' "C:\…" paths) by file name, then any file of the same name (case-insensitive).
/// Thread-safe.
/// </summary>
public sealed class ModelFileSet
{
    /// <summary>Companion files larger than this are not read (1 GiB).</summary>
    public const long MaxFileSize = 1L << 30;

    private const int MaxIndexedFiles = 20_000;
    private const int MaxSearchDepth = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _caseInsensitive = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missing = new();
    private readonly string? _rootDirectory;
    private readonly string _mainDirectory;
    private Dictionary<string, List<string>>? _diskNameIndex;

    private ModelFileSet(string mainFile, byte[] mainData, string? rootDirectory, string? sourcePath)
    {
        MainFile = mainFile;
        SourcePath = sourcePath;
        _rootDirectory = rootDirectory;
        int slash = mainFile.LastIndexOf('/');
        _mainDirectory = slash < 0 ? "" : mainFile[..slash];
        Add(mainFile, mainData);
    }

    /// <summary>Normalised relative path of the main file.</summary>
    public string MainFile { get; }

    /// <summary>Full path of the main file on disk (null for in-memory sets).</summary>
    public string? SourcePath { get; }

    /// <summary>Bytes of the main file.</summary>
    public byte[] MainData
    {
        get
        {
            lock (_gate) return _files[MainFile];
        }
    }

    /// <summary>Snapshot of every file served so far (for disk sets) or held (for memory sets), main file included.</summary>
    public IReadOnlyDictionary<string, byte[]> Files
    {
        get
        {
            lock (_gate) return new Dictionary<string, byte[]>(_files, StringComparer.Ordinal);
        }
    }

    /// <summary>References that could not be resolved, in the order they were first asked for.</summary>
    public IReadOnlyList<string> MissingFiles
    {
        get
        {
            lock (_gate) return _missing.ToArray();
        }
    }

    /// <summary>
    /// A set rooted at the folder of <paramref name="path"/>. Throws <see cref="AssetImportException"/> when the file
    /// cannot be read.
    /// </summary>
    public static ModelFileSet FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full;
        byte[] data;
        try
        {
            full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists) throw new AssetImportException($"File not found: '{Path.GetFileName(path)}'.");
            if (info.Length > MaxFileSize) throw new AssetImportException($"'{info.Name}' is too large to import ({info.Length / (1 << 20)} MiB).");
            data = File.ReadAllBytes(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new AssetImportException($"Cannot read '{Path.GetFileName(path)}': {e.Message}", e);
        }
        string root = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        return new ModelFileSet(Path.GetFileName(full), data, root, full);
    }

    /// <summary>
    /// A set held in memory: the main file and optional companions keyed by relative path (as stored in a project).
    /// </summary>
    public static ModelFileSet FromMemory(string mainFile, byte[] mainData, IReadOnlyDictionary<string, byte[]>? companions = null)
    {
        ArgumentNullException.ThrowIfNull(mainData);
        var main = AssetKeys.NormalizeRelativePath(mainFile ?? "");
        if (main.Length == 0) main = "model";
        var set = new ModelFileSet(main, mainData, null, null);
        if (companions is not null)
        {
            foreach (var (key, bytes) in companions)
            {
                var k = AssetKeys.NormalizeRelativePath(key);
                if (k.Length == 0 || bytes is null || set._files.ContainsKey(k)) continue;
                set.Add(k, bytes);
            }
        }
        return set;
    }

    /// <summary>
    /// Resolves a reference found in the model (texture path, MTL name, glTF URI) to a file of the set. Returns the
    /// normalised key under which the file is stored and its bytes. Failed lookups are remembered in
    /// <see cref="MissingFiles"/>.
    /// </summary>
    public bool TryRead(string reference, [NotNullWhen(true)] out string? key, [NotNullWhen(true)] out byte[]? data)
    {
        key = null;
        data = null;
        if (string.IsNullOrWhiteSpace(reference)) return false;
        lock (_gate)
        {
            if (TryResolve(reference.Trim(), out key, out data)) return true;
            // AssimpNet marshals some native strings with the ANSI code page; recover UTF-8 names mangled that way.
            var repaired = RepairMojibake(reference.Trim());
            if (repaired is not null && TryResolve(repaired, out key, out data)) return true;
            if (!_missing.Contains(reference, StringComparer.Ordinal)) _missing.Add(reference);
            return false;
        }
    }

    /// <summary>True when the reference resolves (does not record a miss).</summary>
    public bool Contains(string reference)
    {
        lock (_gate) return TryResolve(reference.Trim(), out _, out _);
    }

    private bool TryResolve(string reference, [NotNullWhen(true)] out string? key, [NotNullWhen(true)] out byte[]? data)
    {
        key = null;
        data = null;
        var cleaned = reference.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? reference[7..] : reference;
        cleaned = cleaned.Replace('\\', '/');
        if (cleaned.Length == 0) return false;
        bool absolute = IsAbsolute(cleaned);
        string fileName = cleaned[(cleaned.LastIndexOf('/') + 1)..];
        if (fileName.Length == 0) return false;

        // 1. Already known files (exact, then case-insensitive).
        if (!absolute)
        {
            var nearMain = AssetKeys.NormalizeRelativePath(_mainDirectory.Length == 0 ? cleaned : _mainDirectory + "/" + cleaned);
            var fromRoot = AssetKeys.NormalizeRelativePath(cleaned);
            if (TryKnown(nearMain, out key, out data) || TryKnown(fromRoot, out key, out data)) return true;
            if (_rootDirectory is not null)
            {
                if (cleaned.Contains("..", StringComparison.Ordinal))
                {
                    // Paths that climb above the model folder ("../textures/a.png") are read where they point to but
                    // stored under the clamped key, which is how they are found again in memory.
                    if (TryGetFullPath(Path.Combine(_rootDirectory, _mainDirectory, cleaned), out var climbing)
                        && TryDisk(climbing, nearMain, out key, out data)) return true;
                }
                else if (TryDisk(Path.Combine(_rootDirectory, nearMain), nearMain, out key, out data)) return true;
                if (fromRoot != nearMain && TryDisk(Path.Combine(_rootDirectory, fromRoot), fromRoot, out key, out data)) return true;
            }
        }
        else if (_rootDirectory is not null)
        {
            // An absolute path that exists on this machine (the model was exported here).
            if (TryGetFullPath(cleaned, out var local) && TryDisk(local, KeyForDiskFile(local), out key, out data)) return true;
        }

        // 2. By file name anywhere in the set.
        if (TryByName(fileName, out key, out data)) return true;
        if (_rootDirectory is not null)
        {
            var index = DiskNameIndex();
            if (index.TryGetValue(fileName, out var hits))
            {
                foreach (var rel in hits)
                {
                    if (TryDisk(Path.Combine(_rootDirectory, rel), rel, out key, out data)) return true;
                }
            }
        }
        return false;
    }

    private bool TryKnown(string normalized, [NotNullWhen(true)] out string? key, [NotNullWhen(true)] out byte[]? data)
    {
        key = null;
        data = null;
        if (normalized.Length == 0) return false;
        if (_files.TryGetValue(normalized, out data))
        {
            key = normalized;
            return true;
        }
        if (_caseInsensitive.TryGetValue(normalized, out var actual) && _files.TryGetValue(actual, out data))
        {
            key = actual;
            return true;
        }
        return false;
    }

    private bool TryByName(string fileName, [NotNullWhen(true)] out string? key, [NotNullWhen(true)] out byte[]? data)
    {
        key = null;
        data = null;
        string? best = null;
        int bestRank = int.MaxValue;
        foreach (var k in _files.Keys)
        {
            var name = k[(k.LastIndexOf('/') + 1)..];
            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            // Deterministic choice: exact case first, then the shortest path, then ordinal order.
            int rank = (string.Equals(name, fileName, StringComparison.Ordinal) ? 0 : 1 << 20) + k.Length;
            if (rank < bestRank || (rank == bestRank && string.CompareOrdinal(k, best) < 0))
            {
                best = k;
                bestRank = rank;
            }
        }
        if (best is null) return false;
        key = best;
        data = _files[best];
        return true;
    }

    private static bool TryGetFullPath(string path, [NotNullWhen(true)] out string? full)
    {
        try
        {
            full = Path.GetFullPath(path);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = null;
            return false;
        }
    }

    private bool TryDisk(string fullPath, string preferredKey, [NotNullWhen(true)] out string? key, [NotNullWhen(true)] out byte[]? data)
    {
        key = null;
        data = null;
        if (preferredKey.Length == 0) return false;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaxFileSize) return false;
            if (TryKnown(preferredKey, out key, out data)) return true;
            // Outside the model's folder only images are read (a crafted model must not pull arbitrary local files into a
            // project that is later shared).
            bool outside = !IsInsideRoot(info.FullName);
            if (outside && !AssetFormats.IsImageFile(info.Name)) return false;
            data = File.ReadAllBytes(info.FullName);
            if (outside && ImageImporter.DetectFormat(data) is null)
            {
                data = null;
                return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            data = null;
            return false;
        }
        key = preferredKey;
        Add(key, data);
        return true;
    }

    /// <summary>
    /// Forgets a file that was read but turned out not to be needed (e.g. a "texture" that is no image), so it is not
    /// stored with the asset. The main file is never removed.
    /// </summary>
    internal void Discard(string key)
    {
        lock (_gate)
        {
            if (string.Equals(key, MainFile, StringComparison.Ordinal) || !_files.Remove(key)) return;
            _caseInsensitive.Remove(key);
            foreach (var other in _files.Keys)
            {
                if (string.Equals(other, key, StringComparison.OrdinalIgnoreCase))
                {
                    _caseInsensitive[key] = other;
                    break;
                }
            }
        }
    }

    private bool IsInsideRoot(string fullPath)
    {
        if (_rootDirectory is null) return false;
        var rel = Path.GetRelativePath(_rootDirectory, fullPath);
        return !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !rel.StartsWith("../", StringComparison.Ordinal);
    }

    private string KeyForDiskFile(string fullPath)
    {
        if (_rootDirectory is not null)
        {
            var rel = Path.GetRelativePath(_rootDirectory, fullPath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return AssetKeys.NormalizeRelativePath(rel);
        }
        return Path.GetFileName(fullPath);
    }

    private Dictionary<string, List<string>> DiskNameIndex()
    {
        if (_diskNameIndex is not null) return _diskNameIndex;
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_rootDirectory is not null)
        {
            int count = 0;
            var pending = new Queue<(string Dir, int Depth)>();
            pending.Enqueue((_rootDirectory, 0));
            while (pending.Count > 0 && count < MaxIndexedFiles)
            {
                var (dir, depth) = pending.Dequeue();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (++count > MaxIndexedFiles) break;
                        var name = Path.GetFileName(file);
                        if (!index.TryGetValue(name, out var list)) index[name] = list = new List<string>(1);
                        list.Add(AssetKeys.NormalizeRelativePath(Path.GetRelativePath(_rootDirectory, file)));
                    }
                    if (depth < MaxSearchDepth)
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal)) pending.Enqueue((sub, depth + 1));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Unreadable folders are skipped.
                }
            }
            foreach (var list in index.Values) list.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
        }
        _diskNameIndex = index;
        return index;
    }

    private void Add(string key, byte[] data)
    {
        _files[key] = data;
        _caseInsensitive.TryAdd(key, key);
    }

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static string? RepairMojibake(string s)
    {
        bool high = false;
        foreach (var c in s)
        {
            if (c > 0xFF) return null;
            if (c >= 0x80) high = true;
        }
        if (!high) return null;
        try
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            var decoded = new UTF8Encoding(false, true).GetString(bytes);
            return decoded == s ? null : decoded;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
