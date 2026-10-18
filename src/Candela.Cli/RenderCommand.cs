using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Engine;
using Candela.Rendering;
using Candela.Rendering.Vulkan;

namespace Candela.Cli;

/// <summary>How the fixtures are set before rendering.</summary>
internal enum RenderLook
{
    /// <summary>Every light at full, open white, mid zoom, moving heads aimed towards the stage centre.</summary>
    OpenWhite,

    /// <summary>The scene as saved (manual values of the project).</summary>
    Show,

    /// <summary>A DMX recording played from its start for --time seconds.</summary>
    DmxFile,
}

/// <summary>Options of <c>candela render</c>.</summary>
internal sealed class RenderOptions
{
    public string Scene { get; private set; } = "";
    public string Output { get; private set; } = "";
    public int Width { get; private set; } = 1280;
    public int Height { get; private set; } = 720;
    public Vector3? Camera { get; private set; }
    public Vector3? Target { get; private set; }
    public ViewPreset View { get; private set; } = ViewPreset.Auto;
    public float FovY { get; private set; } = 45f;
    public ViewShading Mode { get; private set; } = ViewShading.Full;
    public OverlayMode Overlay { get; private set; } = OverlayMode.None;
    public float? Haze { get; private set; }
    public RenderLook Look { get; private set; } = RenderLook.OpenWhite;
    public string? Recording { get; private set; }
    public double Time { get; private set; }
    /// <summary>Exposure value at ISO 100, or null for the incident-light reading of the stage floor.</summary>
    public float? Ev100 { get; private set; }
    public bool Floor { get; private set; } = true;

    public bool AutoExposure { get; private set; }
    public int Frames { get; private set; } = 6;
    public bool Grid { get; private set; }
    public string Device { get; private set; } = "";
    public List<string> Libraries { get; } = new();
    public string? Cache { get; private set; }

    public static RenderOptions Parse(string[] args)
    {
        var o = new RenderOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            switch (name)
            {
                case "--out":
                    o.Output = CliArgs.Value(args, ref i, name);
                    break;
                case "--width":
                    o.Width = CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 16, 16384);
                    break;
                case "--height":
                    o.Height = CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 16, 16384);
                    break;
                case "--camera":
                    o.Camera = CliArgs.Vector(name, CliArgs.Value(args, ref i, name));
                    break;
                case "--target":
                    o.Target = CliArgs.Vector(name, CliArgs.Value(args, ref i, name));
                    break;
                case "--view":
                    o.View = CliArgs.Value(args, ref i, name).ToLowerInvariant() switch
                    {
                        "auto" => ViewPreset.Auto,
                        "front" => ViewPreset.Front,
                        "top" => ViewPreset.Top,
                        "side" => ViewPreset.Side,
                        var v => throw new UsageException($"Unknown view '{v}' (front, top, side, auto)."),
                    };
                    break;
                case "--fov":
                    o.FovY = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 5f, 120f);
                    break;
                case "--mode":
                    o.Mode = CliArgs.Value(args, ref i, name).ToLowerInvariant() switch
                    {
                        "full" => ViewShading.Full,
                        "colorless" or "colourless" => ViewShading.Colorless,
                        "wireframe" => ViewShading.Wireframe,
                        var v => throw new UsageException($"Unknown mode '{v}' (full, colorless, wireframe)."),
                    };
                    break;
                case "--overlay":
                    o.Overlay = CliArgs.Value(args, ref i, name).ToLowerInvariant() switch
                    {
                        "none" => OverlayMode.None,
                        "lux" => OverlayMode.Lux,
                        "noise" => OverlayMode.Noise,
                        "cables" => OverlayMode.Cables,
                        var v => throw new UsageException($"Unknown overlay '{v}' (none, lux, noise, cables)."),
                    };
                    break;
                case "--haze":
                    o.Haze = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 0f, 10f);
                    break;
                case "--look":
                    string look = CliArgs.Value(args, ref i, name).ToLowerInvariant();
                    switch (look)
                    {
                        case "open-white":
                            o.Look = RenderLook.OpenWhite;
                            break;
                        case "show":
                            o.Look = RenderLook.Show;
                            break;
                        case "dmx-file":
                            o.Look = RenderLook.DmxFile;
                            o.Recording = CliArgs.Value(args, ref i, "--look dmx-file");
                            break;
                        default:
                            throw new UsageException($"Unknown look '{look}' (open-white, show, dmx-file <recording>).");
                    }
                    break;
                case "--time":
                    o.Time = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 0f, 36000f);
                    break;
                case "--ev":
                    string ev = CliArgs.Value(args, ref i, name);
                    o.Ev100 = ev.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : CliArgs.Number(name, ev, -10f, 25f);
                    break;
                case "--auto-exposure":
                    o.AutoExposure = true;
                    break;
                case "--frames":
                    o.Frames = CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 1, 1000);
                    break;
                case "--grid":
                    o.Grid = true;
                    break;
                case "--floor":
                    o.Floor = CliArgs.Value(args, ref i, name).ToLowerInvariant() switch
                    {
                        "auto" => true,
                        "none" => false,
                        var v => throw new UsageException($"Unknown floor '{v}' (auto, none)."),
                    };
                    break;
                case "--device":
                    o.Device = CliArgs.Value(args, ref i, name);
                    break;
                case "--library":
                    o.Libraries.Add(CliArgs.Value(args, ref i, name));
                    break;
                case "--cache":
                    o.Cache = CliArgs.Value(args, ref i, name);
                    break;
                default:
                    if (name.StartsWith('-')) throw new UsageException($"Unknown option '{name}'.");
                    if (o.Scene.Length > 0) throw new UsageException($"Only one scene can be rendered (got '{o.Scene}' and '{name}').");
                    o.Scene = name;
                    break;
            }
        }
        if (o.Scene.Length == 0) throw new UsageException("render needs a scene file (.mvr or .candela).");
        if (o.Output.Length == 0) throw new UsageException("render needs --out <file.png>.");
        if (o.Camera.HasValue != o.Target.HasValue) throw new UsageException("--camera and --target go together.");
        return o;
    }
}

