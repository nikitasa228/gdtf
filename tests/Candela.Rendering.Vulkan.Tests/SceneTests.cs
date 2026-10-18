using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>Retained-scene behaviour: incremental updates keep handles valid; textures and material colours are sampled
/// and converted to linear Rec.2020 correctly.</summary>
public sealed class SceneTests
{
    private const int Width = 160;
    private const int Height = 120;

    [SkippableFact]
    public void IncrementalUpdatesKeepHandlesValid()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var scene = ProceduralTestScene.Build(world);
            var gold = scene.Instances[3];
            var plastic = scene.Instances[4];
            var view = new TopDownView(Width, Height, 12f);
            var colorless = new ViewOptions { Shading = ViewShading.Colorless };
            using var target = renderer.CreateTarget(Width, Height);
            var settings = new PreviewSettings();

            uint PickAt(Vector3 p)
            {
                var (x, y, _) = view.NearestPixel(p);
                return renderer.Pick(target, x, y)?.PickId ?? 0u;
            }

            renderer.Render(target, view.Camera, colorless, settings, 0f);
            Assert.Equal(ProceduralTestScene.PickIds.GoldSphere, PickAt(new Vector3(0.3f, 1.6f, 0f)));

            // Move the gold sphere to the front of the stage.
            var moved = Matrix4x4.CreateScale(0.6f) * Matrix4x4.CreateTranslation(-1.5f, -2.5f, 0.6f);
            world.UpdateInstance(gold, moved, InstanceFlags.CastShadow);
            renderer.Render(target, view.Camera, colorless, settings, 0f);
            Assert.Equal(ProceduralTestScene.PickIds.GoldSphere, PickAt(new Vector3(-1.5f, -2.5f, 0f)));
            Assert.Equal(ProceduralTestScene.PickIds.Floor, PickAt(new Vector3(0.3f, 1.6f, 0f)));

            // Hidden and not-pickable flags.
            world.UpdateInstance(gold, moved, InstanceFlags.Hidden);
            renderer.Render(target, view.Camera, colorless, settings, 0f);
            Assert.Equal(ProceduralTestScene.PickIds.Floor, PickAt(new Vector3(-1.5f, -2.5f, 0f)));
            world.UpdateInstance(gold, moved, InstanceFlags.NotPickable);
            renderer.Render(target, view.Camera, colorless, settings, 0f);
            Assert.Equal(0u, PickAt(new Vector3(-1.5f, -2.5f, 0f)));

            // Destroying one instance leaves the others' handles valid; stale handles are rejected.
            world.DestroyInstance(gold);
            world.DestroyInstance(gold); // no-op
            Assert.Throws<ArgumentException>(() => world.UpdateInstance(gold, Matrix4x4.Identity, InstanceFlags.None));
            world.UpdateInstance(plastic, Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(3.0f, -2.0f, 0.5f), InstanceFlags.None);
            var sphereMesh = world.CreateMesh(ProceduralMeshes.Sphere(0.5f, 24, 12));
            var red = world.CreateMaterial(new MaterialData { Name = "red", BaseColorFactor = new Vector4(1f, 0f, 0f, 1f) }, null);
            var created = world.CreateInstance(sphereMesh, new[] { red }, Matrix4x4.CreateTranslation(0f, -3f, 0.5f), InstanceFlags.None, 99);
            Assert.NotEqual(gold, created);
            renderer.Render(target, view.Camera, colorless, settings, 0f);
            Assert.Equal(ProceduralTestScene.PickIds.PlasticSphere, PickAt(new Vector3(3.0f, -2.0f, 0f)));
            Assert.Equal(99u, PickAt(new Vector3(0f, -3f, 0f)));

            // Materials: reassign and update in place; destroying a used material falls back to the default.
            var light = ProceduralTestScene.Spot(new Vector3(0f, -3f, 4f), new Vector3(0f, -3f, 0f), LightColor.White, 20000f, 40f, 60f);
            world.SetLights(new[] { light });
            var full = new ViewOptions { Shading = ViewShading.Full, ShowGrid = false };
            var (sx, sy, _) = view.NearestPixel(new Vector3(0f, -3f, 0f));
            renderer.Render(target, view.Camera, full, settings, 0f);
            var redColour = renderer.ReadHdr(target).At(sx, sy);
            Assert.True(redColour.X > 5f * redColour.Y, $"red material {redColour}");
            world.UpdateMaterial(red, new MaterialData { Name = "now blue", BaseColorFactor = new Vector4(0f, 0f, 1f, 1f) }, null);
            renderer.Render(target, view.Camera, full, settings, 0f);
            var blueColour = renderer.ReadHdr(target).At(sx, sy);
            Assert.True(blueColour.Z > 5f * blueColour.X, $"updated material {blueColour}");
            var green = world.CreateMaterial(new MaterialData { Name = "green", BaseColorFactor = new Vector4(0f, 1f, 0f, 1f) }, null);
            world.SetInstanceMaterials(created, new[] { green });
            renderer.Render(target, view.Camera, full, settings, 0f);
            var greenColour = renderer.ReadHdr(target).At(sx, sy);
            Assert.True(greenColour.Y > 3f * greenColour.Z, $"reassigned material {greenColour}");
            world.DestroyMaterial(green);
            renderer.Render(target, view.Camera, full, settings, 0f);
            var defaultColour = renderer.ReadHdr(target).At(sx, sy);
            Assert.InRange(MathF.Abs(defaultColour.X - defaultColour.Z) / defaultColour.Y, 0f, 0.05f); // neutral default

