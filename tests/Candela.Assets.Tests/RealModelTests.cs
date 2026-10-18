using System.IO.Compression;
using System.Numerics;
using System.Xml.Linq;

namespace Candela.Assets.Tests;

/// <summary>
/// Real-world GLB and 3DS files: the models inside the repository's GDTF fixtures (written by many manufacturers'
/// exporters), checked against the sizes their description.xml declares. glTF is Y-up by specification, so a correct
/// axis conversion makes the GLB bounds equal the declared Length (X) × Width (Y) × Height (Z); 3DS files are Z-up but
/// their units vary (mm, cm, m, inch), so only the proportions can be compared.
/// </summary>
public class RealModelTests
{
    private const int MaxModelBytes = 200_000;

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public RealModelTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private sealed record Sample(string Fixture, string Entry, byte[] Data, Vector3 Declared);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "_specs"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (with _specs/) not found.");
    }

    /// <summary>Up to <paramref name="count"/> distinct models of one format with a usable declared size, in file order.</summary>
    private static List<Sample> Samples(string folder, string extension, int count)
    {
        var result = new List<Sample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gdtf in Directory.EnumerateFiles(RepositoryRoot(), "*.gdtf").Order(StringComparer.Ordinal))
        {
            using var zip = ZipFile.OpenRead(gdtf);
            var description = zip.GetEntry("description.xml");
            if (description is null) continue;
            XDocument doc;
            using (var s = description.Open()) doc = XDocument.Load(s);
            foreach (var model in doc.Descendants().Where(e => e.Name.LocalName == "Model"))
            {
                var file = (string?)model.Attribute("File");
                if (string.IsNullOrEmpty(file)) continue;
                var entry = zip.GetEntry($"models/{folder}/{file}.{extension}");
                if (entry is null || entry.Length > MaxModelBytes || entry.Length == 0) continue;
                static float F(XElement e, string a) =>
                    float.TryParse((string?)e.Attribute(a), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
                var declared = new Vector3(F(model, "Length"), F(model, "Width"), F(model, "Height"));
                if (declared.X < 0.02f || declared.Y < 0.02f || declared.Z < 0.02f || declared == Vector3.One) continue;
                var data = new byte[entry.Length];
                using (var s = entry.Open()) s.ReadExactly(data);
                if (!seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data)))) continue;
                result.Add(new Sample(Path.GetFileName(gdtf), entry.FullName, data, declared));
                if (result.Count == count) return result;
            }
        }
        return result;
    }

    [Fact]
    public void RealGlbModelsComeOutZUpAtTheirDeclaredSize()
    {
        var samples = Samples("gltf", "glb", 60);
        Assert.True(samples.Count >= 40, $"only {samples.Count} GLB samples");
        int matching = 0;
        var mismatches = new List<string>();
        foreach (var s in samples)
        {
            var result = ModelImporter.Import(Path.GetFileName(s.Entry), s.Data);
            foreach (var part in result.Model.Parts) part.Mesh.Validate();
            var size = result.Model.ComputeBounds().Size;
            var ratio = size / s.Declared;
            if (MathF.Abs(ratio.X - 1) < 0.02f && MathF.Abs(ratio.Y - 1) < 0.02f && MathF.Abs(ratio.Z - 1) < 0.02f) matching++;
            else mismatches.Add($"{s.Fixture} {s.Entry}: {size} vs {s.Declared}");
        }
        _output.WriteLine($"{matching}/{samples.Count} GLB models match their declared size within 2 %");
        // Manufacturers sometimes reuse one mesh for several declared sizes, so not every model matches; a wrong axis
        // conversion (height on Y) would make almost none match.
        Assert.True(matching >= samples.Count * 0.6, $"{matching}/{samples.Count} match:\n{string.Join("\n", mismatches)}");
    }

    [Fact]
    public void RealThreeDsModelsAreReadZUp()
    {
        // 3DS units vary between exporters (mm, cm, m, inch) and declared sizes are approximate, so compare proportions:
        // the spread of log(size / declared) over the axes must be smaller as read (Z up) than with Y and Z swapped
        // (what a stray Y-up rotation would produce). Only models whose declared width and height differ clearly count.
        var samples = Samples("3ds", "3ds", 80);
        Assert.True(samples.Count >= 40, $"only {samples.Count} 3DS samples");
        int decisive = 0, zUpBetter = 0;
        var worse = new List<string>();
        foreach (var s in samples)
        {
            var result = ModelImporter.Import(Path.GetFileName(s.Entry), s.Data, options: new ImportOptions { GuessUnits = false });
            foreach (var part in result.Model.Parts) part.Mesh.Validate();
            Assert.Equal(SourceUpAxis.Z, result.SourceUpAxis);
            var size = result.Model.ComputeBounds().Size;
            if (MathF.Abs(MathF.Log(s.Declared.Y / s.Declared.Z)) < 0.3f || size.X <= 0 || size.Y <= 0 || size.Z <= 0) continue;
            decisive++;
            float asRead = Spread(size / s.Declared);
            float swapped = Spread(new Vector3(size.X, size.Z, size.Y) / s.Declared);
            if (asRead < swapped) zUpBetter++;
            else worse.Add($"{s.Fixture} {s.Entry}: size {size}, declared {s.Declared}");
        }
        _output.WriteLine($"{samples.Count} 3DS models, {decisive} decisive, {zUpBetter} fit better Z-up");
        Assert.True(decisive >= 15, $"only {decisive} decisive samples");
        Assert.True(zUpBetter >= decisive * 0.8, $"{zUpBetter}/{decisive} fit better Z-up:\n{string.Join("\n", worse)}");
    }

    /// <summary>Spread of the logarithms of the per-axis ratios (0 = exactly proportional).</summary>
    private static float Spread(Vector3 ratio)
    {
        float x = MathF.Log(ratio.X), y = MathF.Log(ratio.Y), z = MathF.Log(ratio.Z);
        float mean = (x + y + z) / 3;
        return MathF.Abs(x - mean) + MathF.Abs(y - mean) + MathF.Abs(z - mean);
    }

    [Fact]
    public void UnpaddedGlbChunksAreRepaired()
    {
        // This exporter writes GLB chunks without the 4-byte padding the specification requires.
        var gdtf = Path.Combine(RepositoryRoot(), "Prolights@AstraProfile1200IP@Rev_007_-_Open_white_adjusted_on_wheels.gdtf");
        using var zip = ZipFile.OpenRead(gdtf);
        var entry = zip.GetEntry("models/gltf/Base_red.glb")!;
        var data = new byte[entry.Length];
        using (var s = entry.Open()) s.ReadExactly(data);

        var result = ModelImporter.Import("Base_red.glb", data);

        Assert.True(result.TriangleCount > 0);
        Assert.Contains(result.Warnings, w => w.Contains("repaired", StringComparison.Ordinal));
        Assert.NotNull(GltfReader.RepackGlb(data));
        Assert.Null(GltfReader.RepackGlb(TestFiles.Glb())); // a conforming file needs no repair
    }
}
