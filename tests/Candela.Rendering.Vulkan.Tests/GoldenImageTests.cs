using System.Reflection;
using Candela.Core.Geometry;
using StbImageSharp;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The procedural scene at 320 × 240 in every view mode against reference PNGs in <c>Golden/</c>. The comparison
/// tolerates small driver differences (rasterisation rules, rounding, transcendental precision): every pixel is compared
/// with the best-matching pixel of the 3 × 3 neighbourhood in the other image (both directions), and the test fails
/// when the mean of that error or the share of clearly different pixels exceeds the thresholds.
/// Regenerate the references with <c>CANDELA_UPDATE_GOLDEN=1 dotnet test --filter GoldenImageTests</c> and look at
/// them before committing.
/// </summary>
public sealed class GoldenImageTests
{
    private const int Width = 320;
    private const int Height = 240;

    /// <summary>Largest allowed mean neighbourhood error, 8-bit units.</summary>
    private const double MaxMeanError = 2.0;

    /// <summary>A pixel is "different" when its neighbourhood error exceeds this (8-bit units, max over RGB).</summary>
    private const int PixelThreshold = 40;

    /// <summary>Largest allowed share of different pixels.</summary>
    private const double MaxDifferentFraction = 0.005;

    [SkippableTheory]
    [InlineData(ViewShading.Full, false)]
    [InlineData(ViewShading.Colorless, false)]
    [InlineData(ViewShading.Wireframe, false)]
    [InlineData(ViewShading.Wireframe, true)]
    public void ProceduralSceneMatchesGoldenImage(ViewShading shading, bool edgeShader)
    {
        var renderer = TestRenderers.CreateOrSkip(options: new VulkanRendererOptions { ForceEdgeShaderWireframe = edgeShader });
        try
        {
            ProceduralTestScene.Build(renderer.World);
            using var target = renderer.CreateTarget(Width, Height);
            var camera = ProceduralTestScene.CreateCamera();
            renderer.Render(target, camera, new ViewOptions { Shading = shading }, new PreviewSettings(), 1f / 60f);
            var actual = renderer.ReadPixels(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);

            // The polygon-mode and edge-shader wireframes look different: pick the reference of the path in use.
            string name = shading switch
            {
                ViewShading.Wireframe when renderer.UsesEdgeShaderWireframe => "wireframe-edges",
                ViewShading.Wireframe => "wireframe",
                ViewShading.Colorless => "colorless",
                _ => "full",
            };
            ImageFiles.WritePng(actual, Path.Combine(TestRenderers.OutputDirectory, $"golden-actual-{name}.png"));
            CompareWithGolden(actual, name);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [Fact]
    public void ComparisonToleratesOnePixelShiftsButNotRealChanges()
    {
        // 60 px wide: the shifted checker has the same colours at the image border (only interior edges move).
        var reference = Checker(60, 48, shift: 0);
        Assert.True(Compare(reference, reference).Passed);
        // A 1-pixel shift of every edge passes.
        var shifted = Checker(60, 48, shift: 1);
        Assert.True(Compare(shifted, reference).Passed);
        // A wrong-coloured block of 8 × 8 pixels (2 % of the image) fails.
        var broken = (byte[])reference.Pixels.Clone();
        for (int y = 20; y < 28; y++)
        {
            for (int x = 20; x < 28; x++)
            {
                int i = (y * 60 + x) * 4;
                broken[i] = 255;
                broken[i + 1] = 0;
                broken[i + 2] = 0;
            }
        }

        Assert.False(Compare(new ImageData { Width = 60, Height = 48, Pixels = broken }, reference).Passed);
    }

    private static ImageData Checker(int width, int height, int shift)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool on = (((x + shift) / 8) + (y / 8)) % 2 == 0;
                int i = (y * width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = on ? (byte)220 : (byte)30;
                pixels[i + 3] = 255;
            }
        }

        return new ImageData { Width = width, Height = height, Pixels = pixels };
    }

