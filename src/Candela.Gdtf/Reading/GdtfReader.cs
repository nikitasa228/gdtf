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

    private static FixtureType ReadDescription(Stream xml, GdtfArchive? archive, string? sourceName)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(xml, XmlSettings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException e)
        {
            throw new GdtfFormatException($"description.xml of '{sourceName ?? "GDTF"}' is not well-formed XML: {e.Message}", e);
        }
        return Parse(doc, archive, sourceName);
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

    private static FixtureType Parse(XDocument doc, GdtfArchive? archive, string? sourceName)
    {
        var root = doc.Root ?? throw new GdtfFormatException($"description.xml of '{sourceName ?? "GDTF"}' is empty.");
        var ft = new FixtureType { SourceFileName = sourceName };
        var ctx = new ReadContext(ft, archive);

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
