using System.Numerics;
using System.Text.RegularExpressions;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The reported "dark squares that follow the camera and flicker" around a bright lens in haze. On the reporter's GPU a
/// non-finite value got into the light images near the lens — a blend that overflowed the half floats to +infinity, a
/// texture filter that turned an infinity into NaN, a NaN history — and the sanitising relied on isnan(), which a GPU
/// compiler assuming finite math may fold to false; NaN then spread through the froxel columns (8-pixel squares, moving
/// with the froxel jitter) and the bloom pyramid (squares of every level's size). Lavapipe clamps the half-float
/// conversion and keeps isnan(), so it never showed them. The fix tests on the bits, sanitises everything a pass loads,
/// filters the HDR image in the shader for the bloom and a scaled display, and keeps every writer of the blended HDR
/// attachment below a fifth of the half-float range. These tests look straight into a very bright lens at close range
/// through dense haze, over several frames with every temporal history, and also with the histories filled with NaN and
/// infinities (as stale or uninitialised memory may be): every image stays finite and no tile of the picture drops out.
/// </summary>
public sealed class LensGlareTests
{
    private readonly ITestOutputHelper _output;

    public LensGlareTests(ITestOutputHelper output) => _output = output;

    private const int Width = 192;
    private const int Height = 128;
    private const int Tile = 8;

    private static readonly Vector3 LensPosition = new(0f, 0f, 2f);

