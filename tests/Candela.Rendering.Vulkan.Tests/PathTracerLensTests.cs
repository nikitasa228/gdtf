using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Lights with large lenses, where BSDF samples hit the lens often and the result depends on the multiple importance
/// sampling between next-event estimation and BSDF sampling (and on the light-selection probabilities in its weights):
/// the floor under two overlapping soft lights matches the numerical integral over the lens polygons, for a Lambertian
/// and a glossy floor.
/// </summary>
public sealed class PathTracerLensTests
{
    private const int Size = 48;
    private const float LensRadius = 0.35f;
    private const float LightHeight = 1.4f;

    private readonly ITestOutputHelper _output;

    public PathTracerLensTests(ITestOutputHelper output) => _output = output;

    private static SpotLight[] Lights()
    {
        var a = ProceduralTestScene.Spot(new Vector3(-0.45f, 0f, LightHeight), new Vector3(-0.45f, 0.2f, 0f), LightColor.White, 8000f, 70f, 110f);
        var b = ProceduralTestScene.Spot(new Vector3(0.5f, 0.1f, LightHeight), new Vector3(0.6f, 0.1f, 0f), LightColor.White, 5000f, 60f, 100f);
        a.SourceRadius = LensRadius;
        b.SourceRadius = LensRadius;
        return new[] { a, b };
    }

    /// <summary>The BRDF of brdf.glsl for a grey material (1/sr).</summary>
    internal static double Brdf(Vector3 n, Vector3 v, Vector3 l, float albedo, float metallic, float roughness, float dielectricF0)
    {
        float nl = Math.Clamp(Vector3.Dot(n, l), 0f, 1f);
        float nv = Math.Clamp(MathF.Abs(Vector3.Dot(n, v)), 1e-4f, 1f);
        var h = Vector3.Normalize(v + l);
        float nh = Math.Clamp(Vector3.Dot(n, h), 0f, 1f);
        float vh = Math.Clamp(Vector3.Dot(v, h), 0f, 1f);
        float f0 = dielectricF0 + (albedo - dielectricF0) * metallic;
        float f90 = Math.Clamp(50f * f0, 0f, 1f);
        float fresnel = f0 + (f90 - f0) * MathF.Pow(1f - vh, 5f);
        float alpha = MathF.Max(roughness * roughness, 0.002f);
        float a2 = alpha * alpha;
        float f = (nh * a2 - nh) * nh + 1f;
        float d = a2 / (MathF.PI * f * f);
        float gv = nl * MathF.Sqrt(nv * nv * (1f - a2) + a2);
        float gl = nv * MathF.Sqrt(nl * nl * (1f - a2) + a2);
        float vis = 0.5f / MathF.Max(gv + gl, 1e-7f);
        return d * vis * fresnel + (1f - fresnel) * (1f - metallic) * albedo / MathF.PI;
    }

    /// <summary>
    /// Reflected luminance (cd/m²) at floor point <paramref name="p"/> seen from straight above: the path tracer's lens
    /// model, L(y→x) = I(θ_c) |x − y|² / (A cos θ_y |x − c|²), integrated over the lens polygon of every light.
    /// </summary>
    private static double Reference(SpotLight[] lights, Vector3 p, Func<Vector3, double> brdfCos)
    {
        int n = (int)ShaderConstants.PtDiscSegments;
        float factor = PathTracerScene.PolygonRadiusFactor;
        double total = 0;
        foreach (var light in lights)
        {
            var beam = BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle);
            var toP = p - light.Position;
            float dc = toP.Length();
            float theta = MathF.Acos(Math.Clamp(Vector3.Dot(toP / dc, light.Direction), -1f, 1f));
            double intensity = light.PeakCandela * beam.Relative(theta);
            if (intensity <= 0)
                continue;
            var right = Vector3.Cross(light.Direction, light.Up);
            var centre = light.Position + light.Direction * PathTracerScene.LensOffset;
            float r = light.SourceRadius * factor;
            const int radial = 24, angular = 24;
            double sum = 0;
            int count = 0;
            for (int k = 0; k < n; k++)
            {
                float phi0 = 2f * MathF.PI * k / n, phi1 = 2f * MathF.PI * (k + 1) / n;
                var a = centre + r * (MathF.Cos(phi0) * right + MathF.Sin(phi0) * light.Up);
                var b = centre + r * (MathF.Cos(phi1) * right + MathF.Sin(phi1) * light.Up);
                for (int i = 0; i < radial; i++)
                {
                    for (int j = 0; j < angular; j++)
                    {
                        // uniform points of the triangle (centre, a, b), stratified
                        float su = MathF.Sqrt((i + 0.5f) / radial);
                        float b0 = 1f - su, b1 = (j + 0.5f) / angular * su;
                        var y = b0 * centre + b1 * a + (1f - b0 - b1) * b;
                        var l = Vector3.Normalize(y - p);
                        sum += brdfCos(l);
                        count++;
                    }
                }
            }

            total += intensity / (dc * dc) * sum / count;
        }

