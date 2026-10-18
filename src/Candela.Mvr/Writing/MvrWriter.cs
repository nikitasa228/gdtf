using Candela.Core.SceneGraph;

namespace Candela.Mvr;

/// <summary>
/// Writes MVR files (DIN SPEC 15801): a Core <see cref="Scene"/> as MVR 1.6 for consoles and visualisers, or an
/// <see cref="MvrDocument"/> as read by <see cref="MvrReader"/> (faithful round trip of everything the reader keeps).
/// </summary>
/// <remarks>
/// <para>
/// Scene export (<see cref="Write(Scene, Stream, MvrExportOptions?)"/>): scene layers become MVR layers; entities keep
/// their ids as uuids and their hierarchy, with local transforms written in millimetres. Fixtures are written with
/// <c>GDTFSpec</c>, <c>GDTFMode</c>, <c>Addresses</c>, <c>FixtureID</c>, <c>UnitNumber</c>, <c>Color</c>,
/// <c>CastShadow</c> and <c>Focus</c> (a <c>FocusPoint</c> is created per focused fixture) and their GDTF files are
/// embedded; trusses, rigging points, meshes and groups become <c>Truss</c>, <c>Support</c>, <c>SceneObject</c> and
/// <c>GroupObject</c> with models embedded as binary glTF; cables between two devices become MVR 1.6
/// <c>Connections</c>. Anything MVR cannot hold is listed in <see cref="MvrExportResult.Issues"/>.
/// </para>
/// <para>
/// Output is deterministic: equal input gives byte-identical archives (stable element order, invariant round-trip
/// number text, fixed zip time stamps), so exports can be diffed and round trips tested.
/// </para>
/// <para>
/// Threading: the scene is only read, but it must not change during the call; run it on the thread that owns the
/// scene, or on a snapshot.
/// </para>
/// </remarks>
public static class MvrWriter
{
    /// <summary>MVR major version written by the scene export.</summary>
    public const int VerMajor = 1;

    /// <summary>MVR minor version written by the scene export.</summary>
    public const int VerMinor = 6;

    /// <summary>Exports a scene as an <c>.mvr</c> archive to a stream (the stream is not closed).</summary>
    /// <returns>The document that was written and the export issues.</returns>
    public static MvrExportResult Write(Scene scene, Stream output, MvrExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(output);
        var result = BuildDocument(scene, options);
        MvrXmlWriter.WritePackage(result.Document, output);
        return result;
    }

    /// <summary>Exports a scene as an <c>.mvr</c> file (created or overwritten).</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public static MvrExportResult WriteFile(Scene scene, string path, MvrExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var result = BuildDocument(scene, options);
        WriteDocumentFile(result.Document, path);
        return result;
    }

    /// <summary>
    /// Converts a scene to an MVR document without writing it (GDTF and model callbacks are called, embedded files are
    /// in <see cref="MvrDocument.Files"/>). Useful to inspect or adjust the document before
    /// <see cref="WriteDocument(MvrDocument, Stream)"/>.
    /// </summary>
    public static MvrExportResult BuildDocument(Scene scene, MvrExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return new SceneExporter(scene, options ?? new MvrExportOptions()).Run();
    }

    /// <summary>
    /// Writes a document as an <c>.mvr</c> archive (the stream is not closed): <c>GeneralSceneDescription.xml</c> with
    /// the document's version, provider, user data, AUX data, layers and objects (unknown elements the reader kept are
    /// written back), followed by every readable embedded file.
    /// </summary>
    public static void WriteDocument(MvrDocument document, Stream output)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        MvrXmlWriter.WritePackage(document, output);
    }

    /// <summary>Writes a document as an <c>.mvr</c> archive in memory.</summary>
    public static byte[] WriteDocument(MvrDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var ms = new MemoryStream();
        MvrXmlWriter.WritePackage(document, ms);
        return ms.ToArray();
    }

    /// <summary>Writes a document as an <c>.mvr</c> file. The file is written to a temporary name first and then replaced, so a failed write never leaves a truncated file.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public static void WriteDocumentFile(MvrDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        string temp = full + ".tmp-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                MvrXmlWriter.WritePackage(document, fs);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Writes only <c>GeneralSceneDescription.xml</c> of a document (UTF-8, no archive).</summary>
    public static void WriteSceneDescription(MvrDocument document, Stream output)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        MvrXmlWriter.WriteXml(document, output);
    }
}