    private static (VulkanRenderer Renderer, RenderCamera Camera) Scene(float distance)
    {
        var renderer = TestRenderers.CreateOrSkip();
        var scene = new StageScene(renderer);
        scene.Floor(40f, scene.Lambert(new Vector3(0.3f)));
        // The lens: a thin disc (box) emitting the beam's peak luminance towards the camera, like RenderSync's lens glow.
        var black = scene.Lambert(new Vector3(0.02f));
        var lens = scene.Box(new Vector3(0.16f, 0.01f, 0.16f), LensPosition, black);
        var cameraPosition = LensPosition - new Vector3(0f, distance, 0f) + new Vector3(0.02f, 0f, 0.01f);
        var light = StageScene.Spot(LensPosition, cameraPosition, 3e6f, 12f, 18f, LightColor.FromCct(6500f), shadows: true, type: BeamType.Spot);
        light.SourceRadius = 0.08f;
        // A flood of 170° above the space between camera and lens lights the haze through the froxel volume (cones up to
        // 160° are volumetric beams, integrated per pixel), so the froxel history matters too.
        var above = (LensPosition + cameraPosition) * 0.5f + new Vector3(0f, 0f, 2f);
        var flood = StageScene.Spot(above, above - new Vector3(0f, 0f, 4f), 4e4f, 150f, 170f, LightColor.FromCct(3200f), shadows: false);
        renderer.World.SetLights(new[] { light, flood });
        renderer.World.SetInstanceEmission(lens, LightColor.FromCct(6500f).Rgb * (3e6f / (MathF.PI * 0.08f * 0.08f)), -Vector3.UnitY, 0.25f);
        renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0.12f, Anisotropy = 0.7f });
        var camera = RenderCamera.LookAt(cameraPosition, LensPosition, 60f);
        camera.Ev100 = 9f;
        return (renderer, camera);
    }

    private static PreviewSettings Settings(bool autoExposure = false, float resolutionScale = 1f,
        IndirectLighting indirect = IndirectLighting.ReflectiveShadowMaps) => new()
        {
            VolumetricSlices = 64,
            TemporalAccumulation = true,
            Bloom = true,
            BloomIntensity = 0.3f,
            AutoExposure = autoExposure,
            ResolutionScale = resolutionScale,
            Indirect = indirect,
        };

    private static float[] Luma(ImageData image)
    {
        var luma = new float[image.Width * image.Height];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = 0.2126f * image.Pixels[4 * i] + 0.7152f * image.Pixels[4 * i + 1] + 0.0722f * image.Pixels[4 * i + 2];
        return luma;
    }

    /// <summary>Mean display luminance (0–255) of every 8 × 8 tile.</summary>
    private static float[,] TileMeans(ImageData image)
    {
        var luma = Luma(image);
        int tx = image.Width / Tile, ty = image.Height / Tile;
        var means = new float[tx, ty];
        for (int y = 0; y < ty * Tile; y++)
            for (int x = 0; x < tx * Tile; x++)
                means[x / Tile, y / Tile] += luma[y * image.Width + x] / (Tile * Tile);
        return means;
    }

    /// <summary>
    /// Tiles much darker than every one of their four neighbours while those are lit: the blocks the report showed (a
    /// smooth glow in haze has no tile darker than all of its neighbours by half).
    /// </summary>
    private static List<string> DarkBlocks(ImageData image)
    {
        var means = TileMeans(image);
        int tx = means.GetLength(0), ty = means.GetLength(1);
        var blocks = new List<string>();
        for (int y = 1; y < ty - 1; y++)
        {
            for (int x = 1; x < tx - 1; x++)
            {
                float darkestNeighbour = MathF.Min(MathF.Min(means[x - 1, y], means[x + 1, y]), MathF.Min(means[x, y - 1], means[x, y + 1]));
                if (darkestNeighbour > 40f && means[x, y] < 0.5f * darkestNeighbour)
                    blocks.Add($"tile ({x}, {y}): {means[x, y]:0} among ≥ {darkestNeighbour:0}");
            }
        }

        return blocks;
    }

    private static void AssertFiniteImages(VulkanRenderer renderer, IRenderTarget target, string what)
    {
        var hdr = renderer.ReadHdr(target);
        float maxExposed = 0f;
        for (int i = 0; i < hdr.Rgba.Length; i++)
        {
            Assert.True(float.IsFinite(hdr.Rgba[i]), $"{what}: HDR value {hdr.Rgba[i]} at {i / 4}");
            if ((i & 3) != 3)
                maxExposed = MathF.Max(maxExposed, hdr.Rgba[i] * hdr.Exposure);
        }

        // The blended attachment stays below the half-float maximum with room for more layers.
        Assert.True(maxExposed <= 5f * 12000f, $"{what}: pre-exposed HDR reaches {maxExposed}");
        var bloom = renderer.ReadBloom(target);
        Assert.All(bloom.Rgba, v => Assert.True(float.IsFinite(v), $"{what}: bloom value {v}"));
    }

    [SkippableTheory]
    [InlineData(0.6f, false, 1f)]
    [InlineData(1.5f, true, 1f)]
    [InlineData(1.5f, false, 0.75f)]
    public void Looking_straight_into_a_bright_lens_in_haze_gives_finite_images_without_dark_blocks(float distance, bool autoExposure, float scale)
    {
        var (renderer, camera) = Scene(distance);
        try
        {
            using var target = renderer.CreateTarget(Width, Height);
            var view = new ViewOptions { ShowGrid = false };
            var settings = Settings(autoExposure, scale);
            float[,]? previous = null;
            for (int frame = 0; frame < 10; frame++)
            {
                renderer.Render(target, camera, view, settings, 1f / 30f);
                var image = renderer.ReadPixels(target);
                if (frame == 9)
                    ImageFiles.WritePng(image, Path.Combine(TestRenderers.OutputDirectory, $"lens-glare-{distance:0.0}-{(autoExposure ? "auto" : "ev9")}-{scale:0.00}.png"));
                if (frame < 3)
                    continue;
                AssertFiniteImages(renderer, target, $"frame {frame}");
                var blocks = DarkBlocks(image);
                Assert.True(blocks.Count == 0, $"frame {frame}: {string.Join("; ", blocks.Take(5))}");

                // Nothing moves: after the histories settled no tile may flicker from frame to frame.
                var means = TileMeans(image);
                if (previous is not null && frame >= 6)
                {
                    for (int y = 0; y < means.GetLength(1); y++)
                    {
                        for (int x = 0; x < means.GetLength(0); x++)
                        {
                            float change = MathF.Abs(means[x, y] - previous[x, y]);
                            Assert.True(change <= 6f + 0.08f * previous[x, y], $"frame {frame}: tile ({x}, {y}) flickers {previous[x, y]:0} -> {means[x, y]:0}");
                        }
                    }
                }

                previous = means;
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Histories_holding_non_finite_values_do_not_reach_the_picture(float poison)
    {
        // Two targets render the same frames; one of them gets every temporal history (froxels, beams, indirect light)
        // overwritten with the value before a frame that reads them. It must still give a finite picture close to the
        // other's, and the next frames converge back to it.
        var (renderer, camera) = Scene(3.0f);
        try
        {
            using var clean = renderer.CreateTarget(Width, Height);
            using var poisoned = renderer.CreateTarget(Width, Height);
            var view = new ViewOptions { ShowGrid = false };
            var settings = Settings(indirect: IndirectLighting.RayTraced);
            for (int frame = 0; frame < 4; frame++)
            {
                renderer.Render(clean, camera, view, settings, 1f / 30f);
                renderer.Render(poisoned, camera, view, settings, 1f / 30f);
            }

            ((VulkanRenderTarget)poisoned).HistoryFillForTests = poison;
            for (int frame = 0; frame < 3; frame++)
            {
                renderer.Render(clean, camera, view, settings, 1f / 30f);
                renderer.Render(poisoned, camera, view, settings, 1f / 30f);
                AssertFiniteImages(renderer, poisoned, $"{poison}, frame {frame}");
                var a = renderer.ReadPixels(clean);
                var b = renderer.ReadPixels(poisoned);
                Assert.Empty(DarkBlocks(b));
                double sum = 0;
                for (int i = 0; i < a.Pixels.Length; i++)
                    sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
                double mean = sum / a.Pixels.Length;
                _output.WriteLine($"{poison}, frame {frame}: mean difference {mean:0.000}/255");
                Assert.True(mean < 4.0, $"{poison}, frame {frame}: the picture differs by {mean:0.00}/255 on average");
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(1f)]
    [InlineData(0.75f)]
    public void An_overflowed_hdr_block_stays_bright_in_the_bloom_and_the_picture(float scale)
    {
        // A GPU that does not clamp half floats stores +infinity where a blend overflows (lavapipe clamps, so the HDR
        // image is given such a block directly). Filtered by the texture unit, an infinity becomes NaN; the bloom and the
        // scaled display filter checked texels instead, so the block is the brightest part of the picture and of the
        // glow, never a hole.
        var (renderer, camera) = Scene(1.5f);
        try
        {
            using var clean = renderer.CreateTarget(Width, Height);
            using var overflow = renderer.CreateTarget(Width, Height);
            var view = new ViewOptions { ShowGrid = false };
            var settings = Settings(resolutionScale: scale);
            for (int frame = 0; frame < 3; frame++)
            {
                renderer.Render(clean, camera, view, settings, 1f / 30f);
                renderer.Render(overflow, camera, view, settings, 1f / 30f);
            }

            int rw = (int)MathF.Round(Width * scale), rh = (int)MathF.Round(Height * scale);
            var block = new System.Drawing.Rectangle(rw / 2 - 5, rh / 2 - 5, 11, 11);
            ((VulkanRenderTarget)overflow).HdrOverflowForTests = block;
            renderer.Render(clean, camera, view, settings, 1f / 30f);
            renderer.Render(overflow, camera, view, settings, 1f / 30f);

            var bloom = renderer.ReadBloom(overflow);
            var cleanBloom = renderer.ReadBloom(clean);
            Assert.All(bloom.Rgba, v => Assert.True(float.IsFinite(v), $"bloom value {v}"));
            // Around the block the glow is at least as bright as without the overflow (an infinity is "very bright").
            for (int y = 0; y < bloom.Height; y++)
            {
                for (int x = 0; x < bloom.Width; x++)
                {
                    float a = bloom.LuminanceAt(x, y), b = cleanBloom.LuminanceAt(x, y);
                    Assert.True(a >= 0.9f * b - 1e-3f, $"bloom texel ({x}, {y}) darkened by the overflow: {a} < {b}");
                }
            }

            var picture = renderer.ReadPixels(overflow);
            Assert.Empty(DarkBlocks(picture));
            var luma = Luma(picture);
            // The display pixels over the block (inside it by a pixel, scaled to the display) show it at full brightness.
            for (int y = (int)((block.Top + 1) / scale); y < (int)((block.Bottom - 1) / scale); y++)
            {
                for (int x = (int)((block.Left + 1) / scale); x < (int)((block.Right - 1) / scale); x++)
                    Assert.True(luma[y * picture.Width + x] > 200f, $"display pixel ({x}, {y}) over the overflowed block is {luma[y * picture.Width + x]:0}");
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [Fact]
    public void No_shader_relies_on_isnan_or_self_comparison_to_catch_non_finite_values()
    {
        // GPU compilers may assume finite math and fold isnan(x), isinf(x), x != x and equal(x, x) away; the shaders test
        // the bits (common.glsl: isNonFinite, allFinite, finiteClamp, finiteLight).
        var library = ShaderLibrary.Default;
        var offenders = new List<string>();
        var selfComparison = new Regex(@"(?:equal|notEqual)\s*\(\s*(\w+)\s*,\s*\1\s*\)|\b(\w+)\s*(?:!=|==)\s*\2\b");
        foreach (string name in library.StageFiles.Concat(library.StageFiles.SelectMany(library.IncludeClosure)).Distinct())
        {
            string? source = library.TryGetSource(name);
            if (source is null)
                continue;
            int line = 0;
            foreach (string text in source.Split('\n'))
            {
                line++;
                string code = text.Split("//")[0];
                if (Regex.IsMatch(code, @"\bis(?:nan|inf)\s*\(") || selfComparison.IsMatch(code))
                    offenders.Add($"{name}:{line}: {text.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}
