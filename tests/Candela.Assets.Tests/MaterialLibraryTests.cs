using System.Numerics;
using System.Reflection;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets.Tests;

public class MaterialLibraryTests
{
    /// <summary>Every id declared in Core's MaterialIds.</summary>
    private static readonly string[] s_ids = typeof(MaterialIds).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();

    /// <summary><see cref="s_ids"/> as theory data.</summary>
    public static TheoryData<string> AllIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in s_ids) data.Add(id);
        return data;
    }

    private static readonly MaterialLibrary s_library = MaterialLibrary.Default;

    private static Vector3 MeanLinearAlbedo(ImageData image)
    {
        double r = 0, g = 0, b = 0;
        int n = image.Width * image.Height;
        for (int i = 0; i < n; i++)
        {
            r += ColorEncoding.SrgbByteToLinear(image.Pixels[i * 4]);
            g += ColorEncoding.SrgbByteToLinear(image.Pixels[i * 4 + 1]);
            b += ColorEncoding.SrgbByteToLinear(image.Pixels[i * 4 + 2]);
        }
        return new Vector3((float)(r / n), (float)(g / n), (float)(b / n));
    }

    private static double MeanChannel(ImageData image, int channel)
    {
        double sum = 0;
        int n = image.Width * image.Height;
        for (int i = 0; i < n; i++) sum += image.Pixels[i * 4 + channel];
        return sum / n;
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void EveryBuiltinIdResolvesToAPlausibleMaterial(string id)
    {
        var builtin = s_library.Find(id);

        Assert.NotNull(builtin);
        Assert.Equal(id, builtin!.Id);
        Assert.False(string.IsNullOrWhiteSpace(builtin.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(builtin.Description));
        Assert.False(string.IsNullOrWhiteSpace(builtin.Reference));
        Assert.Contains(builtin.Category, s_library.Categories);
        Assert.InRange(builtin.Albedo.X, 0.01f, 1f);
        Assert.InRange(builtin.Roughness, MaterialApproximation.MinimumRoughness, 1f);
        Assert.True(builtin.Metallic is 0f or 1f);

        var m = s_library.GetMaterial(id);
        Assert.Equal(builtin.DisplayName, m.Name);
        Assert.InRange(m.BaseColorFactor.X, 0f, 1f);
        Assert.Equal(1f, m.BaseColorFactor.W);
        Assert.Equal(builtin.Metallic, m.Metallic);
        Assert.InRange(m.Roughness, MaterialApproximation.MinimumRoughness, 1f);
        Assert.True(m.UvScale.X > 0 && m.UvScale.Y > 0);
        Assert.NotSame(m, s_library.GetMaterial(id)); // callers get their own copy
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void EveryTextureOfABuiltinMaterialDecodesThroughTheAssetStore(string id)
    {
        var store = new AssetStore();
        var builtin = s_library.Find(id)!;
        var m = builtin.CreateMaterial();
        var keys = new[] { m.BaseColorTexture, m.NormalTexture, m.MetallicRoughnessTexture, m.OcclusionTexture, m.EmissiveTexture }
            .Where(k => k is not null).Select(k => k!).ToList();

        Assert.Equal(builtin.TextureChannels.Count, keys.Count);
        foreach (var channel in builtin.TextureChannels) Assert.Contains(MaterialLibrary.TextureKey(id, channel), keys);
        foreach (var key in keys)
        {
            Assert.StartsWith(id + "/", key, StringComparison.Ordinal);
            var image = store.Load(key);
            Assert.NotNull(image);
            Assert.Equal(PixelFormat.Rgba8, image!.Format);
            Assert.True(image.Width >= 32 && image.Width == image.Height, $"{key}: {image.Width} × {image.Height}");
            Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
            Assert.Same(image, s_library.LoadTexture(key)); // generated once
        }

        if (m.BaseColorTexture is { } albedoKey)
        {
            // Albedo maps average to the reference albedo (the factor is white so the texture carries the colour).
            Assert.Equal(Vector4.One, m.BaseColorFactor);
            var mean = MeanLinearAlbedo(store.Load(albedoKey)!);
            Geo.AssertNear(builtin.Albedo, mean, MathF.Max(0.01f, builtin.Albedo.X * 0.08f));
        }
        if (m.MetallicRoughnessTexture is { } roughKey)
        {
            // glTF packing: G = roughness, B = metallic; the factor 1 lets the map carry absolute values.
            var map = store.Load(roughKey)!;
            Assert.Equal(1f, m.Roughness);
            Assert.InRange(MeanChannel(map, 1) / 255.0, builtin.Roughness - 0.1, builtin.Roughness + 0.1);
            Assert.Equal(255 * builtin.Metallic, MeanChannel(map, 2));
        }
        if (m.NormalTexture is { } normalKey)
        {
            // Tangent-space normals pointing mostly out of the surface.
            var map = store.Load(normalKey)!;
            Assert.InRange(MeanChannel(map, 0), 118, 138);
            Assert.InRange(MeanChannel(map, 1), 118, 138);
            Assert.True(MeanChannel(map, 2) > 200);
        }
    }

    [Fact]
    public void ProceduralTexturesTileSeamlessly()
    {
        foreach (var builtin in s_library.Materials)
        {
            foreach (var channel in builtin.TextureChannels)
            {
                var image = s_library.LoadTexture(MaterialLibrary.TextureKey(builtin.Id, channel))!;
                int w = image.Width, h = image.Height;
                int Diff(int x0, int y0, int x1, int y1)
                {
                    int a = (y0 * w + x0) * 4, b = (y1 * w + x1) * 4;
                    return Math.Abs(image.Pixels[a] - image.Pixels[b]) + Math.Abs(image.Pixels[a + 1] - image.Pixels[b + 1])
                        + Math.Abs(image.Pixels[a + 2] - image.Pixels[b + 2]);
                }
                // Summed difference across every column boundary (x | x + 1) and across the wrap-around seam (w − 1 | 0);
                // crossing the tile edge must look like crossing some boundary inside the tile (plank joints included).
                long seam = 0, seamV = 0, worst = 0, worstV = 0;
                for (int y = 0; y < h; y++) seam += Diff(w - 1, y, 0, y);
                for (int x = 0; x + 1 < w; x++)
                {
                    long sum = 0;
                    for (int y = 0; y < h; y++) sum += Diff(x, y, x + 1, y);
                    worst = Math.Max(worst, sum);
                }
                for (int x = 0; x < w; x++) seamV += Diff(x, h - 1, x, 0);
                for (int y = 0; y + 1 < h; y++)
                {
                    long sum = 0;
                    for (int x = 0; x < w; x++) sum += Diff(x, y, x, y + 1);
                    worstV = Math.Max(worstV, sum);
                }
                Assert.True(seam <= worst * 1.05 + h, $"{builtin.Id}/{channel}: horizontal seam {seam / (double)h:0.0} vs worst inner {worst / (double)h:0.0}");
                Assert.True(seamV <= worstV * 1.05 + w, $"{builtin.Id}/{channel}: vertical seam {seamV / (double)w:0.0} vs worst inner {worstV / (double)w:0.0}");
            }
        }
    }

    [Fact]
    public void ReferenceReflectancesArePhysicallyPlausible()
    {
        float Y(string id) => s_library.Find(id)!.LuminousReflectance;

        Assert.InRange(Y(MaterialIds.MoltonBlack), 0.01f, 0.04f);
        Assert.InRange(Y(MaterialIds.PaintedWallBlack), 0.02f, 0.06f);
        Assert.InRange(Y(MaterialIds.StageFloorBlack), 0.02f, 0.06f);
        Assert.InRange(Y(MaterialIds.LedScreenOff), 0.01f, 0.05f);
        Assert.InRange(Y(MaterialIds.PaintedWallWhite), 0.75f, 0.85f);
        Assert.InRange(Y(MaterialIds.DanceFloorWhite), 0.65f, 0.85f);
        Assert.InRange(Y(MaterialIds.Cyclorama), 0.6f, 0.8f);
        Assert.InRange(Y(MaterialIds.Concrete), 0.25f, 0.4f);
        Assert.InRange(Y(MaterialIds.WoodStage), 0.15f, 0.4f);
        Assert.InRange(Y(MaterialIds.Carpet), 0.05f, 0.15f);
        Assert.True(Y(MaterialIds.MoltonBlack) < Y(MaterialIds.PaintedWallBlack));

        var molton = s_library.GetMaterial(MaterialIds.MoltonBlack);
        Assert.Equal(1f, molton.Roughness);
        Assert.True(molton.DoubleSided);

        var aluminium = s_library.GetMaterial(MaterialIds.AluminiumTruss);
        Assert.Equal(1f, aluminium.Metallic);
        Assert.InRange(aluminium.BaseColorFactor.X, 0.85f, 0.95f);

        var mirror = s_library.GetMaterial(MaterialIds.Mirror);
        Assert.Equal(1f, mirror.Metallic);
        Assert.True(mirror.Roughness <= 0.05f);

        var glass = s_library.GetMaterial(MaterialIds.Glass);
        Assert.Equal(0f, glass.Metallic);
        Assert.Equal(1f, glass.Transmission);
        Assert.InRange(glass.Ior, 1.45f, 1.55f);
        Assert.True(glass.DoubleSided);

        var steel = s_library.GetMaterial(MaterialIds.SteelBlack);
        Assert.True(steel.BaseColorFactor.X < 0.1f);
    }

    [Fact]
    public void CategoriesAndNamesForTheUi()
    {
        Assert.Equal(s_ids.Length, s_library.Materials.Count);
        Assert.Equal(s_library.Materials.Count, s_library.Materials.Select(m => m.Id).Distinct().Count());
        Assert.Equal(s_library.Materials.Count, s_library.Materials.Select(m => m.DisplayName).Distinct().Count());
        foreach (var category in Enum.GetValues<MaterialCategory>())
            Assert.False(string.IsNullOrWhiteSpace(MaterialLibrary.CategoryDisplayName(category)));
        Assert.Equal(s_library.Materials.Count, s_library.Categories.Sum(c => s_library.InCategory(c).Count()));
        Assert.Contains(s_library.InCategory(MaterialCategory.Floor), m => m.Id == MaterialIds.WoodStage);
        Assert.Contains(s_library.InCategory(MaterialCategory.Metal), m => m.Id == MaterialIds.AluminiumTruss);
    }

    [Fact]
    public void UnknownIdsFallBackToTheDefaultMaterial()
    {
        Assert.Null(s_library.Find("builtin:unobtainium"));
        Assert.False(s_library.Contains(null));
        Assert.Equal(s_library.Find(MaterialIds.Default)!.DisplayName, s_library.GetMaterial("builtin:unobtainium").Name);
        Assert.Null(s_library.LoadTexture("builtin:painted-wall-white/albedo")); // untextured material
        Assert.Null(s_library.LoadTexture("not-a-key"));
    }

    [Fact]
    public void TexturesAreDeterministicAndThreadSafe()
    {
        var fresh = new MaterialLibrary();
        const string key = "builtin:concrete/albedo";
        var results = new ImageData?[16];

        Parallel.For(0, results.Length, i => results[i] = fresh.LoadTexture(key));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(MaterialLibrary.Default.LoadTexture(key)!.Pixels, results[0]!.Pixels);
    }

    [Fact]
    public void PhongApproximationFollowsTheDocumentedRules()
    {
        Assert.Equal(1f, MaterialApproximation.RoughnessFromShininess(0f));
        Assert.Equal(0.64f, MaterialApproximation.RoughnessFromShininess(10f), 2);
        Assert.Equal(0.37f, MaterialApproximation.RoughnessFromShininess(100f), 2);
        Assert.Equal(0.21f, MaterialApproximation.RoughnessFromShininess(1000f), 2);
        Assert.True(MaterialApproximation.RoughnessFromShininess(1e9f) >= MaterialApproximation.MinimumRoughness);

        var matte = MaterialApproximation.FromPhong(new PhongMaterial { Name = "plaster", Diffuse = new Vector3(0.7f) });
        Assert.Equal(0f, matte.Metallic);
        Assert.True(matte.Roughness >= 0.9f);

        var plastic = MaterialApproximation.FromPhong(new PhongMaterial { Name = "red plastic", Diffuse = new Vector3(0.6f, 0.05f, 0.05f), Specular = new Vector3(0.5f), Shininess = 100 });
        Assert.Equal(0f, plastic.Metallic);
        Assert.Equal(0.6f, plastic.BaseColorFactor.X, 3);

        var gold = MaterialApproximation.FromPhong(new PhongMaterial { Name = "trim", Diffuse = new Vector3(0.1f, 0.08f, 0.02f), Specular = new Vector3(1f, 0.78f, 0.34f), Shininess = 200 });
        Assert.Equal(1f, gold.Metallic);
        Geo.AssertNear(new Vector3(1f, 0.78f, 0.34f), new Vector3(gold.BaseColorFactor.X, gold.BaseColorFactor.Y, gold.BaseColorFactor.Z));

        var named = MaterialApproximation.FromPhong(new PhongMaterial { Name = "Brushed_Steel", Diffuse = new Vector3(0.5f), Specular = new Vector3(0.3f), Shininess = 30 });
        Assert.Equal(1f, named.Metallic);

        var window = MaterialApproximation.FromPhong(new PhongMaterial { Name = "Window glass", Diffuse = new Vector3(0.8f), Specular = new Vector3(0.9f), Shininess = 500, Opacity = 0.2f });
        Assert.Equal(0f, window.Metallic);
        Assert.Equal(0.8f, window.Transmission, 3);
        Assert.Equal(AlphaMode.Opaque, window.AlphaMode);

        var fade = MaterialApproximation.FromPhong(new PhongMaterial { Name = "decal", Diffuse = new Vector3(0.5f), Opacity = 0.5f });
        Assert.Equal(AlphaMode.Blend, fade.AlphaMode);
        Assert.Equal(0.5f, fade.BaseColorFactor.W);

        var lamp = MaterialApproximation.FromPhong(new PhongMaterial { Name = "lamp", Emissive = new Vector3(2f, 1f, 0f) });
        Geo.AssertNear(new Vector3(1f, 0.5f, 0f), lamp.EmissiveColor);
        Assert.Equal(2f * MaterialApproximation.EmissiveReferenceLuminance, lamp.EmissiveLuminance);
    }
}
