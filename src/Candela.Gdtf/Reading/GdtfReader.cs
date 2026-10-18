using System.Xml;
using System.Xml.Linq;

namespace Candela.Gdtf;

/// <summary>
/// Reads GDTF files (DIN SPEC 15800; DataVersion 1.0, 1.1, 1.2) into a <see cref="FixtureType"/>.
/// </summary>
/// <remarks>
/// The reader is tolerant: unknown values, unparsable numbers, dangling references and missing resources become
/// <see cref="FixtureType.Warnings"/> and the GDTF default is used. Only a file that cannot yield a fixture type at all
/// (not a zip, no <c>description.xml</c>, malformed XML) throws <see cref="GdtfFormatException"/>. After parsing, all
/// name references are resolved into object links and every DMX mode gets its instanced channel layout
/// (<see cref="DmxMode.ChannelInstances"/>). Methods are thread-safe; reads of different files can run in parallel.
/// </remarks>
public static partial class GdtfReader
{
    /// <summary>Reads a <c>.gdtf</c> file.</summary>
    public static FixtureType ReadFile(string path)
    {
        using var archive = GdtfArchive.Open(path, GdtfArchiveLoad.Lazy);
        return Read(archive);
    }

    /// <summary>Reads a <c>.gdtf</c> file, returning false with a message instead of throwing for unreadable files.</summary>
    public static bool TryReadFile(string path, out FixtureType? fixtureType, out string? error)
    {
        try
        {
            fixtureType = ReadFile(path);
            error = null;
            return true;
        }
        catch (Exception e) when (e is GdtfFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            fixtureType = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>Reads a GDTF archive given as a stream (e.g. embedded in an MVR file).</summary>
    public static FixtureType Read(Stream gdtfArchive, string? fileName = null)
    {
        using var archive = GdtfArchive.Open(gdtfArchive, fileName);
        return Read(archive);
    }

    /// <summary>Reads an opened archive; wheel media and thumbnails are checked against its entries.</summary>
    public static FixtureType Read(GdtfArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var bytes = archive.ReadDescription();
        using var ms = new MemoryStream(bytes, writable: false);
        return ReadDescription(ms, archive, archive.FileName);
    }

    /// <summary>Reads a bare <c>description.xml</c> (no archive: resource paths are not resolved).</summary>
    public static FixtureType ReadDescription(Stream descriptionXml, string? sourceName = null) =>
        ReadDescription(descriptionXml, null, sourceName);

    /// <summary>
    /// Deepest XML element level that is read (the GDTF root is level 0): enough for <see cref="MaxGeometryDepth"/>
    /// nested geometries plus their Break / PinPatch children; real files reach level 12. Deeper elements are skipped
    /// with a warning, which keeps loading linear for hostile nesting.
    /// </summary>
    internal const int MaxXmlDepth = MaxGeometryDepth + 8;

    /// <summary>
    /// Elements read at most (the largest file of the library has 74 000); the rest of a larger document is ignored
    /// with a warning so that a hostile file cannot exhaust memory.
    /// </summary>
    internal const long MaxXmlElements = 4_000_000;

    private static FixtureType ReadDescription(Stream xml, GdtfArchive? archive, string? sourceName)
    {
        XDocument doc;
        long skipped;
        bool truncated, namespaced;
        try
        {
            using var reader = new LimitedXmlReader(XmlReader.Create(xml, XmlSettings), MaxXmlDepth, MaxXmlElements);
            doc = XDocument.Load(reader, LoadOptions.None);
            skipped = reader.SkippedElements;
            truncated = reader.Truncated;
            namespaced = reader.SawNamespace;
        }
        catch (XmlException e)
        {
            throw new GdtfFormatException($"description.xml of '{sourceName ?? "GDTF"}' is not well-formed XML: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            // XDocument refuses documents without a root element or with content after it.
            throw new GdtfFormatException($"description.xml of '{sourceName ?? "GDTF"}' is not a valid XML document: {e.Message}", e);
        }
        var limits = new List<GdtfWarning>(2);
        if (skipped > 0)
            limits.Add(new GdtfWarning("", $"{skipped} element(s) nested deeper than {MaxXmlDepth} levels were ignored with their content."));
        if (truncated)
            limits.Add(new GdtfWarning("", $"description.xml has more than {MaxXmlElements} elements; the rest of the file was ignored."));
        if (namespaced && doc.Root is { } root && StripNamespaces(root))
            limits.Add(new GdtfWarning("", "description.xml uses XML namespaces, which GDTF does not define; elements and attributes are read by local name."));
        return Parse(doc, archive, sourceName, limits);
    }

    /// <summary>
    /// Renames every element and attribute to its local name and drops namespace declarations (GDTF has no namespace;
    /// a namespaced file comes from a generic XML tool and means the same). Returns true when anything changed.
    /// </summary>
    private static bool StripNamespaces(XElement root)
    {
        bool changed = false;
        foreach (var e in root.DescendantsAndSelf())
        {
            if (e.Name.Namespace != XNamespace.None)
            {
                e.Name = e.Name.LocalName;
                changed = true;
            }
            if (!e.HasAttributes || !e.Attributes().Any(a => a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None)) continue;
            var attributes = new List<XAttribute>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in e.Attributes())
            {
                if (a.IsNamespaceDeclaration || !seen.Add(a.Name.LocalName)) continue; // same local name twice: first wins
                attributes.Add(new XAttribute(a.Name.LocalName, a.Value));
            }
            e.ReplaceAttributes(attributes);
            changed = true;
        }
        return changed;
    }

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    private static FixtureType Parse(XDocument doc, GdtfArchive? archive, string? sourceName, List<GdtfWarning> limits)
    {
        var root = doc.Root ?? throw new GdtfFormatException($"description.xml of '{sourceName ?? "GDTF"}' is empty.");
        var ft = new FixtureType { SourceFileName = sourceName };
        var ctx = new ReadContext(ft, archive);
        foreach (var w in limits) ctx.Warn(w.Path, w.Message);

        XElement? fte;
        if (root.Name == X.Gdtf)
        {
            ft.DataVersion = ReadContext.NonEmpty(root, X.DataVersion)?.Trim() ?? "1.0";
            fte = root.Element(X.FixtureType);
        }
        else if (root.Name == X.FixtureType)
        {
            ctx.Warn("", "Root element is FixtureType instead of GDTF; DataVersion 1.0 assumed.");
            ft.DataVersion = "1.0";
            fte = root;
        }
        else
        {
            fte = null;
        }
        if (fte is null)
            throw new GdtfFormatException($"description.xml of '{sourceName ?? "GDTF"}' has no FixtureType element.");

        ParseFixtureType(ctx, fte);
        Guard(ctx, "References", () => Resolver.Resolve(ctx));
        foreach (var mode in ft.DmxModes)
            Guard(ctx, $"DMXModes/{mode.Name}", () => DmxLayout.Build(mode, ctx.Warnings));
        ctx.Warnings.Complete();
        return ft;
    }

    /// <summary>Runs one section; an unexpected exception becomes a warning so the rest of the file still loads.</summary>
    private static void Guard(ReadContext ctx, string path, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ctx.Warn(path, $"Section could not be read completely ({e.GetType().Name}: {e.Message}).");
        }
    }
}
