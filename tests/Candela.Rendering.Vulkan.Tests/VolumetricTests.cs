using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// Volumetric light: single scattering of a spot beam in uniform haze against a numerically integrated reference
/// (froxel path for a wide beam, full-resolution ray marching for a narrow one), golden images of a beam in haze, a
/// smoke blob from a <see cref="VolumeGrid"/> and a glowing flame volume, and the luminance of an optically thick
/// flame against the blackbody luminance.
/// </summary>
public sealed class VolumetricTests
{
    private const int Width = 160;
    private const int Height = 120;
    private const float Haze = 0.02f;
    private const float Anisotropy = 0.7f;

    private static readonly Vector3 LightPosition = new(0f, 0f, 4f);
    private static readonly Vector3 CameraPosition = new(0f, -6f, 2f);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FroxelSingleScatteringMatchesTheIntegral(bool temporal)
    {
        // A wide beam (field angle above the ray-marching limit) goes through the froxel volume.
        var light = StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
        float measured = MeasureCentre(light, temporal, maxRaymarched: 16, out var renderer, out int raymarched);
        try
        {
            Assert.Equal(0, raymarched);
            float expected = ReferenceInScattering(light);
            Assert.True(expected > 0.1f, $"reference {expected}");
            Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.05f);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void RaymarchedNarrowBeamMatchesTheIntegral()
    {
        // The ray-marched beam is excluded from the froxel injection: were it counted twice, the result would exceed the
        // reference by the froxels' share (about half of it for this narrow beam).
        var light = StageScene.Spot(LightPosition, Vector3.Zero, 40000f, 8f, 12f, shadows: false);
        float measured = MeasureCentre(light, temporal: false, maxRaymarched: 16, out var renderer, out int raymarched);
        try
        {
            Assert.Equal(1, raymarched);
            float expected = ReferenceInScattering(light);
            Assert.True(expected > 0.1f, $"reference {expected}");
            Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.05f);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void UniformSmokeScattersLikeHazeOfTheSameCoefficient()
    {
        // Smoke density 0.04 x 0.5 /m at full density = the haze test's 0.02 /m, through the 3D texture over a box.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = 0f,
                Anisotropy = Anisotropy,
                SmokeScatteringAtFullDensity = 0.5f,
                SmokeDensity = new VolumeGrid
                {
                    Min = new Vector3(-30f, -30f, -1f),
                    Max = new Vector3(30f, 30f, 8f),
                    SizeX = 8,
                    SizeY = 8,
                    SizeZ = 8,
                    Values = Enumerable.Repeat(Haze / 0.5f, 8 * 8 * 8).ToArray(),
                },
            });
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(CameraPosition, CameraPosition + Vector3.UnitY, 40f);
            camera.Ev100 = 2f;
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f },
                new PreviewSettings { TemporalAccumulation = false, Bloom = false, VolumetricSlices = 128 }, 1f / 60f);
            float measured = CentreLuminance(renderer.ReadHdr(target));
            float expected = ReferenceInScattering(light);
            Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.05f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void BeamInUniformHazeSeenFromTheSide()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            var floor = scene.Lambert(new Vector3(0.3f));
            scene.Floor(14f, floor);
            var gobo = renderer.World.CreateTexture(StageScene.GoboImage(), srgb: true);
            var narrow = StageScene.Spot(new Vector3(-1.5f, 0.5f, 5f), new Vector3(-0.8f, 0.5f, 0f), 80000f, 9f, 12f, type: BeamType.Spot);
            narrow.Gobo = gobo;
            var wide = StageScene.Spot(new Vector3(1.8f, 0.5f, 5f), new Vector3(1.2f, 0.5f, 0f), 30000f, 25f, 40f,
                LightColor.FromRec709(new Vector3(0.2f, 0.45f, 1f)));
            renderer.World.SetLights(new[] { narrow, wide });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = 0.6f });
            using var target = renderer.CreateTarget(240, 180);
            var camera = RenderCamera.LookAt(new Vector3(0f, -7f, 2.2f), new Vector3(0f, 0.5f, 2.2f), 55f);
            camera.Ev100 = 4f;
            var image = scene.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f },
                new PreviewSettings { VolumetricSlices = 64, Indirect = IndirectLighting.Off }, frames: 6);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            Assert.Equal(1, renderer.LastStatistics.RaymarchedBeams);
            GoldenImageTests.CompareWithGolden(image, "volumetric-beam");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void SmokeBlobFromAVolumeGrid()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(14f, scene.Lambert(new Vector3(0.3f)));
            renderer.World.SetLights(new[]
            {
                StageScene.Spot(new Vector3(-2.5f, 0.5f, 4.5f), new Vector3(0f, 0.5f, 1.2f), 30000f, 30f, 45f, LightColor.FromCct(3200f)),
            });
            renderer.World.SetAtmosphere(new AtmosphereState
            {
                BaseScattering = 0.002f,
                Anisotropy = 0.5f,
                SmokeDensity = Blob(new Vector3(-1.5f, -1f, 0f), new Vector3(1.5f, 2f, 3f), 24, 24, 24, new Vector3(0f, 0.5f, 1.2f), 0.7f),
                SmokeScatteringAtFullDensity = 1.2f,
            });
            using var target = renderer.CreateTarget(240, 180);
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 1.8f), new Vector3(0f, 0.5f, 1.2f), 50f);
            camera.Ev100 = 6f;
            var image = scene.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f },
                new PreviewSettings { VolumetricSlices = 64, Indirect = IndirectLighting.Off }, frames: 6);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(image, "volumetric-smoke");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void FlameVolumeGlows()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Floor(14f, scene.Lambert(new Vector3(0.3f)));
            var grid = FlameGrid();
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, FlameTemperature = grid });
            using var target = renderer.CreateTarget(160, 120);
            var camera = RenderCamera.LookAt(new Vector3(0f, -4f, 1f), new Vector3(0f, 0f, 0.9f), 50f);
            camera.Ev100 = 12.5f;
            var image = scene.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f },
                new PreviewSettings { VolumetricSlices = 64, Indirect = IndirectLighting.Off }, frames: 6);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            GoldenImageTests.CompareWithGolden(image, "volumetric-flame");
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableFact]
    public void OpticallyThickFlameShowsItsBlackbodyLuminance()
    {
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            const float kelvin = 1500f;
            var values = new float[8 * 8 * 8];
            Array.Fill(values, kelvin);
            var grid = new VolumeGrid { Min = new Vector3(-1.5f, -1.5f, -0.5f), Max = new Vector3(1.5f, 1.5f, 2.5f), SizeX = 8, SizeY = 8, SizeZ = 8, Values = values };
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f, FlameTemperature = grid });
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(new Vector3(0f, -6f, 1f), new Vector3(0f, 0f, 1f), 30f);
            camera.Ev100 = 15f;
            for (int i = 0; i < 4; i++)
                renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, new PreviewSettings(), 1f / 30f);
            var hdr = renderer.ReadHdr(target);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
            // 3 m of flame with kappa = 2 /m: 1 - e^-6 of the blackbody luminance.
            float expected = (float)BlackbodyRadiator.Luminance(kelvin) * (1f - MathF.Exp(-VulkanRenderer.FlameAbsorption * 3f));
            float measured = hdr.LuminanceAt(Width / 2, Height / 2);
            Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.05f);
            // ... with the blackbody colour (chromaticity) of that temperature.
            var xy = LightColor.FromRec2020(hdr.At(Width / 2, Height / 2)).ToXy();
            var reference = BlackbodyRadiator.Chromaticity(kelvin);
            Assert.InRange(Vector2.Distance(xy, reference), 0f, 0.01f);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(true, 16)]
    [InlineData(true, 0)]
    [InlineData(false, 16)]
    public void LookingUpTheBeamMatchesTheIntegral(bool narrow, int maxRaymarched)
    {
        // From inside the beam towards the light (strong forward scattering, steep gradients along the view ray):
        // the ray-marched narrow beam, the same beam in the froxels and a wide beam in the froxels.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = narrow
                ? StageScene.Spot(LightPosition, Vector3.Zero, 40000f, 8f, 12f, shadows: false)
                : StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            var eye = narrow ? new Vector3(0f, -0.1f, 1f) : new Vector3(0f, -0.5f, 1f);
            var look = narrow ? new Vector3(0f, 0.3f, 3.5f) : new Vector3(0f, 0.4f, 3.5f);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(eye, look, 40f);
            camera.Ev100 = 6f;
            var settings = new PreviewSettings { TemporalAccumulation = false, VolumetricSlices = 128, Bloom = false, MaxRaymarchedBeams = maxRaymarched };
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, settings, 1f / 60f);
            Assert.Equal(narrow && maxRaymarched > 0 ? 1 : 0, renderer.LastStatistics.RaymarchedBeams);
            float measured = CentreLuminance(renderer.ReadHdr(target));
            float expected = ReferenceInScattering(light, eye, Vector3.Normalize(look - eye));
            Assert.InRange(MathF.Abs(measured - expected) / expected, 0f, 0.05f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShadowedHazeStaysDark(bool narrow)
    {
        // A plate 1 m below the light blocks the middle of the beam: the haze under it (froxels, or the ray-marched
        // narrow beam) gets no light.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var scene = new StageScene(renderer);
            scene.Box(new Vector3(2f, 2f, 0.05f), new Vector3(0f, 0f, 3f), scene.Lambert(new Vector3(0.5f)));
            var light = narrow
                ? StageScene.Spot(LightPosition, Vector3.Zero, 40000f, 8f, 12f)
                : StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            using var target = renderer.CreateTarget(Width, Height);
            var camera = RenderCamera.LookAt(CameraPosition, CameraPosition + Vector3.UnitY, 40f);
            camera.Ev100 = 2f;
            var settings = new PreviewSettings { TemporalAccumulation = false, Bloom = false, Indirect = IndirectLighting.Off };
            var options = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            renderer.Render(target, camera, options, settings, 1f / 60f);
            Assert.Equal(narrow ? 1 : 0, renderer.LastStatistics.RaymarchedBeams);
            float shadowed = CentreLuminance(renderer.ReadHdr(target));
            settings.Shadows = false;
            renderer.Render(target, camera, options, settings, 1f / 60f);
            float lit = CentreLuminance(renderer.ReadHdr(target));
            Assert.True(lit > 0.1f, $"lit haze {lit} cd/m²");
            Assert.True(shadowed < 0.02f * lit, $"shadowed haze {shadowed} cd/m², {lit} cd/m² unshadowed");
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FroxelHistoryIsReprojectedWhenTheCameraMoves(bool rotate)
    {
        // The camera trucks (or pans) over 20 frames and ends on the beam: with the history reprojected the beam is
        // where the converged still image has it; without (history read at the same froxel) it lags by ~5 px.
        var renderer = TestRenderers.CreateOrSkip();
        try
        {
            var light = StageScene.Spot(LightPosition, Vector3.Zero, 10000f, 40f, 60f, shadows: false);
            renderer.World.SetLights(new[] { light });
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
            using var target = renderer.CreateTarget(Width, Height);
            var settings = new PreviewSettings { TemporalAccumulation = true, VolumetricSlices = 64, Bloom = false, MaxRaymarchedBeams = 0 };
            RenderCamera Camera(int i)
            {
                var eye = rotate ? CameraPosition : CameraPosition + new Vector3(-1f + 0.05f * i, 0f, 0f);
                float yaw = rotate ? 0.15f - 0.0075f * i : 0f;
                var camera = RenderCamera.LookAt(eye, eye + new Vector3(MathF.Sin(yaw), MathF.Cos(yaw), 0f), 40f);
                camera.Ev100 = 2f;
                return camera;
            }

            var options = new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f };
            for (int i = 0; i <= 20; i++)
                renderer.Render(target, Camera(i), options, settings, 1f / 60f);
            float moving = BeamCentroid(renderer.ReadHdr(target));
            for (int i = 0; i < 30; i++)
                renderer.Render(target, Camera(20), options, settings, 1f / 60f);
            float still = BeamCentroid(renderer.ReadHdr(target));
            Assert.InRange(moving - still, -0.75f, 0.75f);
            TestVulkan.AssertNoValidationErrors(renderer.Context);
        }
        finally
        {
            TestRenderers.DisposeAndAssertClean(renderer);
        }
    }

    /// <summary>Luminance-weighted mean column of the lower part of the image (below the light), pixels.</summary>
    private static float BeamCentroid(HdrReadback hdr)
    {
        double sum = 0, weighted = 0;
        for (int y = hdr.Height / 3; y < hdr.Height - 10; y++)
        {
            for (int x = 0; x < hdr.Width; x++)
            {
                float l = hdr.LuminanceAt(x, y);
                sum += l;
                weighted += l * (x + 0.5);
            }
        }

        return (float)(weighted / sum);
    }

    /// <summary>Mean luminance of the four pixels around the image centre (the centre ray lies between them).</summary>
    private static float CentreLuminance(HdrReadback hdr) =>
        0.25f * (hdr.LuminanceAt(Width / 2 - 1, Height / 2 - 1) + hdr.LuminanceAt(Width / 2, Height / 2 - 1) +
                 hdr.LuminanceAt(Width / 2 - 1, Height / 2) + hdr.LuminanceAt(Width / 2, Height / 2));

    /// <summary>
    /// Renders the beam in haze (no geometry, black background) from the side and returns the luminance of the centre
    /// pixel, whose view ray crosses the beam axis 2 m below the light at right angles.
    /// </summary>
    private static float MeasureCentre(SpotLight light, bool temporal, int maxRaymarched, out VulkanRenderer renderer, out int raymarched)
    {
        renderer = TestRenderers.CreateOrSkip();
        renderer.World.SetLights(new[] { light });
        renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = Haze, Anisotropy = Anisotropy });
        using var target = renderer.CreateTarget(Width, Height);
        var camera = RenderCamera.LookAt(CameraPosition, CameraPosition + Vector3.UnitY, 40f);
        camera.Ev100 = 2f;
        var settings = new PreviewSettings
        {
            TemporalAccumulation = temporal,
            VolumetricSlices = 128,
            MaxRaymarchedBeams = maxRaymarched,
            Bloom = false,
        };
        int frames = temporal ? 16 : 1;
        for (int i = 0; i < frames; i++)
            renderer.Render(target, camera, new ViewOptions { ShowGrid = false, BackgroundLuminance = 0f }, settings, 1f / 60f);
        raymarched = renderer.LastStatistics.RaymarchedBeams;
        var hdr = renderer.ReadHdr(target);
        TestVulkan.AssertNoValidationErrors(renderer.Context);
        // Average the four pixels around the image centre (the centre ray lies between them).
        float sum = 0f;
        for (int dy = -1; dy <= 0; dy++)
        {
            for (int dx = -1; dx <= 0; dx++)
                sum += hdr.LuminanceAt(Width / 2 + dx, Height / 2 + dy);
        }

        return 0.25f * sum;
    }

    /// <summary>
    /// L = ∫ σ_s · p_HG(θ) · I(θ_beam) · exp(−σ_t·r) / r² · exp(−σ_t·l) dl along the centre view ray (σ_t = σ_s: haze
    /// does not absorb, but the light it scatters leaves both the light's path r and the view path l), integrated
    /// numerically with the midpoint rule.
    /// </summary>
    private static float ReferenceInScattering(SpotLight light) => ReferenceInScattering(light, CameraPosition, Vector3.UnitY);

    /// <inheritdoc cref="ReferenceInScattering(SpotLight)"/>
    private static float ReferenceInScattering(SpotLight light, Vector3 origin, Vector3 direction)
    {
        var beam = BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle);
        double sum = 0;
        const int steps = 400000;
        const double length = 60;
        double dl = length / steps;
        for (int i = 0; i < steps; i++)
        {
            double l = (i + 0.5) * dl;
            var x = origin + direction * (float)l;
            var toPoint = x - light.Position;
            float r = toPoint.Length();
            var propagation = toPoint / r;
            float theta = MathF.Atan2(Vector3.Cross(light.Direction, toPoint).Length(), Vector3.Dot(light.Direction, toPoint));
            float intensity = light.PeakCandela * beam.Relative(theta);
            if (intensity <= 0f)
                continue;
            float cos = Vector3.Dot(propagation, -direction);
            double g = Anisotropy;
            double phase = (1 - g * g) / (4 * Math.PI * Math.Pow(1 + g * g - 2 * g * cos, 1.5));
            sum += Haze * phase * intensity * Math.Exp(-Haze * r) / (r * r) * Math.Exp(-Haze * l) * dl;
        }

        return (float)sum;
    }

    /// <summary>A Gaussian density blob (1 at the centre) over a box.</summary>
    private static VolumeGrid Blob(Vector3 min, Vector3 max, int nx, int ny, int nz, Vector3 centre, float radius)
    {
        var values = new float[nx * ny * nz];
        for (int z = 0; z < nz; z++)
        {
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    var p = min + (max - min) * new Vector3((x + 0.5f) / nx, (y + 0.5f) / ny, (z + 0.5f) / nz);
                    var d = (p - centre) / radius;
                    d.Z *= 0.7f;
                    values[(z * ny + y) * nx + x] = MathF.Exp(-d.LengthSquared());
                }
            }
        }

        return new VolumeGrid { Min = min, Max = max, SizeX = nx, SizeY = ny, SizeZ = nz, Values = values };
    }

    /// <summary>A teardrop-shaped flame: 1900 K core cooling to nothing at its edge.</summary>
    private static VolumeGrid FlameGrid()
    {
        const int nx = 16, ny = 16, nz = 24;
        var min = new Vector3(-0.6f, -0.6f, 0f);
        var max = new Vector3(0.6f, 0.6f, 1.8f);
        var values = new float[nx * ny * nz];
        for (int z = 0; z < nz; z++)
        {
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    var p = min + (max - min) * new Vector3((x + 0.5f) / nx, (y + 0.5f) / ny, (z + 0.5f) / nz);
                    float h = p.Z / 1.8f;
                    float radius = 0.45f * MathF.Sqrt(MathF.Max(h, 0f)) * (1f - h) * 1.6f + 0.02f;
                    float r = MathF.Sqrt(p.X * p.X + p.Y * p.Y) / radius;
                    values[(z * ny + y) * nx + x] = r < 1f ? 1900f * (1f - 0.45f * r * r) * (1f - 0.35f * h) : 0f;
                }
            }
        }

        return new VolumeGrid { Min = min, Max = max, SizeX = nx, SizeY = ny, SizeZ = nz, Values = values };
    }
}
