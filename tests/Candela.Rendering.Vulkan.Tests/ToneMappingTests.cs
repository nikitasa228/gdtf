using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// The display transform: a neutral emissive surface of known luminance, pre-exposed with the camera EV, comes out of
/// each tone mapper exactly as the documented curve says (CPU reference implementations for achromatic input below),
/// sRGB-encoded into the 8-bit display image.
/// </summary>
public sealed class ToneMappingTests
{
    private const float Ev100 = 7f;

    [SkippableTheory]
    [InlineData(ToneMapper.AgX)]
    [InlineData(ToneMapper.Aces)]
    [InlineData(ToneMapper.Neutral)]
    public void NeutralGreyFollowsTheDocumentedCurve(ToneMapper toneMapper)
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var world = renderer.World;
            var panel = world.CreateMesh(ProceduralMeshes.Plane(20f, 20f));
            var view = new TopDownView(32, 32, 4f, Ev100);
            using var target = renderer.CreateTarget(32, 32);
            float exposure = Core.Math.Units.ExposureFromEv100(Ev100);
            foreach (float exposed in new[] { 0.02f, 0.18f, 0.5f, 1f, 4f, 20f })
            {
                var material = world.CreateMaterial(new MaterialData
                {
                    BaseColorFactor = new Vector4(0f, 0f, 0f, 1f),
                    Roughness = 1f,
                    Ior = 1f,
                    EmissiveColor = Vector3.One,
                    EmissiveLuminance = exposed / exposure,
                }, null);
                var instance = world.CreateInstance(panel, new[] { material }, Matrix4x4.Identity, InstanceFlags.None, 1);
                renderer.Render(target, view.Camera, new ViewOptions { ShowGrid = false }, new PreviewSettings { ToneMapper = toneMapper }, 0f);
                var image = renderer.ReadPixels(target);
                int i = (16 * 32 + 16) * 4;
                float expected = 255f * ColorSpaces.SrgbEncode(Reference(toneMapper, exposed));
                for (int c = 0; c < 3; c++)
                    Assert.InRange(image.Pixels[i + c], expected - 2.5f, expected + 2.5f); // ± 1 LSB dither + rounding
                world.DestroyInstance(instance);
                world.DestroyMaterial(material);
            }

            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void ExposureCompensationIsAddedToTheCameraEv()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            ProceduralTestScene.Build(renderer.World);
            using var target = renderer.CreateTarget(64, 48);
            var camera = ProceduralTestScene.CreateCamera();
            renderer.Render(target, camera, new ViewOptions(), new PreviewSettings { ExposureCompensation = 1.5f }, 0f);
            var hdr = renderer.ReadHdr(target);
            Assert.Equal(Core.Math.Units.ExposureFromEv100(camera.Ev100 + 1.5f), hdr.Exposure, 6);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>CPU reference of the display curves (docs/rendering.md) for an achromatic exposed value.</summary>
    private static float Reference(ToneMapper toneMapper, float x) => toneMapper switch
    {
        ToneMapper.Aces => Math.Clamp(AcesCurve(x), 0f, 1f),
        ToneMapper.Neutral => Neutral(x),
        _ => AgX(x),
    };

    // The AgX inset/outset and ACES input/output matrices have rows summing to 1, so a grey stays grey through them.
    private static float AgX(float x)
    {
        const float minEv = -12.47393f, maxEv = 4.026069f;
        float t = Math.Clamp((MathF.Log2(MathF.Max(x, 1e-10f)) - minEv) / (maxEv - minEv), 0f, 1f);
        float t2 = t * t, t4 = t2 * t2;
        float curve = 15.5f * t4 * t2 - 40.14f * t4 * t + 31.96f * t4 - 6.868f * t2 * t + 0.4298f * t2 + 0.1191f * t - 0.00232f;
        return Math.Clamp(MathF.Pow(MathF.Max(curve, 0f), 2.2f), 0f, 1f);
    }

    private static float AcesCurve(float c) => (c * (c + 0.0245786f) - 0.000090537f) / (c * (0.983729f * c + 0.4329510f) + 0.238081f);

    private static float Neutral(float x)
    {
        const float start = 0.76f, d = 0.24f;
        float offset = x < 0.08f ? x - 6.25f * x * x : 0.04f;
        float peak = x - offset;
        if (peak < start)
            return Math.Clamp(peak, 0f, 1f);
        float newPeak = 1f - d * d / (peak + d - start);
        return Math.Clamp(newPeak, 0f, 1f); // a grey has nothing to desaturate
    }
}