    /// <summary>
    /// Compares <paramref name="actual"/> with <c>Golden/&lt;name&gt;.png</c> (or writes it there when
    /// <c>CANDELA_UPDATE_GOLDEN=1</c>); the image is also written to the test output folder for inspection.
    /// </summary>
    internal static void CompareWithGolden(ImageData actual, string name)
    {
        ImageFiles.WritePng(actual, Path.Combine(TestRenderers.OutputDirectory, $"golden-actual-{name}.png"));
        string file = $"{name}.png";
        if (Environment.GetEnvironmentVariable("CANDELA_UPDATE_GOLDEN") == "1")
        {
            string source = GoldenSourceDirectory();
            ImageFiles.WritePng(actual, Path.Combine(source, file));
            ImageFiles.WritePng(actual, Path.Combine(AppContext.BaseDirectory, "Golden", file));
            return;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "Golden", file);
        Assert.True(File.Exists(path), $"Missing golden image {file}; create it with CANDELA_UPDATE_GOLDEN=1 and check it visually.");
        var decoded = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        var golden = new ImageData { Width = decoded.Width, Height = decoded.Height, Pixels = decoded.Data };
        Assert.Equal(golden.Width, actual.Width);
        Assert.Equal(golden.Height, actual.Height);

        var result = Compare(actual, golden);
        if (!result.Passed)
            ImageFiles.WritePng(result.Difference, Path.Combine(TestRenderers.OutputDirectory, $"golden-diff-{name}.png"));
        Assert.True(result.Passed,
            $"{file}: mean neighbourhood error {result.MeanError:F2} (max {MaxMeanError}), {result.DifferentFraction:P2} pixels differ by more " +
            $"than {PixelThreshold} (max {MaxDifferentFraction:P2}); see golden-diff-{name}.png in {TestRenderers.OutputDirectory}.");
    }

    private static string GoldenSourceDirectory()
    {
        var attribute = typeof(GoldenImageTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GoldenSourceDirectory");
        return attribute?.Value ?? Path.Combine(AppContext.BaseDirectory, "Golden");
    }

    private readonly record struct Comparison(double MeanError, double DifferentFraction, ImageData Difference)
    {
        public bool Passed => MeanError <= MaxMeanError && DifferentFraction <= MaxDifferentFraction;
    }

    /// <summary>Symmetric 3 × 3 neighbourhood comparison of two RGBA8 images of the same size (RGB only).</summary>
    private static Comparison Compare(ImageData actual, ImageData golden)
    {
        int w = actual.Width, h = actual.Height;
        var difference = new byte[w * h * 4];
        double sum = 0;
        int different = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int e = Math.Max(NeighbourhoodError(actual.Pixels, golden.Pixels, w, h, x, y),
                    NeighbourhoodError(golden.Pixels, actual.Pixels, w, h, x, y));
                sum += e;
                if (e > PixelThreshold)
                    different++;
                int i = (y * w + x) * 4;
                difference[i] = (byte)Math.Min(255, e * 4);
                difference[i + 1] = difference[i + 2] = (byte)(e > PixelThreshold ? 0 : Math.Min(255, e * 4));
                difference[i + 3] = 255;
            }
        }

        double count = (double)w * h;
        return new Comparison(sum / count, different / count, new ImageData { Width = w, Height = h, Pixels = difference });
    }

    /// <summary>Smallest max-channel difference between pixel (x, y) of <paramref name="a"/> and the 3 × 3 pixels around it in <paramref name="b"/>.</summary>
    private static int NeighbourhoodError(byte[] a, byte[] b, int w, int h, int x, int y)
    {
        int ia = (y * w + x) * 4;
        int best = int.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
        {
            int yy = y + dy;
            if (yy < 0 || yy >= h)
                continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx;
                if (xx < 0 || xx >= w)
                    continue;
                int ib = (yy * w + xx) * 4;
                int e = Math.Max(Math.Abs(a[ia] - b[ib]), Math.Max(Math.Abs(a[ia + 1] - b[ib + 1]), Math.Abs(a[ia + 2] - b[ib + 2])));
                if (e < best)
                    best = e;
            }
        }

        return best;
    }
}
