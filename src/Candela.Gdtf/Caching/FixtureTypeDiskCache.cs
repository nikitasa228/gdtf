using System.Buffers.Binary;
using System.Security.Cryptography;
using Candela.Core.Settings;

namespace Candela.Gdtf;

/// <summary>Counters of a <see cref="FixtureTypeDiskCache"/> since it was created.</summary>
/// <param name="Hits">Fixture types loaded from the cache.</param>
/// <param name="Misses">Lookups without a usable cache file (absent, from another reader version, or damaged).</param>
/// <param name="Stores">Fixture types written to the cache.</param>
/// <param name="Damaged">Cache files that were unreadable or failed validation (they are deleted and re-created).</param>
/// <param name="WriteFailures">Stores that failed (read-only or full disk, file locked by another process …).</param>
public readonly record struct FixtureTypeCacheStatistics(long Hits, long Misses, long Stores, long Damaged, long WriteFailures);

/// <summary>
/// Persistent binary cache of parsed fixture types, so that re-opening a fixture skips XML parsing and reference
/// resolution. Thread-safe; several processes may share one folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key.</b> A cache file is keyed by the content of the GDTF archive and by the reader version, not by its path:
/// a SHA-256 over the archive's table of contents (every entry's name, sizes and CRC-32 — the zip format's own content
/// checksum, so nothing has to be decompressed or hashed again), the cache <see cref="FormatVersion"/> and the module
/// version id of this assembly, which changes with every change of the reader code (builds are deterministic). The
/// same file under another name or path hits the same entry; an edited file or an updated reader misses. Hashing the
/// whole file instead would cost more than parsing it: 3D models make up 95 % of a typical <c>.gdtf</c>.
/// </para>
/// <para>
/// <b>Files.</b> <c>{Directory}/{key}.gdtfc</c>: a header (magic, format version, reader module id, key, payload
/// length, SHA-256 of the payload) and the payload (<see cref="FixtureTypeSerializer"/>). Files are written to a
/// temporary name and renamed, so readers never see partial files. Anything that does not validate — wrong magic,
/// version or key, bad checksum, truncated or malformed data — is deleted and treated as a miss, and the fixture is
/// parsed again: the cache can never make a fixture unreadable. Every I/O failure is swallowed the same way and
/// counted in <see cref="Statistics"/>.
/// </para>
/// <para>
/// <b>Size.</b> When the folder grows beyond <see cref="MaxBytes"/>, the least recently used files (by write time,
/// refreshed on hits once a day) are deleted down to three quarters of the limit. Files of older reader versions are
/// never hit again and age out the same way.
/// </para>
/// </remarks>
public sealed class FixtureTypeDiskCache
{
    /// <summary>Version of the cache file layout (header and <see cref="FixtureTypeSerializer"/> payload).</summary>
    public const int FormatVersion = 1 + 1000 * FixtureTypeSerializer.FormatVersion;

    /// <summary>Extension of cache files.</summary>
    public const string FileExtension = ".gdtfc";

    /// <summary>Default size limit of the cache folder (512 MiB; the 1091-file reference library needs about 60 MiB).</summary>
    public const long DefaultMaxBytes = 512L << 20;

    private const uint Magic = 0x43544643; // "CFTC" little endian
    private const int HeaderBytes = 4 + 4 + 16 + 32 + 4 + 32;
    private const int MaxPayloadBytes = 256 << 20;
    private static readonly Guid ReaderModuleId = typeof(FixtureTypeDiskCache).Assembly.ManifestModule.ModuleVersionId;
    private static readonly Lazy<FixtureTypeDiskCache> DefaultCache = new(() => new FixtureTypeDiskCache(DefaultDirectory));

    private readonly object _pruneGate = new();
    private long _hits, _misses, _stores, _damaged, _writeFailures;
    private long _approximateBytes = -1;