/// <summary>
/// <c>candela render</c>: loads a scene through <see cref="StageEngine"/>, applies a look, simulates, renders one image
/// with the Vulkan renderer and writes it as PNG.
/// </summary>
internal static class RenderCommand
{
    /// <summary>Simulation step used to advance --time (30 frames per second, like a typical preview).</summary>
    public const double TickSeconds = 1.0 / 30.0;

    public static int Run(RenderOptions options, TextWriter output, TextWriter error)
    {
        var total = Stopwatch.StartNew();
        using var engine = new StageEngine(CliArgs.EngineOptions(options.Libraries, options.Cache));
        var watch = Stopwatch.StartNew();
        var opened = engine.Open(options.Scene);
        output.WriteLine(opened.Summary);
        if (!opened.Success)
        {
            PrintIssues(error, opened.Issues, 20);
            return Program.ExitFailure;
        }
        double loadMs = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        DmxRecording? recording = null;
        switch (options.Look)
        {
            case RenderLook.OpenWhite:
                int changed = StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
                output.WriteLine($"Look:     open white on {changed} fixtures");
                break;
            case RenderLook.DmxFile:
                try
                {
                    recording = DmxRecording.Load(options.Recording!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    error.WriteLine($"The DMX recording '{options.Recording}' cannot be read: {e.Message}");
                    return Program.ExitFailure;
                }
                foreach (var issue in recording.Issues) engine.Issues.Report(issue);
                engine.Play(recording);
                output.WriteLine($"Look:     DMX recording '{recording.Name}' ({recording.Frames.Count} frames, {CliArgs.F(recording.Duration)} s)");
                break;
            default:
                output.WriteLine("Look:     as saved");
                break;
        }

        var contextOptions = new VulkanContextOptions
        {
            PreferredDeviceName = options.Device,
            MinimumLogLevel = VulkanLogLevel.Error,
            Log = (level, message) => error.WriteLine($"[vulkan {level.ToString().ToLowerInvariant()}] {message}"),
        };
        if (options.Floor && AddFloorIfMissing(engine) is { } floor)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Floor:    added a {floor.X:0.#} × {floor.Y:0.#} m stage floor (the scene has none)"));

        using var renderer = VulkanRenderer.Create(contextOptions);
        try
        {
            return Render(engine, renderer, options, output, total, watch, loadMs);
        }
        finally
        {
            engine.DetachRenderWorld();
        }
    }

    private static int Render(StageEngine engine, VulkanRenderer renderer, RenderOptions options, TextWriter output, Stopwatch total, Stopwatch watch,
        double loadMs)
    {
        var sync = engine.AttachRenderWorld(renderer.World);
        sync.LoadFixtureModelsInBackground = false;
        sync.HazeOverride = options.Haze;

        int ticks = Math.Max(1, (int)Math.Ceiling(options.Time / TickSeconds));
        double step = options.Time > 0 ? options.Time / ticks : 0;
        engine.Tick(0);
        for (int i = 0; i < ticks; i++) engine.Tick(step);
        sync.CompletePendingLoads(TimeSpan.FromMinutes(5));
        double simulateMs = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"Scene:    {sync.InstanceCount} instances, {engine.Lights.Length} lights " +
                         $"({engine.LightCollector.FixtureLightCount} beams, {engine.LightCollector.FlameLightCount / 2} flame samples), " +
                         $"simulated {CliArgs.F(engine.Time)} s");

        watch.Restart();
        var camera = CreateCamera(engine, options);
        var bounds = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);
        if (!bounds.IsEmpty) output.WriteLine($"Bounds:   {Format(bounds.Min)} … {Format(bounds.Max)} m");
        var view = new ViewOptions
        {
            Shading = options.Mode,
            Overlay = options.Overlay,
            ShowGrid = options.Grid,
            ShowSelectionOutline = false,
        };
        var settings = new PreviewSettings
        {
            AutoExposure = options.AutoExposure,
            Volumetrics = true,
        };
        using var target = renderer.CreateTarget(options.Width, options.Height);
        int frames = options.AutoExposure ? Math.Max(options.Frames, 12) : options.Frames;
        for (int i = 0; i < frames; i++) renderer.Render(target, camera, view, settings, (float)TickSeconds);
        var image = renderer.ReadPixels(target);
        double renderMs = watch.Elapsed.TotalMilliseconds;
        ImageImporter.SavePng(image, options.Output);

        engine.RefreshIssues();
        var issues = engine.Issues.Current;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Camera:   eye {Format(camera.Position)}, EV100 {camera.Ev100:0.#}{(options.AutoExposure ? " (auto exposure)" : options.Ev100 is null ? " (incident reading of the floor)" : "")}"));
        output.WriteLine($"Image:    {options.Width} x {options.Height}, {options.Mode}, overlay {options.Overlay}, {renderer.Info.DeviceName}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Timing:   load {loadMs:0} ms, look + simulation {simulateMs:0} ms, render {renderMs:0} ms ({frames} frames), total {total.Elapsed.TotalMilliseconds:0} ms"));
        PrintIssues(output, issues, 15);
        output.WriteLine($"Wrote {Path.GetFullPath(options.Output)}");
        return Program.ExitOk;
    }

