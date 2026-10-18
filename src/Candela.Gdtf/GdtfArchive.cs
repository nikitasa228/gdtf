using System.IO.Compression;

namespace Candela.Gdtf;

/// <summary>
/// A <c>.gdtf</c> file: a zip archive with <c>description.xml</c>, thumbnails (<c>{Thumbnail}.png/.svg</c>), wheel media
/// (<c>wheels/*.png</c>) and models (<c>models/3ds/*.3ds</c>, <c>models/gltf/*.glb</c>, <c>models/svg*/*.svg</c>, plus
/// optional <c>_low</c>/<c>_high</c> detail folders).
/// </summary>
/// <remarks>
/// Entry lookup is case-insensitive and accepts back slashes. Opening from a path keeps only a shared-read file handle
/// and reads entries on demand (<see cref="GdtfArchiveLoad.Lazy"/>), or reads the whole file once and closes it
/// (<see cref="GdtfArchiveLoad.InMemory"/>). Reads are serialised internally, so one archive may be used from several
/// threads; every read returns its own byte array.
/// </remarks>
public sealed class GdtfArchive : IDisposable
{
    /// <summary>Name of the fixture description inside the archive.</summary>
    public const string DescriptionEntry = "description.xml";

    /// <summary>
    /// Largest entry that is decompressed (256 MiB; real descriptions are below 10 MiB, models below 20 MiB). Larger
    /// entries are treated as unreadable so a corrupt or hostile size field cannot exhaust memory.
    /// </summary>
    public const long MaxEntryBytes = 256L << 20;

    private readonly ZipArchive _zip;
    private readonly Stream _stream;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = new();
    private readonly object _gate = new();
    private bool _disposed;