    /// <summary>Creates a cache in <paramref name="directory"/> (created when the first file is stored).</summary>
    public FixtureTypeDiskCache(string directory, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L << 20);
        Directory = Path.GetFullPath(directory);
        MaxBytes = maxBytes;
    }

    /// <summary>
    /// The per-user default folder: <c>{ConfigPaths.CacheDir}/gdtf</c> (the system temp folder instead when the user
    /// profile has no local application data folder).
    /// </summary>
    public static string DefaultDirectory
    {
        get
        {
            string root = ConfigPaths.CacheDir;
            if (!Path.IsPathRooted(root)) root = Path.Combine(Path.GetTempPath(), "Candela", "cache");
            return Path.Combine(root, "gdtf");
        }
    }

    /// <summary>The shared cache in <see cref="DefaultDirectory"/> (the folder is evaluated on first use).</summary>
    public static FixtureTypeDiskCache Default => DefaultCache.Value;

    /// <summary>Folder of the cache files.</summary>
    public string Directory { get; }

    /// <summary>Size limit of the folder in bytes.</summary>
    public long MaxBytes { get; }

    /// <summary>Counters since creation.</summary>
    public FixtureTypeCacheStatistics Statistics => new(
        Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _stores),
        Interlocked.Read(ref _damaged), Interlocked.Read(ref _writeFailures));

    /// <summary>
    /// Reads a <c>.gdtf</c> file through the cache: loads the cached fixture type when there is one for this content and
    /// reader version, else parses the file with <see cref="GdtfReader"/> and stores the result. Throws what
    /// <see cref="GdtfReader.ReadFile"/> throws for unreadable files (failures are not cached).
    /// </summary>
    public FixtureType ReadFile(string path)
    {
        using var archive = GdtfArchive.Open(path, GdtfArchiveLoad.Lazy);
        return Read(archive);
    }

    /// <summary>Reads an opened archive through the cache (see <see cref="ReadFile"/>).</summary>
    public FixtureType Read(GdtfArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        string key = ComputeKey(archive);
        if (TryLoad(key, out var cached))
        {
            // The content is the same, the name may differ (the same file under another name).
            cached!.SourceFileName = archive.FileName;
            return cached;
        }
        var ft = GdtfReader.Read(archive);
        Store(key, ft);
        return ft;
    }

    /// <summary>
    /// The cache key of an archive: hex SHA-256 over its table of contents (names, sizes and CRC-32 of all entries),
    /// <see cref="FormatVersion"/> and the reader's module version id.
    /// </summary>
    public static string ComputeKey(GdtfArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[4 + 16];
        BinaryPrimitives.WriteInt32LittleEndian(header, FormatVersion);
        ReaderModuleId.TryWriteBytes(header[4..]);
        hash.AppendData(header);
        archive.AppendContentDigest(hash);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Path of the cache file for <paramref name="key"/>.</summary>
    public string PathOf(string key) => Path.Combine(Directory, key + FileExtension);

    /// <summary>
    /// Loads the fixture type cached under <paramref name="key"/>. False when there is none or it is damaged (a damaged
    /// file is deleted). Never throws for I/O or data problems.
    /// </summary>
    public bool TryLoad(string key, out FixtureType? fixtureType)
    {
        ValidateKey(key);
        fixtureType = null;
        string path = PathOf(key);
        byte[] data;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (fs.Length is < HeaderBytes or > HeaderBytes + MaxPayloadBytes)
            {
                fs.Dispose();
                Discard(path);
                return false;
            }
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            Interlocked.Increment(ref _misses);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        try
        {
            fixtureType = Decode(data, key);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever is wrong with a cache file, the fixture is parsed again from the GDTF.
            fixtureType = null;
        }
        if (fixtureType is null)
        {
            Discard(path);
            return false;
        }
        Interlocked.Increment(ref _hits);
        Touch(path);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="fixtureType"/> under <paramref name="key"/> (atomically: temporary file + rename). False
    /// when it could not be written; never throws for I/O problems.
    /// </summary>
    public bool Store(string key, FixtureType fixtureType)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(fixtureType);
        byte[] file;
        try
        {
            file = Encode(fixtureType, key);
        }
        catch (InvalidOperationException)
        {
            // A model built or edited in code with links outside the fixture type: not cacheable.
            Interlocked.Increment(ref _writeFailures);
            return false;
        }

        string path = PathOf(key);
        string temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                fs.Write(file);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temp);
            Interlocked.Increment(ref _writeFailures);
            return false;
        }
        Interlocked.Increment(ref _stores);
        AccountAndPrune(file.Length);
        return true;
    }

    /// <summary>Deletes every cache file of the folder (files in use by other processes are skipped).</summary>
    public void Clear()
    {
        foreach (var f in EnumerateCacheFiles()) TryDelete(f.FullName);
        Interlocked.Exchange(ref _approximateBytes, 0);
    }

    // ---------------------------------------------------------------- encoding

    internal static byte[] Encode(FixtureType ft, string key)
    {
        var w = new CacheWriter();
        FixtureTypeSerializer.Write(ft, w);
        var payload = w.Written;
        var file = new byte[HeaderBytes + payload.Length];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], FormatVersion);
        ReaderModuleId.TryWriteBytes(span[8..]);
        Convert.FromHexString(key).CopyTo(span[24..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[56..], payload.Length);
        SHA256.HashData(payload, span[60..]);
        payload.CopyTo(span[HeaderBytes..]);
        return file;
    }

    /// <summary>The fixture type in a cache file, or null when the header does not match; throws on damaged payloads.</summary>
    internal static FixtureType? Decode(byte[] file, string key)
    {
        var span = file.AsSpan();
        if (span.Length < HeaderBytes) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(span[4..]) != FormatVersion) return null;
        if (new Guid(span.Slice(8, 16)) != ReaderModuleId) return null;
        if (key.Length != 64 || !Convert.FromHexString(key).AsSpan().SequenceEqual(span.Slice(24, 32))) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(span[56..]);
        if (length < 0 || length != span.Length - HeaderBytes) return null;
        var payload = span[HeaderBytes..];
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        if (!hash.SequenceEqual(span.Slice(60, 32))) return null;
        return FixtureTypeSerializer.Read(new CacheReader(file, HeaderBytes, length));
    }

    // ---------------------------------------------------------------- housekeeping

    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A cache key is 64 hexadecimal characters (see ComputeKey).", nameof(key));
    }

    private void Discard(string path)
    {
        Interlocked.Increment(ref _damaged);
        Interlocked.Increment(ref _misses);
        TryDelete(path);
    }

    /// <summary>Marks a hit file as recently used (at most once a day, so hits stay read-only in the common case).</summary>
    private static void Touch(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1)) File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only the eviction order suffers.
        }
    }

    private void AccountAndPrune(long added)
    {
        if (Interlocked.Read(ref _approximateBytes) < 0)
        {
            lock (_pruneGate)
            {
                if (_approximateBytes < 0) _approximateBytes = EnumerateCacheFiles().Sum(f => f.Length);
                else _approximateBytes += added;
            }
        }
        else
        {
            Interlocked.Add(ref _approximateBytes, added);
        }
        if (Interlocked.Read(ref _approximateBytes) > MaxBytes) Prune();
    }

    /// <summary>Deletes the least recently used files until the folder is below three quarters of <see cref="MaxBytes"/>.</summary>
    private void Prune()
    {
        lock (_pruneGate)
        {
            var files = EnumerateCacheFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            long target = MaxBytes / 4 * 3;
            foreach (var f in files)
            {
                if (total <= target) break;
                if (TryDelete(f.FullName)) total -= f.Length;
            }
            _approximateBytes = total;
        }
    }

    private IEnumerable<FileInfo> EnumerateCacheFiles()
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (!dir.Exists) return Array.Empty<FileInfo>();
            // Temporary files of crashed writers are cache files too (older than an hour: no writer is still busy).
            var stale = DateTime.UtcNow.AddHours(-1);
            return dir.EnumerateFiles("*" + FileExtension).Concat(dir.EnumerateFiles("*" + FileExtension + ".*.tmp").Where(f => f.LastWriteTimeUtc < stale)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
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
