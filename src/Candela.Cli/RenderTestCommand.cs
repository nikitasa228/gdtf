using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Candela.Rendering;
using Candela.Rendering.Vulkan;

namespace Candela.Cli;

/// <summary>Options of <c>candela render-test</c>.</summary>
internal sealed class RenderTestOptions
{
    public string OutputDirectory { get; private set; } = "";
    public int Width { get; private set; } = 1280;
    public int Height { get; private set; } = 720;
    public int Frames { get; private set; } = 5;
    public ToneMapper ToneMapper { get; private set; } = ToneMapper.AgX;
    public string Device { get; private set; } = "";
    public bool Validation { get; private set; }

    /// <summary>Uniform haze scattering coefficient (1/m); 0 = clear air.</summary>
    public float Haze { get; private set; }

    public bool Shadows { get; private set; } = true;
    public int ShadowResolution { get; private set; } = 1024;
    public IndirectLighting Indirect { get; private set; } = IndirectLighting.ReflectiveShadowMaps;
    public bool Bloom { get; private set; } = true;
    public float BloomIntensity { get; private set; } = 0.04f;
    public bool AutoExposure { get; private set; }
    public float ExposureCompensation { get; private set; }
    public float ResolutionScale { get; private set; } = 1f;
    public int VolumetricSlices { get; private set; } = 128;

    /// <summary>Also render the lux, noise and cable overlays.</summary>
    public bool Overlays { get; private set; }

    public static RenderTestOptions Parse(string[] args)
    {
        var o = new RenderTestOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            string Value()
            {
                if (i + 1 >= args.Length)
                    throw new UsageException($"Option {name} needs a value.");
                return args[++i];
            }

            switch (name)
            {
                case "--out":
                    o.OutputDirectory = Value();
                    break;
                case "--width":
                    o.Width = PositiveInt(name, Value(), 16384);
                    break;
                case "--height":
                    o.Height = PositiveInt(name, Value(), 16384);
                    break;
                case "--frames":
                    o.Frames = PositiveInt(name, Value(), 10000);
                    break;
                case "--tonemapper":
                    o.ToneMapper = Value().ToLowerInvariant() switch
                    {
                        "agx" => ToneMapper.AgX,
                        "aces" => ToneMapper.Aces,
                        "neutral" => ToneMapper.Neutral,
                        var v => throw new UsageException($"Unknown tone mapper '{v}' (agx, aces, neutral)."),
                    };
                    break;
                case "--device":
                    o.Device = Value();
                    break;
                case "--validation":
                    o.Validation = true;
                    break;
                case "--haze":
                    o.Haze = Number(name, Value(), 0f, 10f);
                    break;
                case "--no-shadows":
                    o.Shadows = false;
                    break;
                case "--shadow-resolution":
                    o.ShadowResolution = PositiveInt(name, Value(), 8192);
                    break;
                case "--indirect":
                    o.Indirect = Value().ToLowerInvariant() switch
                    {
                        "off" => IndirectLighting.Off,
                        "rsm" => IndirectLighting.ReflectiveShadowMaps,
                        "raytraced" or "rt" => IndirectLighting.RayTraced,
                        var v => throw new UsageException($"Unknown indirect lighting '{v}' (off, rsm, raytraced)."),
                    };
                    break;
                case "--no-bloom":
                    o.Bloom = false;
                    break;
                case "--bloom":
                    o.BloomIntensity = Number(name, Value(), 0f, 1f);
                    break;
                case "--auto-exposure":
                    o.AutoExposure = true;
                    break;
                case "--exposure-compensation":
                    o.ExposureCompensation = Number(name, Value(), -20f, 20f);
                    break;
                case "--scale":
                    o.ResolutionScale = Number(name, Value(), 0.5f, 1f);
                    break;
                case "--volumetric-slices":
                    o.VolumetricSlices = PositiveInt(name, Value(), 256);
                    break;
                case "--overlays":
                    o.Overlays = true;
                    break;
                default:
                    throw new UsageException($"Unknown option '{name}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(o.OutputDirectory))
            throw new UsageException("render-test needs --out <dir>.");
        return o;
    }

    /// <summary>The preview settings these options describe.</summary>
    public PreviewSettings CreateSettings() => new()
    {
        ToneMapper = ToneMapper,
        Shadows = Shadows,
        ShadowMapResolution = ShadowResolution,
        Indirect = Indirect,
        Bloom = Bloom,
        BloomIntensity = BloomIntensity,
        AutoExposure = AutoExposure,
        ExposureCompensation = ExposureCompensation,
        ResolutionScale = ResolutionScale,
        Volumetrics = true,
        VolumetricSlices = VolumetricSlices,
    };

    private static float Number(string name, string value, float min, float max)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.IsFinite(x) || x < min || x > max)
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"{name} must be a number between {min} and {max} (got '{value}')."));
        return x;
    }

    private static int PositiveInt(string name, string value, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > max)
            throw new UsageException($"{name} must be an integer between 1 and {max} (got '{value}').");
        return n;
    }
}

