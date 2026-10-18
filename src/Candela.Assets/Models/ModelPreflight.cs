using System.Globalization;
using System.Xml;

namespace Candela.Assets;

/// <summary>
/// Checks and cleans model files in managed code before the native Assimp library parses them. Assimp (the 5.0 build
/// AssimpNet ships) dereferences out-of-range data on malformed input and takes the whole process down — a truncated
/// download must not crash the editor or a project load. What it guards against was found by fuzzing the readers with
/// truncated and corrupted files:
/// <list type="bullet">
/// <item>OBJ/MTL: rewritten into a strictly well-formed subset by <see cref="ObjSanitizer"/>; an OBJ without any face
/// is rejected here (Assimp crashes building a point cloud from it).</item>
/// <item>COLLADA: the XML must be well-formed; numeric arrays (<c>float_array</c>, <c>int_array</c>, <c>p</c>,
/// <c>vcount</c>, <c>v</c>, <c>h</c>) must contain only numbers and at least as many as their <c>count</c> says; every
/// <c>accessor</c> must stay inside the array it reads.</item>
/// <item>ASCII FBX: every property record must have the tokens Assimp reads for its type.</item>
/// </list>
/// Binary FBX and 3DS went through the same fuzzing without failures; PLY, STL and glTF never reach Assimp.
/// </summary>
internal static class ModelPreflight
{
    /// <summary>
    /// Returns the bytes of the main file to hand to Assimp (sanitised for OBJ) and a filter applied to every companion
    /// file Assimp opens (MTL sanitising). Throws <see cref="AssetImportException"/> for files that must not reach Assimp.
    /// </summary>
    public static byte[] Prepare(ImportContext ctx, out Func<string, byte[], byte[]>? companionFilter)
    {
        companionFilter = null;
        var data = ctx.Files.MainData;
        switch (ctx.Format)
        {
            case ModelFormat.Obj:
                {
                    var report = new ObjSanitizer.Report();
                    ctx.ObjReport = report;
                    // While reading an OBJ, Assimp opens only material libraries (textures are just referenced). A "material
                    // library" without a single material is not one (e.g. "mtllib notes.txt"); it is not kept with the asset,
                    // so a crafted OBJ cannot pull a neighbouring private file into a project that is later shared.
                    companionFilter = (key, bytes) =>
                    {
                        int before = report.Materials;
                        var cleaned = ObjSanitizer.SanitizeMtl(bytes, report);
                        if (report.Materials == before)
                        {
                            ctx.Files.Discard(key);
                            ctx.Warn($"'{key}' contains no materials; it is not kept with the model.");
                        }
                        return cleaned;
                    };
                    var sanitized = ObjSanitizer.SanitizeObj(data, report);
                    // Assimp crashes on an OBJ that has vertices but no faces (it builds a point cloud).
                    if (report.Faces == 0)
                    {
                        throw new AssetImportException(report.DroppedFaces > 0
                            ? "The OBJ file contains no valid faces (every face refers to missing or invalid vertices)."
                            : "The OBJ file contains no faces (only points, lines or empty objects).");
                    }
                    return sanitized;
                }
            case ModelFormat.Collada:
                CheckCollada(data);
                return data;
            case ModelFormat.Fbx:
                CheckAsciiFbxProperties(data);
                return data;
            default:
                return data;
        }
    }

    /// <summary>Turns what the sanitiser removed into user-facing warnings.</summary>
    public static void ReportWarnings(ImportContext ctx)
    {
        if (ctx.ObjReport is not { } r) return;
        if (r.InvalidElements > 0)
            ctx.Warn($"{r.InvalidElements} vertex, texture-coordinate or normal entries of the OBJ file are not valid numbers; faces using them were skipped.");
        if (r.DroppedFaces > 0) ctx.Warn($"{r.DroppedFaces} OBJ face(s) with invalid or missing vertex references were skipped.");
        if (r.DroppedLinesAndPoints > 0) ctx.Warn("Points and lines in the OBJ file were skipped (only surfaces are imported).");
    }