            // Lights are replaced per frame: no lights, no pool.
            world.SetLights(ReadOnlySpan<SpotLight>.Empty);
            renderer.Render(target, view.Camera, full, settings, 0f);
            Assert.InRange(renderer.ReadHdr(target).LuminanceAt(sx, sy), 0f, 0.05f);

            world.DestroyMesh(sphereMesh); // instances of a destroyed mesh are skipped
            renderer.Render(target, view.Camera, colorless, settings, 0f);
            Assert.Equal(ProceduralTestScene.PickIds.Floor, PickAt(new Vector3(0f, -3f, 0f)));
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void TexturesAndColoursAreConvertedToLinearRec2020()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var images = new ImageSource
            {
                ["red"] = Uniform(64, 64, 255, 0, 0),
                ["grey"] = Uniform(37, 19, 128, 128, 128), // non-power-of-two mip chain
                ["flat normal"] = Uniform(16, 16, 128, 128, 255),
                ["checker"] = Checkerboard(256, 256),
            };
            var view = new TopDownView(Width, Height, 8f);
            var light = ProceduralTestScene.Spot(new Vector3(0f, 0f, 5f), Vector3.Zero, LightColor.White, 10000f, 50f, 80f);
            float illuminanceOverPi = 10000f / 25f / MathF.PI;

            Vector3 Measure(MaterialData material)
            {
                var floor = world.CreateMesh(ProceduralMeshes.Plane(40f, 40f));
                var handle = world.CreateMaterial(material, images);
                var instance = world.CreateInstance(floor, new[] { handle }, Matrix4x4.Identity, InstanceFlags.None, 1);
                world.SetLights(new[] { light });
                using var target = renderer.CreateTarget(Width, Height);
                renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, new PreviewSettings(), 0f);
                var (x, y, _) = view.NearestPixel(Vector3.Zero);
                var value = renderer.ReadHdr(target).At(x, y) / illuminanceOverPi;
                world.DestroyInstance(instance);
                world.DestroyMaterial(handle);
                world.DestroyMesh(floor);
                return value;
            }

            static MaterialData Lambert(Vector4 factor, string? baseColor = null, string? normal = null) => new()
            {
                BaseColorFactor = factor,
                BaseColorTexture = baseColor,
                NormalTexture = normal,
                Metallic = 0f,
                Roughness = 1f,
                Ior = 1f,
            };

            static void AssertClose(Vector3 expected, Vector3 actual, string what) =>
                Assert.True(Vector3.Distance(expected, actual) <= 0.01f + 0.02f * expected.Length(), $"{what}: {actual}, expected {expected}");

            var red2020 = ColorSpaces.Rec709ToRec2020(new Vector3(1f, 0f, 0f));
            AssertClose(red2020, Measure(Lambert(new Vector4(1f, 0f, 0f, 1f))), "linear Rec.709 red factor");
            AssertClose(red2020, Measure(Lambert(Vector4.One, "red")), "sRGB red texture");
            float grey = ColorSpaces.SrgbDecode(128f / 255f);
            AssertClose(new Vector3(grey), Measure(Lambert(Vector4.One, "grey")), "sRGB 128 grey texture");
            AssertClose(new Vector3(0.5f * grey), Measure(Lambert(new Vector4(0.5f, 0.5f, 0.5f, 1f), "grey")), "texture × factor");
            AssertClose(Vector3.One, Measure(Lambert(Vector4.One, normal: "flat normal")), "flat normal map (generated tangents)");
            // A texture key the image source does not know: the white default texture.
            AssertClose(Vector3.One, Measure(Lambert(Vector4.One, "missing")), "missing texture");

            // Minified far enough that only mips averaging whole squares are sampled: the mean of the linear values
            // (mips are generated with linear filtering, which decodes sRGB before averaging).
            float checkerMean = 0.5f * (ColorSpaces.SrgbDecode(220f / 255f) + ColorSpaces.SrgbDecode(30f / 255f));
            AssertClose(new Vector3(checkerMean), Measure(Lambert(Vector4.One, "checker")), "minified checker texture");

            // Explicit textures (gobos) with full mip chains, destroyed while frames are in flight.
            var checker = world.CreateTexture(Checkerboard(256, 256), srgb: true);
            var data = world.CreateTexture(Uniform(5, 3, 10, 20, 30), srgb: false);
            Assert.True(checker.IsValid && data.IsValid && checker != data);
            Measure(Lambert(Vector4.One));
            world.DestroyTexture(checker);
            world.DestroyTexture(data);
            world.DestroyTexture(data); // no-op
            Assert.Equal(0, renderer.VulkanWorld.Statistics.Textures);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>A 220/30 grey checkerboard with 8-pixel squares (sRGB-encoded RGBA8).</summary>
    internal static ImageData Checkerboard(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = ((x / 8) + (y / 8)) % 2 == 0 ? (byte)220 : (byte)30;
                int i = (y * width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }

        return new ImageData { Width = width, Height = height, Pixels = pixels };
    }

    /// <summary>A uniform RGBA8 image.</summary>
    internal static ImageData Uniform(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return new ImageData { Width = width, Height = height, Pixels = pixels };
    }

    /// <summary>Texture keys → images for <see cref="IRenderWorld.CreateMaterial"/>.</summary>
    internal sealed class ImageSource : Dictionary<string, ImageData>, IImageSource
    {
        public ImageData? Load(string key) => TryGetValue(key, out var image) ? image : null;
    }
}
