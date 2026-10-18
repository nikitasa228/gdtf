using System.IO.Compression;

namespace Candela.Project.Internal;

/// <summary>A problem reading one archive entry (missing data, damage, size limit); reported as an issue by the caller.</summary>
internal sealed class EntryReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads entries of a project archive with size limits, whatever sizes the zip directory declares, plus an overlay of
/// entries replaced by migrations. A declared size that the compressed data could not possibly produce (deflate expands at
/// most about 1032:1) is refused before anything is allocated.
/// </summary>
internal sealed class ArchiveAccess
{
    private const long MaxDeflateRatio = 1032;
    private readonly ZipArchive _zip;
    private readonly long _maxEntry;
    private long _remaining;

    public ArchiveAccess(ZipArchive zip, ProjectLoadOptions options)
    {
        _zip = zip;
        _maxEntry = System.Math.Clamp(options.MaxEntryBytes, 1, Array.MaxLength);
        _remaining = System.Math.Max(0, options.MaxTotalBytes);
    }

    /// <summary>Entries replaced (or removed, value null) by migrations.</summary>
    public Dictionary<string, byte[]?> Overlay { get; } = new(StringComparer.Ordinal);

    /// <summary>Names of all entries (archive and overlay), files only.</summary>
    public IReadOnlyCollection<string> EntryNames
    {
        get
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in _zip.Entries)
                if (!e.FullName.EndsWith('/')) names.Add(e.FullName);
            foreach (var (name, content) in Overlay)
            {
                if (content is null) names.Remove(name);
                else names.Add(name);
            }
            return names;
        }
    }

    public bool Exists(string name)
    {
        if (Overlay.TryGetValue(name, out var content)) return content is not null;
        return _zip.GetEntry(name) is not null;
    }

    /// <summary>The entry's content, or null when it does not exist. Throws <see cref="EntryReadException"/> when it cannot be read.</summary>
    public byte[]? Read(string name)
    {
        if (Overlay.TryGetValue(name, out var content)) return content;
        var entry = _zip.GetEntry(name);
        return entry is null ? null : Read(entry);
    }

    /// <summary>Like <see cref="Read(string)"/> but returns null instead of throwing (used by migrations).</summary>
    public byte[]? TryRead(string name)
    {
        try
        {
            return Read(name);
        }
        catch (EntryReadException)
        {
            return null;
        }
    }

    private byte[] Read(ZipArchiveEntry entry)
    {
        long declared = entry.Length;
        if (declared < 0) throw new EntryReadException("the archive directory is damaged");
        if (declared > _maxEntry) throw new EntryReadException($"it is too large ({declared:N0} bytes; the limit is {_maxEntry:N0})");
        if (declared > _remaining) throw new EntryReadException("the project exceeds the total size limit for loading");
        if (declared > 1024 * 1024 && declared > entry.CompressedLength * MaxDeflateRatio)
            throw new EntryReadException("its declared size is impossible for its compressed size (damaged archive)");
        var buffer = new byte[declared];
        try
        {
            using var stream = entry.Open();
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < buffer.Length) throw new EntryReadException("its data is truncated");
            if (stream.ReadByte() != -1) throw new EntryReadException("its data is longer than the archive directory says");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            throw new EntryReadException($"its data is damaged ({e.Message})", e);
        }
        _remaining -= declared;
        return buffer;
    }
}
