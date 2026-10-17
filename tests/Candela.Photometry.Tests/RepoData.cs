using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Candela.Photometry.Tests;

/// <summary>
/// Real data from the repository: colours and beams of every bundled .gdtf fixture file (scanned once and cached) and
/// the researched fixture index in _specs. Used to check that the photometry code copes with messy real-world values.
/// </summary>
/// <remarks>
/// Only the part of each description.xml that precedes <c>&lt;DMXModes</c> is decompressed and scanned: by the GDTF
/// schema the wheels (slot colours), physical descriptions (emitters, filters) and geometries (beams) come first, while
/// the DMX modes are about two thirds of the 350 MB of XML in the bundled files. Only compact records are kept, so the
/// scan takes a fraction of the time and memory of holding every description in memory.
/// </remarks>
internal static partial class RepoData
{
    private const string DmxModesMarker = "<DMXModes";

    private static readonly Lazy<string> _root = new(FindRoot);
    private static readonly Lazy<GdtfScan> _gdtf = new(ScanGdtfFiles);
    private static readonly Lazy<JsonDocument> _specs = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "_specs", "fixtures_index.json"))));

    /// <summary>Repository root (the folder holding _specs and the .gdtf files).</summary>
    public static string Root => _root.Value;

    /// <summary>The fixture records of _specs/fixtures_index.json.</summary>
    public static IEnumerable<JsonElement> SpecFixtures => _specs.Value.RootElement.EnumerateArray();

    /// <summary>
    /// Every <c>Color="x,y,Y"</c> attribute of an <c>Emitter</c>, <c>Filter</c> or <c>Slot</c> element (named by
    /// <paramref name="element"/>) in all GDTF files, in file-name order; unparsable numbers are NaN.
    /// </summary>
    public static IEnumerable<ColorRecord> Colors(string element) =>
        _gdtf.Value.Colors.TryGetValue(element, out var list) ? list : [];

    /// <summary>Every Beam geometry of all GDTF files: raw BeamType and the beam/field angles in degrees (NaN when absent).</summary>
    public static IReadOnlyList<BeamRecord> Beams() => _gdtf.Value.Beams;

    public static float Parse(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;

    [GeneratedRegex("<(Emitter|Filter|Slot)\\b[^>]*?\\bColor=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ColorAttribute();

    [GeneratedRegex("<Beam\\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex BeamElement();

    [GeneratedRegex("\\b(BeamType|BeamAngle|FieldAngle)=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex BeamAttribute();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "_specs")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Repository root with _specs/ not found above " + AppContext.BaseDirectory);
    }

    private static GdtfScan ScanGdtfFiles()
    {
        var files = Directory.GetFiles(Root, "*.gdtf");
        Array.Sort(files, StringComparer.Ordinal);
        var perFile = new (List<(string Element, ColorRecord Color)> Colors, List<BeamRecord> Beams)?[files.Length];
        Parallel.For(0, files.Length, i =>
        {
            string? xml = ReadDescriptionPrefix(files[i]);
            if (xml is null)
                return;
            string name = Path.GetFileName(files[i]);
            var colors = new List<(string, ColorRecord)>();
            foreach (Match m in ColorAttribute().Matches(xml))
            {
                string raw = m.Groups[2].Value;
                var parts = raw.Split(',');
                colors.Add((m.Groups[1].Value, new ColorRecord(name, raw,
                    parts.Length > 0 ? Parse(parts[0]) : float.NaN,
                    parts.Length > 1 ? Parse(parts[1]) : float.NaN,
                    parts.Length > 2 ? Parse(parts[2]) : float.NaN)));
            }

            var beams = new List<BeamRecord>();
            foreach (Match m in BeamElement().Matches(xml))
            {
                string? type = null, beamAngle = null, fieldAngle = null;
                foreach (Match a in BeamAttribute().Matches(m.Value))
                {
                    switch (a.Groups[1].Value)
                    {
                        case "BeamType": type = a.Groups[2].Value; break;
                        case "BeamAngle": beamAngle = a.Groups[2].Value; break;
                        default: fieldAngle = a.Groups[2].Value; break;
                    }
                }

                beams.Add(new BeamRecord(name, type, Parse(beamAngle), Parse(fieldAngle)));
            }

            perFile[i] = (colors, beams);
        });

        var byElement = new Dictionary<string, List<ColorRecord>>(StringComparer.Ordinal);
        var allBeams = new List<BeamRecord>();
        foreach (var entry in perFile)
        {
            if (entry is not { } data)
                continue;
            foreach (var (element, color) in data.Colors)
            {
                if (!byElement.TryGetValue(element, out var list))
                    byElement[element] = list = [];
                list.Add(color);
            }

            allBeams.AddRange(data.Beams);
        }

        return new GdtfScan(byElement.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal), allBeams.ToArray());
    }

    /// <summary>The description.xml of a GDTF archive up to its DMX modes, or null when the archive cannot be read.</summary>
    private static string? ReadDescriptionPrefix(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("description.xml");
            if (entry is null)
                return null;
            using var reader = new StreamReader(entry.Open());
            var text = new StringBuilder();
            var buffer = new char[(1 << 16) + DmxModesMarker.Length];
            int carry = 0;
            while (true)
            {
                int read = reader.Read(buffer, carry, buffer.Length - carry);
                if (read == 0)
                {
                    text.Append(buffer, 0, carry);
                    return text.ToString();
                }

                int length = carry + read;
                int marker = new ReadOnlySpan<char>(buffer, 0, length).IndexOf(DmxModesMarker, StringComparison.Ordinal);
                if (marker >= 0)
                {
                    text.Append(buffer, 0, marker);
                    return text.ToString();
                }

                // Keep a tail that could hold the start of a marker split across two reads.
                carry = Math.Min(DmxModesMarker.Length - 1, length);
                text.Append(buffer, 0, length - carry);
                Array.Copy(buffer, length - carry, buffer, 0, carry);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // A broken archive is not this module's concern; the GDTF reader reports it.
            return null;
        }
    }

    private sealed record GdtfScan(IReadOnlyDictionary<string, ColorRecord[]> Colors, BeamRecord[] Beams);
}

/// <summary>One <c>Color="x,y,Y"</c> attribute found in a GDTF file.</summary>
internal readonly record struct ColorRecord(string File, string Raw, float X, float Y, float BigY);

/// <summary>One Beam geometry found in a GDTF file (angles in degrees, NaN when absent or unparsable).</summary>
internal readonly record struct BeamRecord(string File, string? BeamType, float BeamAngleDeg, float FieldAngleDeg);