        return total;
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FloorUnderOverlappingSoftLightsMatchesTheLensIntegral(bool glossy)
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var view = new TopDownView(Size, Size, 3f);
            float albedo = glossy ? 0.9f : 1f, metallic = glossy ? 1f : 0f, roughness = glossy ? 0.35f : 1f, ior = glossy ? 1.5f : 1f;
            FinalRenderSupport.Floor(renderer.World, new MaterialData
            {
                Name = glossy ? "glossy metal" : "white",
                BaseColorFactor = new Vector4(albedo, albedo, albedo, 1f),
                Metallic = metallic,
                Roughness = roughness,
                Ior = ior,
            });
            var lights = Lights();
            renderer.World.SetLights(lights);
            using var render = FinalRenderSupport.RenderToCompletion(renderer, view.Camera, FinalRenderSupport.Settings(Size, Size, glossy ? 1024 : 256, bounces: 1),
                new FinalRenderOptions { BackgroundLuminance = 0f, Seed = 5 });
            FinalRenderSupport.Save(render, glossy ? "pt-soft-lights-glossy" : "pt-soft-lights");
            var hdr = FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure);
            float f0 = MathF.Pow((ior - 1f) / (ior + 1f), 2f);
            var v = Vector3.UnitZ;
            double worst = 0;
            foreach (var target in new[] { new Vector3(-0.45f, 0.2f, 0f), new Vector3(0.6f, 0.1f, 0f), new Vector3(0.05f, 0.1f, 0f), new Vector3(0f, -0.6f, 0f) })
            {
                var (cx, cy, _) = view.NearestPixel(target);
                // the mean over the 3 x 3 pixels (4 x 4 points inside each: the path tracer averages over the pixel)
                double expected = 0;
                var origin = view.FloorPointOf(cx, cy);
                var stepX = view.FloorPointOf(cx + 1, cy) - origin;
                var stepY = view.FloorPointOf(cx, cy + 1) - origin;
                for (int y = -6; y < 6; y++)
                {
                    for (int x = -6; x < 6; x++)
                    {
                        var p = origin + stepX * ((x + 0.5f) / 4f) + stepY * ((y + 0.5f) / 4f);
                        expected += Reference(lights, p, l => Brdf(Vector3.UnitZ, v, l, albedo, metallic, roughness, f0) * MathF.Max(l.Z, 0f)) / 144.0;
                    }
                }

                float measured = hdr.MeanLuminance(cx, cy, 1);
                double error = Math.Abs(measured - expected) / expected;
                worst = Math.Max(worst, error);
                _output.WriteLine($"{target}: path tracer {measured:0.000} cd/m², lens integral {expected:0.000} cd/m², {100 * error:0.0} %");
            }

            Assert.InRange(worst, 0, glossy ? 0.05 : 0.03);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>
    /// Geometry without <see cref="InstanceFlags.CastShadow"/> does not block light (as in the real-time view), so the
    /// glossy reflection of a soft light behind such a wall keeps its luminance: next-event estimation passes the wall,
    /// and BSDF samples that the wall stops must not leave a hole in the multiple importance sampling. A shadow-casting
    /// wall blocks the light.
    /// </summary>
    [SkippableFact]
    public void NonCastingGeometryDoesNotDimTheReflectionOfASoftLight()
    {
        var renderer = FinalRenderSupport.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(8f, renderer.World.CreateMaterial(new MaterialData
            {
                Name = "glossy metal",
                BaseColorFactor = new Vector4(0.9f, 0.9f, 0.9f, 1f),
                Metallic = 1f,
                Roughness = 0.3f,
            }, null));
            var light = ProceduralTestScene.Spot(new Vector3(-1f, 0f, 1.4f), new Vector3(0.5f, 0f, 0f), LightColor.White, 8000f, 60f, 90f);
            light.SourceRadius = 0.25f;
            renderer.World.SetLights(new[] { light });
            // the camera looks at the floor point (0.5, 0, 0) from the mirror direction of the light
            var camera = RenderCamera.LookAt(new Vector3(2f, 0f, 1.4f), new Vector3(0.5f, 0f, 0f), 8f);
            camera.Ev100 = 10f;
            const int size = 16;

            float Reflection(string name)
            {
                using var render = FinalRenderSupport.RenderToCompletion(renderer, camera, FinalRenderSupport.Settings(size, size, 512, bounces: 1),
                    new FinalRenderOptions { BackgroundLuminance = 0f, Seed = 3 });
                FinalRenderSupport.Save(render, name);
                return FinalRenderSupport.Luminance(render.RawResultHdr(), render.Exposure).MeanLuminance(size / 2, size / 2, 3);
            }

            float open = Reflection("pt-reflection-open");
            // a thin wall between the light and the floor point, outside the camera's view of it
            var wallMaterial = scene.Lambert(new Vector3(0.5f), "wall");
            var wall = scene.Box(new Vector3(0.02f, 2f, 0.9f), new Vector3(-0.5f, 0f, 0.75f), wallMaterial, InstanceFlags.None);
            float behindNonCaster = Reflection("pt-reflection-non-caster");
            renderer.World.DestroyInstance(wall);
            scene.Box(new Vector3(0.02f, 2f, 0.9f), new Vector3(-0.5f, 0f, 0.75f), wallMaterial, InstanceFlags.CastShadow);
            float behindCaster = Reflection("pt-reflection-caster");
            _output.WriteLine($"reflection: open {open:0.0} cd/m², behind a non-casting wall {behindNonCaster:0.0}, behind a casting wall {behindCaster:0.0}");

            Assert.True(open > 100f, $"the reflection of the lens is bright ({open} cd/m²)");
            Assert.InRange(Math.Abs(behindNonCaster - open) / open, 0, 0.03);
            Assert.True(behindCaster < 0.02f * open, $"a shadow-casting wall blocks the light ({behindCaster} cd/m²)");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }
}
