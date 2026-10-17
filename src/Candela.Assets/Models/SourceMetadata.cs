using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace Candela.Assets;

/// <summary>Axis and unit metadata read from model files (COLLADA, 3MF, FBX global settings).</summary>
internal static class SourceMetadata
{
    /// <summary>Row-vector matrix that turns a Y-up right-handed frame into Candela's Z-up: (x, y, z) → (x, −z, y).</summary>
    public static readonly Matrix4x4 YUpToZUp = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);

    /// <summary>
    /// Reads <c>&lt;asset&gt;&lt;unit meter&gt;</c> and <c>&lt;up_axis&gt;</c> of a COLLADA document (defaults per the
    /// specification: 1 m, Y_UP). X_UP is mapped to a basis; malformed XML yields the defaults.
    /// </summary>
    public static (float Meter, SourceUpAxis Up, Matrix4x4? Basis) ReadCollada(byte[] data)
    {
        float meter = 1f;
        string up = "Y_UP";
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
            using var reader = XmlReader.Create(new MemoryStream(data, writable: false), settings);
            int depth = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "asset" && depth < 0)
                {
                    depth = reader.Depth;
                    continue;
                }
                if (depth < 0)
                {
                    // Only the document's own <asset> (direct child of COLLADA) matters; stop at the first library.
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName.StartsWith("library_", StringComparison.Ordinal)) break;
                    continue;
                }
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;
                if (reader.LocalName == "unit"
                    && float.TryParse(reader.GetAttribute("meter"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
                    && m > 0 && float.IsFinite(m))
                    meter = m;
                else if (reader.LocalName == "up_axis") up = reader.ReadElementContentAsString().Trim().ToUpperInvariant();
            }
        }
        catch (XmlException)
        {
            // Defaults; Assimp reports real syntax problems.
        }
        return up switch
        {
            "Z_UP" => (meter, SourceUpAxis.Z, null),
            // COLLADA X_UP: right = −Y, up = +X, towards the viewer = +Z → x → +Z, y → −X, z → −Y.
            "X_UP" => (meter, SourceUpAxis.Z, new Matrix4x4(0, 0, 1, 0, -1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, 1)),
            _ => (meter, SourceUpAxis.Y, null),
        };
    }

    /// <summary>Unit of a 3MF package (<c>&lt;model unit&gt;</c>, default millimetre) in metres; null when unreadable.</summary>
    public static float? Read3mfUnit(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase)
                && e.FullName.StartsWith("3D/", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(entry.Open(), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName != "model") return 0.001f;
                return (reader.GetAttribute("unit") ?? "millimeter").Trim().ToLowerInvariant() switch
                {
                    "micron" => 1e-6f,
                    "centimeter" => 0.01f,
                    "inch" => 0.0254f,
                    "foot" => 0.3048f,
                    "meter" => 1f,
                    _ => 0.001f,
                };
            }
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Source → Candela basis from FBX GlobalSettings (UpAxis/UpAxisSign, FrontAxis/FrontAxisSign,
    /// CoordAxis/CoordAxisSign): the coordinate (right) axis maps to +X, up to +Z, and the front axis (pointing
    /// towards the viewer) to −Y. Null when the values are absent or do not form an axis permutation.
    /// </summary>
    public static Matrix4x4? FbxBasis(int up, int upSign, int front, int frontSign, int coord, int coordSign)
    {
        if (up is < 0 or > 2 || front is < 0 or > 2 || coord is < 0 or > 2) return null;
        if (up == front || up == coord || front == coord) return null;
        var rows = new Vector3[3];
        rows[coord] = Vector3.UnitX * Sign(coordSign);
        rows[up] = Vector3.UnitZ * Sign(upSign);
        rows[front] = -Vector3.UnitY * Sign(frontSign);
        return new Matrix4x4(
            rows[0].X, rows[0].Y, rows[0].Z, 0,
            rows[1].X, rows[1].Y, rows[1].Z, 0,
            rows[2].X, rows[2].Y, rows[2].Z, 0,
            0, 0, 0, 1);
    }

    private static float Sign(int s) => s < 0 ? -1f : 1f;
}
