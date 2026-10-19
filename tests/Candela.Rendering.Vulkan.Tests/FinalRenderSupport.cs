using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>Helpers of the path tracer tests: renderers with ray tracing, renders run to completion, HDR read-backs.</summary>
internal static class FinalRenderSupport
{
    /// <summary>A validated renderer on a device with ray query, or the test is skipped.</summary>
    public static VulkanRenderer CreateOrSkip()
    {
        var renderer = TestRenderers.CreateOrSkip();
        if (!renderer.Info.SupportsRayTracing)
        {
            TestRenderers.DisposeAndAssertClean(renderer);
            Skip.If(true, "The device has no ray query support (the path tracer needs it).");
        }

        return renderer;
    }

    /// <summary>Renders to completion (generous budgets per call) and returns the render.</summary>
    public static VulkanFinalRender RenderToCompletion(VulkanRenderer renderer, RenderCamera camera, FinalRenderSettings settings,
        FinalRenderOptions? options = null)
    {
        var render = renderer.StartFinalRender(camera, settings, options ?? new FinalRenderOptions());
        for (int i = 0; i < 100000 && !render.IsComplete; i++)
            render.Advance(TimeSpan.FromSeconds(5));
        Assert.True(render.IsComplete);
        return render;
    }

    /// <summary>Settings of a small reference render (no denoiser).</summary>
    public static FinalRenderSettings Settings(int width, int height, int samples, int bounces = 6, int volumeBounces = 2) => new()
    {
        Width = width,
        Height = height,
        SamplesPerPixel = samples,
        MaxBounces = bounces,
        MaxVolumeBounces = volumeBounces,
        Denoise = false,
    };

    /// <summary>Linear RGBA floats of an HDR image divided by the exposure: cd/m² per Rec.2020 channel.</summary>
    public static HdrImage Luminance(ImageData hdr, float exposure)
    {
        Assert.Equal(PixelFormat.RgbaFloat, hdr.Format);
        var values = new float[hdr.Width * hdr.Height * 4];
        Buffer.BlockCopy(hdr.Pixels, 0, values, 0, hdr.Pixels.Length);
        for (int i = 0; i < values.Length; i++)
        {
            if ((i & 3) != 3)
                values[i] /= exposure;
        }

        return new HdrImage(hdr.Width, hdr.Height, values);
    }

    /// <summary>Writes the tone-mapped preview for inspection.</summary>
    public static void Save(VulkanFinalRender render, string name) =>
        ImageFiles.WritePng(render.Preview(), Path.Combine(TestRenderers.OutputDirectory, name + ".png"));

    /// <summary>Creates a floor (Z = 0) of the given material.</summary>
    public static InstanceHandle Floor(IRenderWorld world, MaterialData material, float size = 40f)
    {
        var mesh = world.CreateMesh(ProceduralMeshes.Plane(size, size));
        var handle = world.CreateMaterial(material, null);
        return world.CreateInstance(mesh, new[] { handle }, Matrix4x4.Identity, InstanceFlags.CastShadow, 1);
    }
}

/// <summary>A linear HDR image in cd/m² (Rec.2020 RGB + alpha), top row first.</summary>
internal sealed record HdrImage(int Width, int Height, float[] Rgba)
{
    public Vector3 At(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return new Vector3(Rgba[i], Rgba[i + 1], Rgba[i + 2]);
    }

    public float LuminanceAt(int x, int y) => ColorSpaces.LuminanceRec2020(At(x, y));

    /// <summary>Mean luminance over a square block of pixels centred on (x, y).</summary>
    public float MeanLuminance(int x, int y, int radius)
    {
        double sum = 0;
        int n = 0;
        for (int j = Math.Max(y - radius, 0); j <= Math.Min(y + radius, Height - 1); j++)
        {
            for (int i = Math.Max(x - radius, 0); i <= Math.Min(x + radius, Width - 1); i++)
            {
                sum += LuminanceAt(i, j);
                n++;
            }
        }

        return (float)(sum / Math.Max(n, 1));
    }

    /// <summary>Mean colour over a square block of pixels centred on (x, y).</summary>
    public Vector3 MeanColor(int x, int y, int radius)
    {
        var sum = Vector3.Zero;
        int n = 0;
        for (int j = Math.Max(y - radius, 0); j <= Math.Min(y + radius, Height - 1); j++)
        {
            for (int i = Math.Max(x - radius, 0); i <= Math.Min(x + radius, Width - 1); i++)
            {
                sum += At(i, j);
                n++;
            }
        }

        return sum / Math.Max(n, 1);
    }
}