    /// <summary>Validates a COLLADA document (see the class remarks); throws with a user-facing message.</summary>
    internal static void CheckCollada(byte[] data)
    {
        var arrays = new Dictionary<string, long>(StringComparer.Ordinal);
        var accessors = new List<(string Source, long Count, long Stride, long Offset, long Parameters)>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data, writable: false), settings);
            reader.Read();
            while (!reader.EOF)
            {
                // ReadElementContentAsString already moves to the next node; everything else advances here.
                if (reader.NodeType != XmlNodeType.Element || !IsChecked(reader.LocalName))
                {
                    reader.Read();
                    continue;
                }
                switch (reader.LocalName)
                {
                    case "float_array":
                    case "int_array":
                        {
                            bool isFloat = reader.LocalName == "float_array";
                            string? id = reader.GetAttribute("id");
                            long declared = Count(reader, "count", 0);
                            string name = reader.LocalName;
                            long actual = CountNumbers(reader.ReadElementContentAsString(), isFloat, allowNegative: true, name);
                            if (actual < declared) throw Inconsistent($"an array declares {declared} values but holds {actual}");
                            if (!string.IsNullOrEmpty(id)) arrays[id] = actual;
                            break;
                        }
                    case "p":
                    case "vcount":
                    case "v":
                    case "h":
                        {
                            string name = "<" + reader.LocalName + ">";
                            CountNumbers(reader.ReadElementContentAsString(), isFloat: false, allowNegative: false, name);
                            break;
                        }
                    case "accessor":
                        {
                            var source = reader.GetAttribute("source") ?? "";
                            long count = Count(reader, "count", 0), stride = Count(reader, "stride", 1), offset = Count(reader, "offset", 0);
                            long parameters = 0;
                            if (!reader.IsEmptyElement)
                            {
                                using var subtree = reader.ReadSubtree();
                                while (subtree.Read())
                                    if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "param") parameters++;
                            }
                            accessors.Add((source.StartsWith('#') ? source[1..] : source, count, stride, offset, parameters));
                            reader.Read();
                            break;
                        }
                }
            }
        }
        catch (XmlException e)
        {
            throw new AssetImportException($"The COLLADA (.dae) file is damaged or truncated (line {e.LineNumber}: {e.Message})", e);
        }
        foreach (var (source, count, stride, offset, parameters) in accessors)
        {
            if (!arrays.TryGetValue(source, out long length) || count == 0) continue;
            // The last element reads as many values as the accessor has parameters (at most one stride).
            long last = System.Math.Clamp(parameters, 1, System.Math.Max(stride, 1));
            if (stride == 0 || offset + (count - 1) * stride + last > length)
                throw Inconsistent($"an accessor reads {count} × {stride} values from '{source}', which holds {length}");
        }
    }

    // FBX property types whose value Assimp reads from the fifth token of a "P:" record, and those it reads as three
    // values (fifth to seventh token). Assimp does not check the token count first (assert only), so a damaged record
    // reads past the token list.
    private static readonly HashSet<string> s_fbxScalarTypes = new(StringComparer.Ordinal)
    {
        "KString", "bool", "Bool", "int", "Int", "enum", "Enum", "ULongLong", "KTime", "double", "Number", "Float",
        "FieldOfView", "UnitScaleFactor",
    };

    private static readonly HashSet<string> s_fbxVectorTypes = new(StringComparer.Ordinal)
    {
        "Vector3D", "ColorRGB", "Vector", "Color", "Lcl Translation", "Lcl Rotation", "Lcl Scaling",
    };

    /// <summary>
    /// Checks every property record (<c>P: "name", "type", "label", "flags", values…</c>) of an ASCII FBX file for the
    /// number of tokens Assimp will read. Binary FBX (typed records) is not affected and passes unchecked.
    /// </summary>
    internal static void CheckAsciiFbxProperties(byte[] data)
    {
        if (data.AsSpan().StartsWith("Kaydara FBX Binary"u8)) return;
        ReadOnlySpan<byte> span = data;
        int lineNumber = 0;
        var tokens = new List<string>(8);
        while (!span.IsEmpty)
        {
            int nl = span.IndexOf((byte)'\n');
            var line = nl < 0 ? span : span[..nl];
            span = nl < 0 ? ReadOnlySpan<byte>.Empty : span[(nl + 1)..];
            lineNumber++;
            int i = 0;
            while (i < line.Length && line[i] is (byte)' ' or (byte)'\t') i++;
            if (line.Length - i < 2 || line[i] != (byte)'P' || line[i + 1] != (byte)':') continue;
            SplitFbxTokens(line[(i + 2)..], tokens);
            int needed = tokens.Count < 2 ? 2 : s_fbxVectorTypes.Contains(tokens[1]) ? 7 : s_fbxScalarTypes.Contains(tokens[1]) ? 5 : 2;
            if (tokens.Count < needed)
                throw new AssetImportException($"The FBX file is damaged: the property record on line {lineNumber} is incomplete.");
        }
    }

    /// <summary>
    /// Comma-separated value tokens of an ASCII FBX record as Assimp's tokenizer sees them: quotes removed, empty tokens
    /// skipped, and the record ending at a bracket, a comment or the key of a following record.
    /// </summary>
    private static void SplitFbxTokens(ReadOnlySpan<byte> text, List<string> tokens)
    {
        tokens.Clear();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (byte b in text)
        {
            if (quoted)
            {
                if (b == (byte)'"') quoted = false;
                else current.Append((char)b);
                continue;
            }
            if (b == (byte)'"')
            {
                quoted = true;
                any = true;
            }
            else if (b is (byte)'{' or (byte)'}' or (byte)';')
            {
                // A scope bracket or a comment ends the record (Assimp's tokenizer makes the bracket its own token).
                break;
            }
            else if (b == (byte)':')
            {
                // "…,0P: …": the word before a colon is the key of the next record, not a value of this one.
                current.Clear();
                any = false;
                break;
            }
            else if (b == (byte)',')
            {
                if (any) tokens.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                current.Append((char)b);
                any = true;
            }
        }
        if (any) tokens.Add(current.ToString());
    }

    private static bool IsChecked(string element) => element is "float_array" or "int_array" or "p" or "vcount" or "v" or "h" or "accessor";

    private static long Count(XmlReader reader, string attribute, long fallback)
    {
        var text = reader.GetAttribute(attribute);
        if (text is null) return fallback;
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value > int.MaxValue)
            throw Inconsistent($"<{reader.LocalName} {attribute}=\"{text}\"> is not a valid count");
        return value;
    }

    /// <summary>Number of whitespace-separated numbers in <paramref name="text"/>; throws on anything else.</summary>
    private static long CountNumbers(string text, bool isFloat, bool allowNegative, string element)
    {
        long count = 0;
        var span = text.AsSpan();
        while (true)
        {
            int i = 0;
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            span = span[i..];
            if (span.IsEmpty) break;
            int end = 0;
            while (end < span.Length && !char.IsWhiteSpace(span[end])) end++;
            var token = span[..end];
            bool ok = isFloat
                ? double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)
                : long.TryParse(token, allowNegative ? NumberStyles.AllowLeadingSign : NumberStyles.None, CultureInfo.InvariantCulture, out long l)
                  && l is >= int.MinValue and <= int.MaxValue;
            if (!ok) throw Inconsistent($"{element} contains '{Shorten(token)}', which is not a valid number");
            count++;
            span = span[end..];
        }
        return count;
    }

    private static string Shorten(ReadOnlySpan<char> token) => token.Length > 24 ? token[..24].ToString() + "…" : token.ToString();

    private static AssetImportException Inconsistent(string detail) =>
        new($"The COLLADA (.dae) file is damaged: {detail}.");
}
