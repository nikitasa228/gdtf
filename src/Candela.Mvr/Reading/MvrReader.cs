using System.IO.Compression;
using System.Text;
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

    /// <summary>
    /// Largest total of embedded file bytes read from one archive (2 GiB). Entries beyond it are reported and skipped,
    /// so a zip bomb cannot exhaust memory.
    /// </summary>
    public const long MaxTotalBytes = 2L << 30;

    /// <summary>
    /// Deepest XML element nesting kept from a scene description (an object nesting level takes two: the object and
    /// its <c>ChildList</c>, so this allows about 250 levels of nested objects). Deeper content is dropped and reported,
    /// so a malicious or broken file cannot overflow the stack of the recursive object readers.
    /// </summary>
    public const int MaxXmlDepth = 512;

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
            xmlBytes = ReadEntry(description, MaxEntryBytes);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new MvrFormatException($"{SceneDescriptionEntry} of '{sourceName ?? "MVR"}' cannot be read: {e.Message}", e);
        }

        long total = 0;
        foreach (var e in entries)
        {
            if (ReferenceEquals(e, description)) continue;
            string name = MvrDocument.NormalizePath(e.FullName);
            if (name.Length == 0 || name.EndsWith('/')) continue; // folder entry
            if (root.Length > 0 && name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) name = name[root.Length..];
            string? limitError = e.Length > MaxEntryBytes
                ? $"{e.Length} bytes exceed the limit of {MaxEntryBytes} bytes"
                : total + e.Length > MaxTotalBytes
                    ? $"the archive's embedded files exceed the total limit of {MaxTotalBytes} bytes"
                    : null;
            if (limitError is not null)
            {
                doc.Files.Add(new MvrEmbeddedFile(name, null, limitError));
                sink.Add(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.FileUnreadable, $"Embedded file '{name}' was skipped: {limitError}.");
                continue;
            }
            try
            {
                var bytes = ReadEntry(e, MaxTotalBytes - total);
                total += bytes.Length;
                doc.Files.Add(new MvrEmbeddedFile(name, bytes));
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

    /// <summary>Reads an entry; the declared size is not trusted, the inflated data is capped at <paramref name="limit"/> bytes.</summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
    {
        limit = System.Math.Min(limit, MaxEntryBytes);
        using var s = entry.Open();
        long declared = entry.Length;
        var buffer = new MemoryStream(declared > 0 && declared <= limit ? (int)declared : 4096);
        var chunk = new byte[81920];
        int read;
        while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException($"the entry inflates to more than {limit} bytes");
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
        MemoryStream? copy = null;
        try
        {
            if (!xml.CanSeek)
            {
                copy = new MemoryStream();
                xml.CopyTo(copy);
                copy.Position = 0;
                xml = copy;
            }
            long start = xml.Position;
            XDocument x;
            int dropped = 0;
            try
            {
                // XDocument loading slows down more than quadratically with nesting depth and every later pass over
                // the tree recurses, so the depth is measured first (one cheap streaming pass) and an over-deep
                // document is pruned while it is streamed, before it is loaded.
                bool tooDeep = ExceedsMaxDepth(xml);
                xml.Position = start;
                using var reader = XmlReader.Create(xml, XmlSettings);
                if (!tooDeep)
                {
                    x = XDocument.Load(reader, LoadOptions.SetLineInfo);
                }
                else
                {
                    using var pruned = Prune(reader, out dropped);
                    using var prunedReader = XmlReader.Create(pruned, XmlSettings);
                    x = XDocument.Load(prunedReader, LoadOptions.None);
                }
            }
            catch (XmlException e)
            {
                throw new MvrFormatException($"{SceneDescriptionEntry} of '{sourceName ?? "MVR"}' is not well-formed XML: {e.Message}", e);
            }
            if (dropped > 0)
            {
                doc.Issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.NestingTooDeep,
                    $"{dropped} element(s) of {SceneDescriptionEntry} are nested deeper than {MaxXmlDepth} levels; they were dropped with their content (line numbers in other messages are not available)."));
            }
            new MvrXmlParser(doc).Parse(x);
        }
        finally
        {
            copy?.Dispose();
        }
    }

    /// <summary>True when an element is nested <see cref="MaxXmlDepth"/> levels deep or deeper (streaming, stops early).</summary>
    private static bool ExceedsMaxDepth(Stream xml)
    {
        using var reader = XmlReader.Create(xml, XmlSettings);
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.Depth >= MaxXmlDepth) return true;
        return false;
    }

    /// <summary>
    /// Streams the document into a UTF-8 copy without the elements nested <see cref="MaxXmlDepth"/> levels deep or
    /// deeper (with their content), counting the dropped subtrees. Comments and processing instructions are left out,
    /// as the loader ignores them anyway.
    /// </summary>
    private static MemoryStream Prune(XmlReader reader, out int dropped)
    {
        dropped = 0;
        var output = new MemoryStream();
        using (var w = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false }))
        {
            bool more = reader.Read();
            while (more)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.Depth >= MaxXmlDepth:
                        dropped++;
                        reader.Skip(); // now on the node after the subtree, which still has to be handled
                        more = !reader.EOF;
                        continue;
                    case XmlNodeType.Element:
                        bool empty = reader.IsEmptyElement;
                        w.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                        w.WriteAttributes(reader, defattr: false);
                        if (empty) w.WriteEndElement();
                        break;
                    case XmlNodeType.EndElement:
                        w.WriteFullEndElement();
                        break;
                    case XmlNodeType.Text:
                        w.WriteString(reader.Value);
                        break;
                    case XmlNodeType.CDATA:
                        w.WriteCData(reader.Value);
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        w.WriteWhitespace(reader.Value);
                        break;
                }
                more = reader.Read();
            }
        }
        output.Position = 0;
        return output;
    }
}

/// <summary>Thrown when data is not an MVR archive at all (content problems are reported as issues instead).</summary>
public sealed class MvrFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public MvrFormatException(string message, Exception? inner = null) : base(message, inner) { }
}
