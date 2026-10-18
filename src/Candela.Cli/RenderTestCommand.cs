using System.Diagnostics;
using System.Globalization;
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
                default:
                    throw new UsageException($"Unknown option '{name}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(o.OutputDirectory))
            throw new UsageException("render-test needs --out <dir>.");
        return o;
    }

    private static int PositiveInt(string name, string value, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > max)
            throw new UsageException($"{name} must be an integer between 1 and {max} (got '{value}').");
        return n;
    }
}

/// <summary>
/// <c>candela render-test</c>: renders the procedural test scene in the Full, Colorless and Wireframe views, writes
/// the PNGs and prints the renderer information and frame timings (CPU recording time, wall time per frame and the GPU
/// time per pass of the last frame).
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
        var stats = renderer.VulkanWorld.Statistics;
        output.WriteLine($"Scene:        {stats.Meshes} meshes, {stats.Materials} materials, {stats.Instances} instances, " +
                         $"{stats.Lights} spot lights, {stats.LineSegments} line segments");
        output.WriteLine($"Image:        {options.Width} x {options.Height}, {options.Frames} timed frames per view");
        output.WriteLine();

        var camera = ProceduralTestScene.CreateCamera();
        var settings = new PreviewSettings { ToneMapper = options.ToneMapper };
        using var target = renderer.CreateTarget(options.Width, options.Height);
        Directory.CreateDirectory(options.OutputDirectory);
        foreach (var (shading, file) in new[] { (ViewShading.Full, "full.png"), (ViewShading.Colorless, "colorless.png"), (ViewShading.Wireframe, "wireframe.png") })
        {
            var view = new ViewOptions { Shading = shading };
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
                $"{shading,-10} CPU {cpu / options.Frames,7:F2} ms   wall {wall.Elapsed.TotalMilliseconds / options.Frames,8:F2} ms/frame   " +
                $"GPU {(timings.Length > 0 ? gpu.ToString("F2", CultureInfo.InvariantCulture) + " ms" : "n/a")}   " +
                $"{last.VisibleInstances} visible, {last.CulledInstances} culled, {last.Draws} draws"));
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