    /// <summary>
    /// Adds a wooden stage floor under the scene when it has no floor (MVR exports often contain only the rig), so the
    /// light has something to land on: the content's footprint plus 3 m on every side, top face at the lowest content
    /// height (at most 0). Returns its size, or null when the scene has a floor or no content.
    /// </summary>
    public static Vector2? AddFloorIfMissing(StageEngine engine)
    {
        foreach (var (_, wall) in engine.Scene.Query<Core.SceneGraph.Components.WallComponent>())
            if (wall.Kind == Core.SceneGraph.Components.WallKind.Floor) return null;
        var bounds = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);
        if (bounds.IsEmpty) return null;
        var size = new Vector2(bounds.Size.X + 6f, bounds.Size.Y + 6f);
        var floor = new Core.SceneGraph.Entity("Stage floor (render)")
        {
            Local = new Core.Math.Transform(new Vector3(bounds.Center.X, bounds.Center.Y, MathF.Min(0f, bounds.Min.Z)), Quaternion.Identity, Vector3.One),
        };
        floor.Add(new Core.SceneGraph.Components.WallComponent
        {
            Kind = Core.SceneGraph.Components.WallKind.Floor,
            Width = size.X,
            Height = size.Y,
            Thickness = 0.1f,
            MaterialId = Core.SceneGraph.Components.MaterialIds.WoodStage,
        });
        engine.Scene.Add(floor);
        return size;
    }

    /// <summary>The camera of the options: explicit eye/target, or a preset framing the scene's bounds.</summary>
    public static RenderCamera CreateCamera(StageEngine engine, RenderOptions options)
    {
        RenderCamera camera;
        if (options.Camera is { } eye && options.Target is { } target)
        {
            camera = RenderCamera.LookAt(eye, Vector3.DistanceSquared(eye, target) < 1e-8f ? target + Vector3.UnitY : target, options.FovY);
        }
        else
        {
            var bounds = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);
            camera = ViewFraming.Frame(bounds, options.View, options.FovY, (float)options.Width / options.Height);
        }
        if (options.Ev100 is { } ev) camera.Ev100 = ev;
        else
        {
            // meter the light on the floor like a lighting designer's incident meter (median of the lit floor)
            var bounds = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);
            camera.Ev100 = engine.LightMeter.IncidentEv100(bounds, bounds.IsEmpty ? 0f : bounds.Min.Z + 0.01f);
        }
        return camera;
    }

    /// <summary>Prints the most severe issues and a count per severity.</summary>
    public static void PrintIssues(TextWriter writer, IReadOnlyList<Issue> issues, int max)
    {
        if (issues.Count == 0)
        {
            writer.WriteLine("Issues:   none");
            return;
        }
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        int notes = issues.Count - errors - warnings;
        writer.WriteLine($"Issues:   {errors} errors, {warnings} warnings, {notes} notes");
        foreach (var issue in issues.OrderByDescending(i => i.Severity).Take(max))
            writer.WriteLine($"  {Severity(issue.Severity)} {issue.Code}: {issue.Message}");
        if (issues.Count > max) writer.WriteLine($"  … {issues.Count - max} more (candela validate lists all)");
    }

    internal static string Severity(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "error  ",
        IssueSeverity.Warning => "warning",
        _ => "note   ",
    };

    private static string Format(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.##}, {v.Y:0.##}, {v.Z:0.##})");
}
