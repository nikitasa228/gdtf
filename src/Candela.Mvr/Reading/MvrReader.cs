using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Candela.Core.Diagnostics;

namespace Candela.Mvr;

/// <summary>
/// Reads <c>.mvr</c> archives (DIN SPEC 15801, MVR 1.4 – 1.6) into an <see cref="MvrDocument"/>.
/// </summary>
/// <remarks>
/// The reader is tolerant: unknown elements, unparsable values, missing uuids, bad matrices and unreadable embedded
/// files become <see cref="MvrDocument.Issues"/> and the element is read as far as possible. Only an archive that
/// cannot yield a scene description at all (not a zip, no <c>GeneralSceneDescription.xml</c>, malformed XML) throws
/// <see cref="MvrFormatException"/>. All methods are thread-safe; different files can be read in parallel.
/// </remarks>
public static class MvrReader
{
    /// <summary>Name of the scene description entry at the archive root.</summary>
    public const string SceneDescriptionEntry = "GeneralSceneDescription.xml";

    /// <summary>Largest embedded file read into memory (bigger entries are reported and skipped).</summary>
    public const long MaxEntryBytes = 1L << 30;

    /// <summary>Reads an <c>.mvr</c> file.</summary>
    /// <exception cref="MvrFormatException">The file is not an MVR archive.</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static MvrDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var doc = Read(stream, Path.GetFileName(path));
        return doc;
    }

    /// <summary>Reads an <c>.mvr</c> archive from a stream (the stream is not closed).</summary>
    /// <exception cref="MvrFormatException">The data is not an MVR archive.</exception>
    public static MvrDocument Read(Stream stream, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Stream source = stream;
        MemoryStream? copy = null;
        if (!stream.CanSeek)
        {
            copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            source = copy;
        }
        try
        {
            ZipArchive zip;
            try
            {
                zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException e)
            {
                throw new MvrFormatException($"'{sourceName ?? "MVR"}' is not a zip archive: {e.Message}", e);
            }
            using (zip)
            {
                return ReadArchive(zip, sourceName);
            }
        }
        finally
        {
            copy?.Dispose();
        }
    }

    /// <summary>Reads an <c>.mvr</c> archive held in memory.</summary>
    /// <exception cref="MvrFormatException">The data is not an MVR archive.</exception>
    public static MvrDocument Read(byte[] data, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data, writable: false);
        return Read(ms, sourceName);
    }

    /// <summary>
    /// Reads a bare <c>GeneralSceneDescription.xml</c> (no archive, so the document has no embedded files).
    /// </summary>
    /// <exception cref="MvrFormatException">The XML is malformed or not a scene description.</exception>
    public static MvrDocument ReadSceneDescription(Stream xml, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var doc = new MvrDocument { SourceFileName = sourceName };
        ParseXml(xml, doc, sourceName);
        return doc;
    }

    private static MvrDocument ReadArchive(ZipArchive zip, string? sourceName)
    {
        var doc = new MvrDocument { SourceFileName = sourceName };
        var sink = new IssueSink(doc.Issues);

        ZipArchiveEntry? description = null;
        string root = "";
        var entries = new List<ZipArchiveEntry>();
        try
        {
            foreach (var e in zip.Entries) entries.Add(e);
        }
        catch (InvalidDataException e)
        {
            throw new MvrFormatException($"'{sourceName ?? "MVR"}' has a corrupt zip directory: {e.Message}", e);
        }

        foreach (var e in entries)
        {
            string name = MvrDocument.NormalizePath(e.FullName);
            if (!string.Equals(MvrDocument.FileNameOf(name), SceneDescriptionEntry, StringComparison.OrdinalIgnoreCase)) continue;
            // Prefer the entry closest to the archive root.
            if (description is null || name.Count(c => c == '/') < MvrDocument.NormalizePath(description.FullName).Count(c => c == '/'))
                description = e;
        }
        if (description is null)
            throw new MvrFormatException($"'{sourceName ?? "MVR"}' has no {SceneDescriptionEntry}.");

        string descriptionPath = MvrDocument.NormalizePath(description.FullName);
        int slash = descriptionPath.LastIndexOf('/');
        if (slash >= 0)
        {
            root = descriptionPath[..(slash + 1)];
            sink.Add(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.ArchiveLayout,
                $"{SceneDescriptionEntry} is in the folder '{root}' instead of the archive root; file references are resolved from there.");
        }

        byte[] xmlBytes;
        try
        {
            xmlBytes = ReadEntry(description);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new MvrFormatException($"{SceneDescriptionEntry} of '{sourceName ?? "MVR"}' cannot be read: {e.Message}", e);
        }

        foreach (var e in entries)
        {
            if (ReferenceEquals(e, description)) continue;
            string name = MvrDocument.NormalizePath(e.FullName);
            if (name.Length == 0 || name.EndsWith('/')) continue; // folder entry
            if (root.Length > 0 && name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) name = name[root.Length..];
            if (e.Length > MaxEntryBytes)
            {
                string error = $"{e.Length} bytes exceed the limit of {MaxEntryBytes} bytes";
                doc.Files.Add(new MvrEmbeddedFile(name, null, error));
                sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.FileUnreadable, $"Embedded file '{name}' was skipped: {error}.");
                continue;
            }
            try
            {
                doc.Files.Add(new MvrEmbeddedFile(name, ReadEntry(e)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                doc.Files.Add(new MvrEmbeddedFile(name, null, ex.Message));
                sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.FileUnreadable, $"Embedded file '{name}' cannot be read: {ex.Message}");
            }
        }
        sink.Complete();

        using var ms = new MemoryStream(xmlBytes, writable: false);
        ParseXml(ms, doc, sourceName);
        return doc;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        long declared = entry.Length;
        var buffer = new MemoryStream(declared > 0 && declared < int.MaxValue ? (int)declared : 4096);
        var chunk = new byte[81920];
        int read;
        while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxEntryBytes) throw new InvalidDataException($"entry is larger than {MaxEntryBytes} bytes");
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    private static void ParseXml(Stream xml, MvrDocument doc, string? sourceName)
    {
        XDocument x;
        try
        {
            using var reader = XmlReader.Create(xml, XmlSettings);
            x = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw new MvrFormatException($"{SceneDescriptionEntry} of '{sourceName ?? "MVR"}' is not well-formed XML: {e.Message}", e);
        }
        new MvrXmlParser(doc).Parse(x);
    }
}

/// <summary>Thrown when data is not an MVR archive at all (content problems are reported as issues instead).</summary>
public sealed class MvrFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public MvrFormatException(string message, Exception? inner = null) : base(message, inner) { }
}
