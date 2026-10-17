using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Candela.Photometry.Tests;

/// <summary>
/// Real data from the repository: the bundled .gdtf fixture files (their description.xml, read once and cached) and
/// the researched fixture index in _specs. Used to check that the photometry code copes with messy real-world values.
/// </summary>
internal static partial class RepoData
{
    private static readonly Lazy<string> _root = new(FindRoot);
    private static readonly Lazy<IReadOnlyList<(string File, string Xml)>> _descriptions = new(LoadDescriptions);
    private static readonly Lazy<JsonDocument> _specs = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "_specs", "fixtures_index.json"))));

    /// <summary>Repository root (the folder holding _specs and the .gdtf files).</summary>
    public static string Root => _root.Value;

    /// <summary>description.xml of every bundled GDTF file that could be opened, in file-name order.</summary>
    public static IReadOnlyList<(string File, string Xml)> Descriptions => _descriptions.Value;

    /// <summary>The fixture records of _specs/fixtures_index.json.</summary>
    public static IEnumerable<JsonElement> SpecFixtures => _specs.Value.RootElement.EnumerateArray();

    /// <summary>Every <c>Color="x,y,Y"</c> attribute of an element named <paramref name="element"/> in all GDTF files.</summary>
    public static IEnumerable<(string File, string Raw, float X, float Y, float BigY)> Colors(string element)
    {
        var regex = new Regex($"<{element}\\b[^>]*?\\bColor=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        foreach (var (file, xml) in Descriptions)
        {
            foreach (Match m in regex.Matches(xml))
            {
                var raw = m.Groups[1].Value;
                var parts = raw.Split(',');
                float x = parts.Length > 0 ? Parse(parts[0]) : float.NaN;
                float y = parts.Length > 1 ? Parse(parts[1]) : float.NaN;
                float bigY = parts.Length > 2 ? Parse(parts[2]) : float.NaN;
                yield return (file, raw, x, y, bigY);
            }
        }
    }

    /// <summary>Every Beam geometry of all GDTF files: raw BeamType and the beam/field angles in degrees (NaN when absent).</summary>
    public static IEnumerable<(string File, string? BeamType, float BeamAngleDeg, float FieldAngleDeg)> Beams()
    {
        foreach (var (file, xml) in Descriptions)
        {
            foreach (Match m in BeamElement().Matches(xml))
            {
                string tag = m.Value;
                yield return (file, Attribute(tag, "BeamType"), Parse(Attribute(tag, "BeamAngle")), Parse(Attribute(tag, "FieldAngle")));
            }
        }
    }

    public static float Parse(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;

    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, $"\\b{name}=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("<Beam\\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex BeamElement();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "_specs")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Repository root with _specs/ not found above " + AppContext.BaseDirectory);
    }

    private static IReadOnlyList<(string File, string Xml)> LoadDescriptions()
    {
        var files = Directory.GetFiles(Root, "*.gdtf");
        Array.Sort(files, StringComparer.Ordinal);
        var result = new (string, string)[files.Length];
        Parallel.For(0, files.Length, i =>
        {
            try
            {
                using var zip = ZipFile.OpenRead(files[i]);
                var entry = zip.GetEntry("description.xml");
                if (entry is null)
                    return;
                using var reader = new StreamReader(entry.Open());
                result[i] = (Path.GetFileName(files[i]), reader.ReadToEnd());
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // A broken archive is not this module's concern; the GDTF reader reports it.
            }
        });
        return result.Where(r => r.Item1 is not null).ToArray();
    }
}