/// <summary>
/// <c>candela render-test</c>: renders the procedural test scene in the Full, Colorless and Wireframe views (and, with
/// <c>--overlays</c>, the lux, noise and cable overlays), writes the PNGs and prints the renderer information and frame
/// timings (CPU recording time, wall time per frame, the GPU time per pass of the last frame and the stage-2
/// statistics: shadowed lights, shadow tiles rendered, ray-marched beams, virtual point lights).
/// </summary>
internal static class RenderTestCommand
{
    public static int Run(RenderTestOptions options, TextWriter output)
    {
        var contextOptions = new VulkanContextOptions
        {
            EnableValidation = options.Validation ? true : null,
            PreferredDeviceName = options.Device,
            MinimumLogLevel = VulkanLogLevel.Warning,
            Log = (level, message) => Console.Error.WriteLine($"[vulkan {level.ToString().ToLowerInvariant()}] {message}"),
        };
        using var renderer = VulkanRenderer.Create(contextOptions);
        var info = renderer.Info;
        output.WriteLine($"Device:       {info.DeviceName}");
        output.WriteLine($"Driver:       {info.DriverName}");
        output.WriteLine($"Vulkan:       {info.ApiVersion}");
        output.WriteLine($"Software:     {(info.IsSoftwareRenderer ? "yes" : "no")}");
        output.WriteLine($"Ray tracing:  {(info.SupportsRayTracing ? "yes" : "no")}");
        output.WriteLine($"Wireframe:    {(renderer.UsesEdgeShaderWireframe ? "edge shader" : "polygon mode")}");
        output.WriteLine($"Memory:       {info.DeviceMemoryBytes / (1024.0 * 1024.0):F0} MiB device-local");
        output.WriteLine($"Validation:   {(renderer.Context.ValidationEnabled ? "on" : "off")}");

        var scene = ProceduralTestScene.Build(renderer.World);
        if (options.Haze > 0f)
            renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = options.Haze, Anisotropy = 0.6f });
        if (options.Overlays)
        {
            renderer.World.SetNoiseSources(new[]
            {
                new NoiseSource(new Vector3(-3f, 2f, 1f), 70f),
                new NoiseSource(new Vector3(3f, 3f, 4f), 55f),
            });
        }

        var stats = renderer.VulkanWorld.Statistics;
        output.WriteLine($"Scene:        {stats.Meshes} meshes, {stats.Materials} materials, {stats.Instances} instances, " +
                         $"{stats.Lights} spot lights, {stats.LineSegments} line segments");
        output.WriteLine($"Image:        {options.Width} x {options.Height}, {options.Frames} timed frames per view");
        string shadows = options.Shadows ? options.ShadowResolution.ToString(CultureInfo.InvariantCulture) : "off";
        string bloom = options.Bloom ? options.BloomIntensity.ToString(CultureInfo.InvariantCulture) : "off";
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Settings:     shadows {shadows}, indirect {options.Indirect}, haze {options.Haze} 1/m, bloom {bloom}, " +
            $"exposure {(options.AutoExposure ? "auto" : "camera")} {options.ExposureCompensation:+0.##;-0.##;0} stops, scale {options.ResolutionScale}"));
        output.WriteLine();

        var camera = ProceduralTestScene.CreateCamera();
        var settings = options.CreateSettings();
        using var target = renderer.CreateTarget(options.Width, options.Height);
        Directory.CreateDirectory(options.OutputDirectory);
        var views = new List<(string Label, ViewOptions View, string File)>
        {
            ("Full", new ViewOptions { Shading = ViewShading.Full }, "full.png"),
            ("Colorless", new ViewOptions { Shading = ViewShading.Colorless }, "colorless.png"),
            ("Wireframe", new ViewOptions { Shading = ViewShading.Wireframe }, "wireframe.png"),
        };
        if (options.Overlays)
        {
            views.Add(("Lux", new ViewOptions { Overlay = OverlayMode.Lux }, "lux.png"));
            views.Add(("Noise", new ViewOptions { Overlay = OverlayMode.Noise }, "noise.png"));
            views.Add(("Cables", new ViewOptions { Overlay = OverlayMode.Cables }, "cables.png"));
        }

        foreach (var (label, view, file) in views)
        {
            renderer.Render(target, camera, view, settings, 0f); // warm-up (pipelines, uploads)
            renderer.Context.WaitForSubmittedFrames();

            double cpu = 0;
            var wall = Stopwatch.StartNew();
            for (int i = 0; i < options.Frames; i++)
            {
                renderer.Render(target, camera, view, settings, 1f / 60f);
                cpu += renderer.LastStatistics.CpuMilliseconds;
            }

            renderer.Context.WaitForSubmittedFrames();
            wall.Stop();
            var image = renderer.ReadPixels(target);
            string path = Path.Combine(options.OutputDirectory, file);
            ImageFiles.WritePng(image, path);

            var timings = renderer.Context.LastGpuTimings;
            double gpu = 0;
            var parts = new List<string>();
            foreach (var t in timings)
            {
                gpu += t.Milliseconds;
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{t.Name} {t.Milliseconds:F2}"));
            }

            var last = renderer.LastStatistics;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{label,-10} CPU {cpu / options.Frames,7:F2} ms   wall {wall.Elapsed.TotalMilliseconds / options.Frames,8:F2} ms/frame   " +
                $"GPU {(timings.Length > 0 ? gpu.ToString("F2", CultureInfo.InvariantCulture) + " ms" : "n/a")}   " +
                $"{last.VisibleInstances} visible, {last.CulledInstances} culled, {last.Draws} draws"));
            if (last.ShadowedLights + last.RaymarchedBeams + last.VirtualPointLights > 0)
            {
                output.WriteLine($"           {last.ShadowedLights} shadowed lights ({last.ShadowTilesRendered} tiles rendered this frame), " +
                                 $"{last.RaymarchedBeams} ray-marched beams, {last.VirtualPointLights} virtual point lights");
            }

            if (parts.Count > 0)
                output.WriteLine($"           passes: {string.Join(", ", parts)}");
            output.WriteLine($"           wrote {path}");
        }

        scene.Destroy();
        int errors = renderer.Context.ValidationErrorCount;
        if (renderer.Context.ValidationEnabled)
            output.WriteLine($"Validation errors: {errors}");
        return errors == 0 ? Program.ExitOk : Program.ExitFailure;
    }
}