    private GdtfArchive(Stream stream, string? fileName, string? filePath)
    {
        _stream = stream;
        FileName = fileName;
        FilePath = filePath;
        try
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException e)
        {
            stream.Dispose();
            throw new GdtfFormatException($"'{fileName ?? "stream"}' is not a zip archive: {e.Message}", e);
        }
        foreach (var e in _zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
            string key = Normalize(e.FullName);
            if (_entries.TryAdd(key, e)) _names.Add(key);
        }
    }

    /// <summary>File name of the archive (e.g. <c>Robe_Lighting@Robin_T1_Profile@….gdtf</c>), or null for anonymous streams.</summary>
    public string? FileName { get; }

    /// <summary>Full path when opened from a file.</summary>
    public string? FilePath { get; }

    /// <summary>Normalised entry names (forward slashes), directories excluded.</summary>
    public IReadOnlyList<string> EntryNames => _names;

    /// <summary>Opens a file. <paramref name="load"/> selects on-demand reading or reading the whole file into memory.</summary>
    public static GdtfArchive Open(string path, GdtfArchiveLoad load = GdtfArchiveLoad.Lazy)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Stream stream = load == GdtfArchiveLoad.InMemory
            ? new MemoryStream(File.ReadAllBytes(path), writable: false)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        return new GdtfArchive(stream, Path.GetFileName(path), Path.GetFullPath(path));
    }

    /// <summary>
    /// Opens an archive from a stream (e.g. a GDTF embedded in an MVR). The content is copied into memory, so the stream
    /// does not need to be seekable and is not kept.
    /// </summary>
    public static GdtfArchive Open(Stream stream, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return new GdtfArchive(ms, fileName, null);
    }

    /// <summary>Opens an archive held in memory (the array is not copied and must not change).</summary>
    public static GdtfArchive FromBytes(byte[] data, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new GdtfArchive(new MemoryStream(data, writable: false), fileName, null);
    }

    /// <summary>True when the archive has an entry with this path (case-insensitive).</summary>
    public bool Contains(string path) => _entries.ContainsKey(Normalize(path));

    /// <summary>The stored (normalised) spelling of an entry path, or null when absent.</summary>
    public string? ResolveEntryName(string path)
    {
        lock (_gate)
            return _entries.TryGetValue(Normalize(path), out var e) ? Normalize(e.FullName) : null;
    }

    /// <summary>Uncompressed size of an entry in bytes, or -1 when absent.</summary>
    public long GetSize(string path)
    {
        lock (_gate)
            return _entries.TryGetValue(Normalize(path), out var e) ? e.Length : -1;
    }

    /// <summary>Reads an entry; null when it does not exist, is larger than <see cref="MaxEntryBytes"/> or cannot be decompressed.</summary>
    public byte[]? TryReadBytes(string path)
    {
        string key = Normalize(path);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry) || entry.Length is < 0 or > MaxEntryBytes) return null;
            try
            {
                using var s = entry.Open();
                var buffer = new byte[entry.Length];
                s.ReadExactly(buffer);
                return buffer;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>Reads an entry; throws <see cref="FileNotFoundException"/> when it does not exist.</summary>
    public byte[] ReadBytes(string path) =>
        TryReadBytes(path) ?? throw new FileNotFoundException($"'{path}' is not in GDTF archive '{FileName}'.", path);

    /// <summary>Opens an entry as a read-only in-memory stream, or null when absent.</summary>
    public Stream? TryOpen(string path)
    {
        var bytes = TryReadBytes(path);
        return bytes is null ? null : new MemoryStream(bytes, writable: false);
    }

    /// <summary>Reads <c>description.xml</c>; throws <see cref="GdtfFormatException"/> when it is missing.</summary>
    public byte[] ReadDescription() =>
        TryReadBytes(DescriptionEntry) ?? throw new GdtfFormatException($"GDTF archive '{FileName}' has no {DescriptionEntry}.");

    /// <summary>Thumbnail entry path for an extension ("png" or "svg"), or null.</summary>
    public string? FindThumbnail(string? thumbnail, string extension = "png")
    {
        if (string.IsNullOrWhiteSpace(thumbnail)) thumbnail = "thumbnail";
        return ResolveEntryName($"{thumbnail}.{extension}") ?? ResolveEntryName($"{thumbnail.Trim()}.{extension}");
    }

    /// <summary>Entry path of a wheel media file (<c>wheels/{name}.png</c>, other image extensions tolerated), or null.</summary>
    public string? FindWheelMedia(string? mediaFileName)
    {
        if (string.IsNullOrWhiteSpace(mediaFileName)) return null;
        foreach (var ext in WheelExtensions)
        {
            var hit = ResolveEntryName($"wheels/{mediaFileName}{ext}");
            if (hit is not null) return hit;
        }
        return ResolveEntryName($"wheels/{mediaFileName}");
    }

    /// <summary>
    /// Finds a model file <c>{folder}/{fileName}{extension}</c> trying the folders and extensions in order, then any
    /// entry under <c>models/</c> with that base name and one of the extensions; null when nothing matches.
    /// </summary>
    public string? FindModelFile(string fileName, IReadOnlyList<string> folders, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        foreach (var folder in folders)
            foreach (var ext in extensions)
            {
                var hit = ResolveEntryName($"{folder}/{fileName}{ext}");
                if (hit is not null) return hit;
            }
        foreach (var ext in extensions)
            foreach (var name in _names)
            {
                if (!name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                int slash = name.LastIndexOf('/');
                var baseName = name.AsSpan(slash + 1, name.Length - slash - 1 - ext.Length);
                if (baseName.Equals(fileName.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase)) return name;
            }
        return null;
    }

    /// <summary>
    /// Feeds the archive's table of contents into <paramref name="hash"/>: every entry in archive order with its name,
    /// uncompressed and compressed size and CRC-32 (the zip format's own checksum of the entry's content). Two archives
    /// with the same digest have the same entries with the same content (up to CRC-32 collisions of same-sized
    /// entries), which is what <see cref="FixtureTypeDiskCache"/> keys on without decompressing anything.
    /// </summary>
    internal void AppendContentDigest(System.Security.Cryptography.IncrementalHash hash)
    {
        Span<byte> number = stackalloc byte[8 + 8 + 4];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var e in _zip.Entries)
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(e.FullName);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(number, name.Length);
                hash.AppendData(number[..4]);
                hash.AppendData(name);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number, e.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number[8..], e.CompressedLength);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(number[16..], e.Crc32);
                hash.AppendData(number);
            }
        }
    }

    /// <summary>Closes the file / releases the memory.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _zip.Dispose();
            _stream.Dispose();
        }
    }

    private static readonly string[] WheelExtensions = { ".png", ".PNG", ".jpg", ".jpeg", ".svg" };

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.TrimStart('/');
    }
}

/// <summary>How <see cref="GdtfArchive.Open(string, GdtfArchiveLoad)"/> reads a file.</summary>
public enum GdtfArchiveLoad
{
    /// <summary>Keep a shared-read handle and decompress entries on demand (cheap to open, holds a file handle).</summary>
    Lazy,

    /// <summary>Read the whole file into memory and close it (no handle kept; memory = file size).</summary>
    InMemory,
}
