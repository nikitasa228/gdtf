using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Dmx.Input;
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

    /// <summary>The demo look (<see cref="StageLooks.ApplyDemo"/>): fans and crossings, saturated colours, gobos, haze.</summary>
    Demo,
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
    /// <summary>
    /// Exposure value at ISO 100 (<c>--ev N</c>), or null for the incident-light reading of the stage floor — the fixed
    /// exposure of <c>--ev floor</c>, and where auto exposure starts adapting from.
    /// </summary>
    public float? Ev100 { get; private set; }

    public bool Floor { get; private set; } = true;

    /// <summary>
    /// The work light (<c>--work-light off|auto|on</c>, default off): a dim neutral key and ambient light for scenes
    /// whose fixtures are dark. A render never gets it unless asked.
    /// </summary>
    public WorkLightMode WorkLight { get; private set; } = WorkLightMode.Off;

    /// <summary>
    /// The renderer adapts the exposure to the picture like an eye or a camera on a stage (default; <c>--ev auto</c>,
    /// <c>--auto-exposure</c>). A stage picture is mostly dark air and set with bright beams and pools, which a reading
    /// of the floor alone says little about. False with a fixed exposure: <c>--ev N</c> or <c>--ev floor</c>.
    /// </summary>
    public bool AutoExposure { get; private set; } = true;
    public int Frames { get; private set; } = 6;

    /// <summary>The start of the render (<c>--time</c>): seconds, or a timecode looked up in the DMX recording.</summary>
    public TimeSpec Start { get; private set; } = TimeSpec.FromSeconds(0);

    /// <summary>The last frame of an image sequence (<c>--end</c>, inclusive), or null for one image.</summary>
    public TimeSpec? End { get; private set; }

    /// <summary>Frames per second of an image sequence (<c>--fps</c>).</summary>
    public double FrameRate { get; private set; } = 25;

    /// <summary>Path-traced final render (<c>--final</c>) instead of the real-time view.</summary>
    public bool Final { get; private set; }

    /// <summary>Samples per pixel of the final render (<c>--samples</c>).</summary>
    public int Samples { get; private set; } = DefaultSamples;

    /// <summary>True when <c>--samples</c> was given (with <c>--seconds</c> alone the render runs until the time is up).</summary>
    public bool SamplesGiven { get; private set; }

    /// <summary>Time budget of each final frame (<c>--seconds</c>), or null.</summary>
    public double? Seconds { get; private set; }

    /// <summary>Linear HDR OpenEXR output of the final render (<c>--exr</c>), or null.</summary>
    public string? Exr { get; private set; }

    /// <summary>Denoise the final render (<c>--denoise</c>; the EXR keeps the raw result as the layer "raw").</summary>
    public bool Denoise { get; private set; }

    /// <summary>Surface bounces of the final render (<c>--bounces</c>).</summary>
    public int Bounces { get; private set; } = 6;

    /// <summary>Scattering events in haze and smoke per path (<c>--volume-bounces</c>).</summary>
    public int VolumeBounces { get; private set; } = 2;

    /// <summary>Seed of the final render's random sequences (<c>--seed</c>).</summary>
    public uint Seed { get; private set; }

    /// <summary>Firefly clamp of the final render, cd/m² (<c>--clamp</c>; 0 = off).</summary>
    public float Clamp { get; private set; }

    /// <summary>Samples per pixel of <c>--final</c> without <c>--samples</c>.</summary>
    public const int DefaultSamples = 64;

    public bool Grid { get; private set; }
    public string Device { get; private set; } = "";
    public List<string> Libraries { get; } = new();
    public string? Cache { get; private set; }

    public static RenderOptions Parse(string[] args)
    {
        var o = new RenderOptions();
        bool exposure = true, autoExposure = false; // --ev's choice; --auto-exposure (adapting from --ev N when both are given)
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
                        case "demo":
                            o.Look = RenderLook.Demo;
                            break;
                        case "dmx-file":
                            o.Look = RenderLook.DmxFile;
                            o.Recording = CliArgs.Value(args, ref i, "--look dmx-file");
                            break;
                        default:
                            throw new UsageException($"Unknown look '{look}' (open-white, demo, show, dmx-file <recording>).");
                    }
                    break;
                case "--time":
                    o.Start = TimeSpec.Parse(name, CliArgs.Value(args, ref i, name));
                    o.Time = o.Start.Seconds ?? 0;
                    break;
                case "--end":
                    o.End = TimeSpec.Parse(name, CliArgs.Value(args, ref i, name));
                    break;
                case "--fps":
                    o.FrameRate = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 1f, 240f);
                    break;
                case "--final":
                    o.Final = true;
                    break;
                case "--samples":
                    o.Samples = CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 1, VulkanFinalRender.MaxSamples);
                    o.SamplesGiven = true;
                    break;
                case "--seconds":
                    o.Seconds = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 0.01f, 864000f);
                    break;
                case "--exr":
                    o.Exr = CliArgs.Value(args, ref i, name);
                    break;
                case "--denoise":
                    o.Denoise = true;
                    break;
                case "--bounces":
                    o.Bounces = CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 0, VulkanFinalRender.MaxBounceLimit);
                    break;
                case "--volume-bounces":
                    o.VolumeBounces = CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 0, VulkanFinalRender.MaxBounceLimit);
                    break;
                case "--seed":
                    o.Seed = (uint)CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 0, int.MaxValue);
                    break;
                case "--clamp":
                    o.Clamp = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 0f, FinalRenderOptions.MaxFireflyClamp);
                    break;
                case "--ev":
                    string ev = CliArgs.Value(args, ref i, name);
                    switch (ev.ToLowerInvariant())
                    {
                        case "auto":
                            o.Ev100 = null;
                            exposure = true;
                            break;
                        case "floor" or "incident":
                            o.Ev100 = null;
                            exposure = false;
                            break;
                        default:
                            o.Ev100 = CliArgs.Number(name, ev, -10f, 25f);
                            exposure = false;
                            break;
                    }
                    break;
                case "--auto-exposure":
                    autoExposure = true;
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
                case "--work-light":
                    o.WorkLight = CliArgs.Value(args, ref i, name).ToLowerInvariant() switch
                    {
                        "off" => WorkLightMode.Off,
                        "auto" => WorkLightMode.Auto,
                        "on" => WorkLightMode.On,
                        var v => throw new UsageException($"Unknown work light '{v}' (off, auto, on)."),
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
        if (Directory.Exists(o.Output)) throw new UsageException($"--out '{o.Output}' is a folder; give the name of the PNG file to write.");
        if (o.Camera.HasValue != o.Target.HasValue) throw new UsageException("--camera and --target go together.");
        if (o.Final)
        {
            if (o.Mode != ViewShading.Full) throw new UsageException("--final renders the full shading; --mode does not apply.");
            if (o.Overlay != OverlayMode.None) throw new UsageException("--final renders no analysis overlays; --overlay does not apply.");
            if (o.Grid) throw new UsageException("--final renders no grid; --grid does not apply.");
            if (o.Seconds is not null && !o.SamplesGiven) o.Samples = VulkanFinalRender.MaxSamples; // until the time is up
        }
        else if (o.Exr is not null || o.Denoise || o.SamplesGiven || o.Seconds is not null)
        {
            throw new UsageException("--samples, --seconds, --exr and --denoise belong to --final (the path-traced render).");
        }
        if (o.Exr is not null && Directory.Exists(o.Exr)) throw new UsageException($"--exr '{o.Exr}' is a folder; give the name of the EXR file to write.");
        if (o.End is { } end && end.Seconds is { } e && o.Start.Seconds is { } b && e < b) throw new UsageException("--end must not be before --time.");
        if ((o.Start.IsTimecode || o.End is { IsTimecode: true }) && o.Recording is null)
            throw new UsageException("A timecode needs --look dmx-file <recording> (it is looked up in the recording's timecode track).");
        o.AutoExposure = exposure || autoExposure;
        return o;
    }
}

/// <summary>
/// A point in time on the command line: seconds (of the engine clock, i.e. of a DMX recording played from its start), or
/// a SMPTE timecode <c>HH:MM:SS:FF</c> (<c>HH:MM:SS;FF</c> drop-frame) looked up in the recording's timecode track.
/// </summary>
/// <param name="Seconds">The time in seconds, or null for a timecode.</param>
/// <param name="Hours">Timecode hours.</param>
/// <param name="Minutes">Timecode minutes.</param>
/// <param name="SecondsOfMinute">Timecode seconds.</param>
/// <param name="FrameNumber">Timecode frames.</param>
/// <param name="DropFrame">The timecode was written with ';' before the frames (29.97 drop-frame).</param>
internal readonly record struct TimeSpec(double? Seconds, int Hours = 0, int Minutes = 0, int SecondsOfMinute = 0, int FrameNumber = 0, bool DropFrame = false)
{
    /// <summary>True for a timecode (looked up in the recording).</summary>
    public bool IsTimecode => Seconds is null;

    public static TimeSpec FromSeconds(double seconds) => new(seconds);

    /// <summary>Parses seconds (0 … 36000) or a timecode.</summary>
    public static TimeSpec Parse(string name, string value)
    {
        if (!value.Contains(':') && !value.Contains(';'))
            return FromSeconds(CliArgs.Number(name, value, 0f, 36000f));
        var parts = value.Split(':', ';');
        if (parts.Length != 4)
            throw new UsageException($"{name} must be seconds or a timecode HH:MM:SS:FF (got '{value}').");
        return new TimeSpec(null, CliArgs.Integer(name, parts[0], 0, 23), CliArgs.Integer(name, parts[1], 0, 59), CliArgs.Integer(name, parts[2], 0, 59),
            CliArgs.Integer(name, parts[3], 0, 29), value.Contains(';'));
    }

    /// <summary>
    /// Seconds on the recording's timeline: the seconds themselves, or the moment the timecode was received (its frame
    /// rate from the recording's first timecode, drop-frame when written with ';'). A timecode the recording never
    /// received counts from the recording's first timecode (or from 00:00:00:00 without one) when that lands inside the
    /// recording. Null otherwise.
    /// </summary>
    public double? Resolve(DmxRecording? recording)
    {
        if (Seconds is { } s)
            return s;
        if (recording is null)
            return null;
        var type = DropFrame ? TimecodeType.Df2997 : recording.FirstTimecode?.Type ?? TimecodeType.Ebu25;
        var timecode = new Timecode(Hours, Minutes, SecondsOfMinute, FrameNumber, type);
        if (recording.FindTimecode(timecode) is { } found)
            return found;
        double offset = recording.FirstTimecode is { } first ? timecode.TotalSeconds - first.TotalSeconds : timecode.TotalSeconds;
        return offset >= 0 && offset <= recording.Duration + 1e-6 ? offset : null;
    }

    /// <inheritdoc />
    public override string ToString() =>
        Seconds is { } s ? CliArgs.F(s, "0.###") + " s" : $"{Hours:D2}:{Minutes:D2}:{SecondsOfMinute:D2}{(DropFrame ? ';' : ':')}{FrameNumber:D2}";
}

/// <summary>The frames a render writes: one at <paramref name="Start"/>, or a sequence of <paramref name="Count"/> frames.</summary>
/// <param name="Start">Time of the first frame on the engine clock (= seconds into a DMX recording), s.</param>
/// <param name="Count">Number of frames (1 for a single image).</param>
/// <param name="FrameRate">Frames per second of a sequence.</param>
internal sealed record FramePlan(double Start, int Count, double FrameRate)
{
    /// <summary>Time of frame <paramref name="index"/>, s.</summary>
    public double TimeOf(int index) => Start + index / FrameRate;

    /// <summary>
    /// The file of frame <paramref name="index"/>: <paramref name="path"/> itself for a single image, otherwise numbered
    /// from 1 before the extension with at least four digits (<c>show_0001.png</c>, like the editor's render job).
    /// </summary>
    public string PathOf(string path, int index)
    {
        if (Count <= 1) return path;
        string folder = Path.GetDirectoryName(path) ?? "";
        int digits = Math.Max(4, Count.ToString(CultureInfo.InvariantCulture).Length);
        string number = (index + 1).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "_" + number + Path.GetExtension(path));
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

    /// <summary>
    /// Time each rendered frame stands for while auto exposure adapts, s. The CLI renders a still: the renderer adapts
    /// with a time constant of about 0.7 s, so at the 1/30 s of a live frame 12 frames would leave the exposure less than
    /// halfway to the picture's; at half a second per frame it settles within the 12 frames.
    /// </summary>
    public const float AdaptationSecondsPerFrame = 0.5f;

    /// <summary>Fewest frames rendered with auto exposure (the measurement is read back a few frames late).</summary>
    public const int AutoExposureFrames = 12;

    /// <summary>Space around the content of the preset views (1 = touching the image edges).</summary>
    public const float FramingMargin = 1.1f;

    /// <summary>Most frames of one image sequence.</summary>
    public const int MaxSequenceFrames = 100_000;

    /// <summary>Time between the progress lines of a final render, s.</summary>
    public const double ProgressIntervalSeconds = 10.0;

    /// <summary>Work of the final render between checks for progress and cancellation.</summary>
    private static readonly TimeSpan AdvanceSlice = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Runs <c>candela render</c>. Ctrl+C (or <paramref name="cancellationToken"/>) stops a final render after the work in
    /// flight and writes what was rendered so far (exit code <see cref="Program.ExitCancelled"/>); a second Ctrl+C ends
    /// the process.
    /// </summary>
    public static int Run(RenderOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (interrupt.IsCancellationRequested)
                return;
            e.Cancel = true;
            interrupt.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return RunCore(options, output, error, interrupt.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static int RunCore(RenderOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
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
            case RenderLook.Demo:
                int lit = StageLooks.ApplyDemo(engine, new DemoLookOptions { Undoable = false });
                output.WriteLine($"Look:     demo on {lit} fixtures (fans and crossings, saturated colours, gobos, haze)");
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
                output.WriteLine($"Look:     DMX recording '{recording.Name}' ({recording.Frames.Count} frames, {CliArgs.F(recording.Duration)} s" +
                                 (recording.FirstTimecode is { } first ? $", timecode from {first}" : "") + ")");
                break;
            default:
                output.WriteLine("Look:     as saved");
                break;
        }

        if (PlanFrames(options, recording, error) is not { } plan) return Program.ExitFailure;
        if (plan.Count > 1)
            output.WriteLine($"Frames:   {plan.Count} from {CliArgs.F(plan.Start, "0.###")} s to {CliArgs.F(plan.TimeOf(plan.Count - 1), "0.###")} s " +
                             $"at {CliArgs.F(plan.FrameRate, "0.###")} fps");

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
            return Render(engine, renderer, options, plan, output, error, total, watch, loadMs, cancellationToken);
        }
        finally
        {
            engine.DetachRenderWorld();
        }
    }

    /// <summary>
    /// The frames to render: one at <c>--time</c>, or with <c>--end</c> every 1/<c>--fps</c> s from <c>--time</c> up to and
    /// including <c>--end</c>, timecodes looked up in the recording. Null (with the reason written to
    /// <paramref name="error"/>) when the recording never received a timecode.
    /// </summary>
    internal static FramePlan? PlanFrames(RenderOptions options, DmxRecording? recording, TextWriter error)
    {
        if (options.Start.Resolve(recording) is not { } start)
        {
            error.WriteLine($"The DMX recording '{recording?.Name}' does not reach timecode {options.Start} (--time).");
            return null;
        }
        if (options.End is not { } endSpec) return new FramePlan(start, 1, options.FrameRate);
        if (endSpec.Resolve(recording) is not { } end)
        {
            error.WriteLine($"The DMX recording '{recording?.Name}' does not reach timecode {endSpec} (--end).");
            return null;
        }
        if (end < start) throw new UsageException($"--end ({endSpec}) is before --time ({options.Start}).");
        double frames = Math.Floor((end - start) * options.FrameRate + 1e-6) + 1;
        if (frames > MaxSequenceFrames)
            throw new UsageException($"--time … --end at {CliArgs.F(options.FrameRate, "0.###")} fps makes {frames:0} frames; at most {MaxSequenceFrames} are rendered at once.");
        return new FramePlan(start, (int)frames, options.FrameRate);
    }

    /// <summary>
    /// Advances the engine from <paramref name="from"/> to <paramref name="to"/> seconds in equal steps of at most
    /// <see cref="TickSeconds"/> (at least one step; DMX playback, motors, smoke and fire follow).
    /// </summary>
    internal static void Simulate(StageEngine engine, double from, double to)
    {
        double span = Math.Max(to - from, 0);
        int ticks = Math.Max(1, (int)Math.Ceiling(span / TickSeconds - 1e-9));
        double step = span / ticks;
        for (int i = 0; i < ticks; i++) engine.Tick(step);
    }

    private static int Render(StageEngine engine, VulkanRenderer renderer, RenderOptions options, FramePlan plan, TextWriter output, TextWriter error,
        Stopwatch total, Stopwatch watch, double loadMs, CancellationToken cancellationToken)
    {
        var sync = engine.AttachRenderWorld(renderer.World);
        sync.LoadFixtureModelsInBackground = false;
        sync.HazeOverride = options.Haze;
        // the binding leaves the work light out while the view shows the lux overlay (it measures stage light only): the
        // view is the picture rendered here, not the overlay saved with a project (the CLI never saves the project)
        engine.View.View ??= new ViewOptions();
        engine.View.View.Overlay = options.Overlay;
        sync.WorkLight = options.WorkLight;

        engine.Tick(0);
        Simulate(engine, 0, plan.Start);
        sync.CompletePendingLoads(TimeSpan.FromMinutes(5));
        double simulateMs = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"Scene:    {sync.InstanceCount} instances, {engine.Lights.Length} lights " +
                         $"({engine.LightCollector.FixtureLightCount} beams, {engine.LightCollector.FlameLightCount / 2} flame samples)" +
                         $"{(sync.IsWorkLightOn ? $" + work light ({WorkLightRig.LightCount} lights)" : "")}, simulated {CliArgs.F(engine.Time)} s");

        watch.Restart();
        var camera = CreateCamera(engine, options);
        var bounds = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);
        if (!bounds.IsEmpty) output.WriteLine($"Bounds:   {Format(bounds.Min)} … {Format(bounds.Max)} m");
        var result = options.Final
            ? RenderFinal(engine, renderer, options, plan, camera, output, error, cancellationToken)
            : RenderRealTime(engine, renderer, options, plan, camera, bounds, output, cancellationToken);
        if (result.Code != Program.ExitOk) return result.Code;
        double renderMs = watch.Elapsed.TotalMilliseconds;

        engine.RefreshIssues();
        var issues = engine.Issues.Current;
        output.WriteLine($"Camera:   eye {Format(camera.Position)}, {result.Exposure}");
        output.WriteLine($"Image:    {options.Width} x {options.Height}, {result.Description}, {renderer.Info.DeviceName}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Timing:   load {loadMs:0} ms, look + simulation {simulateMs:0} ms, render {renderMs:0} ms ({result.Work}), total {total.Elapsed.TotalMilliseconds:0} ms"));
        PrintIssues(output, issues, 15);
        if (result.Written == 0)
        {
            output.WriteLine("Cancelled before the first sample: nothing was written.");
            return Program.ExitCancelled;
        }
        if (plan.Count > 1)
            output.WriteLine($"Wrote {result.Written} of {plan.Count} frames: {Path.GetFullPath(plan.PathOf(options.Output, 0))} …");
        else
            output.WriteLine($"Wrote {Path.GetFullPath(options.Output)}");
        if (options.Exr is { } exr)
            output.WriteLine(plan.Count > 1 ? $"Wrote {result.Written} EXR frames: {Path.GetFullPath(plan.PathOf(exr, 0))} …" : $"Wrote {Path.GetFullPath(exr)}");
        if (result.Cancelled)
        {
            output.WriteLine(options.Final
                ? "Cancelled: the last image holds the samples rendered until then."
                : $"Cancelled after {result.Written} of {plan.Count} frames.");
            return Program.ExitCancelled;
        }
        return Program.ExitOk;
    }

    /// <summary>What a render mode did: exit code, frames written, and the words for the summary lines.</summary>
    private readonly record struct RenderResult(int Code, int Written, bool Cancelled, string Exposure, string Description, string Work);

    /// <summary>The real-time renderer: each frame rendered a few times (temporal accumulation, auto exposure) and read back.</summary>
    private static RenderResult RenderRealTime(StageEngine engine, VulkanRenderer renderer, RenderOptions options, FramePlan plan, RenderCamera camera,
        Core.Math.Aabb bounds, TextWriter output, CancellationToken cancellationToken)
    {
        var view = new ViewOptions
        {
            Shading = options.Mode,
            Overlay = options.Overlay,
            ShowGrid = options.Grid,
            ShowSelectionOutline = false,
        };
        if (options.Overlay == OverlayMode.Lux)
        {
            view.LuxGradient = LuxGradientFor(engine, bounds);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Lux:      overlay {view.LuxGradient.RangeMin:0} … {view.LuxGradient.RangeMax:0} lx (grey outside)"));
        }
        var settings = new PreviewSettings
        {
            AutoExposure = options.AutoExposure,
            Volumetrics = true,
        };
        using var target = renderer.CreateTarget(options.Width, options.Height);
        int written = 0, passes = 0;
        bool cancelled = false;
        for (int f = 0; f < plan.Count; f++)
        {
            if (f > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                Simulate(engine, plan.TimeOf(f - 1), plan.TimeOf(f));
            }

            // The first image lets auto exposure settle on the picture (at half a second per frame); the following frames
            // of a sequence adapt in real time, one frame interval spread over their passes.
            int frames = f == 0 && options.AutoExposure ? Math.Max(options.Frames, AutoExposureFrames) : options.Frames;
            float frameSeconds = !options.AutoExposure ? (float)TickSeconds
                : f == 0 ? AdaptationSecondsPerFrame
                : (float)(1.0 / plan.FrameRate / options.Frames);
            for (int i = 0; i < frames; i++) renderer.Render(target, camera, view, settings, frameSeconds);
            passes += frames;
            string path = plan.PathOf(options.Output, f);
            ImageImporter.SavePng(renderer.ReadPixels(target), path);
            written++;
            if (plan.Count > 1) output.WriteLine($"Frame:    {f + 1}/{plan.Count} at {CliArgs.F(plan.TimeOf(f), "0.###")} s → {path}");
        }

        string exposure = options.AutoExposure
            ? string.Create(CultureInfo.InvariantCulture, $"auto exposure (adapting from EV100 {camera.Ev100:0.#})")
            : string.Create(CultureInfo.InvariantCulture, $"EV100 {camera.Ev100:0.#}{(options.Ev100 is null ? " (incident reading of the floor)" : "")}");
        return new RenderResult(Program.ExitOk, written, cancelled, exposure, $"{options.Mode}, overlay {options.Overlay}", $"{passes} frames");
    }

    /// <summary>
    /// The path tracer: each frame accumulates <c>--samples</c> samples per pixel (or until <c>--seconds</c> are up), is
    /// written as PNG through the same display transform as the real-time view and, with <c>--exr</c>, as linear OpenEXR.
    /// Auto exposure meters the first frame; a sequence keeps that exposure (no flicker between frames).
    /// </summary>
    private static RenderResult RenderFinal(StageEngine engine, VulkanRenderer renderer, RenderOptions options, FramePlan plan, RenderCamera camera,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var settings = new FinalRenderSettings
        {
            Width = options.Width,
            Height = options.Height,
            SamplesPerPixel = options.Samples,
            MaxBounces = options.Bounces,
            MaxVolumeBounces = options.VolumeBounces,
            Denoise = options.Denoise,
            FrameRate = plan.FrameRate,
            OutputPath = options.Output,
        };
        string detail = $"path traced, {(options.SamplesGiven || options.Seconds is null ? options.Samples + " samples per pixel" : "samples")}" +
                        (options.Seconds is { } budget ? $" within {CliArgs.F(budget, "0.##")} s per frame" : "") +
                        $", {options.Bounces} bounces, {options.VolumeBounces} volume bounces" +
                        (options.Clamp > 0 ? $", clamp {CliArgs.F(options.Clamp, "0.##")} cd/m²" : "") + (options.Denoise ? ", denoised" : "") +
                        (options.Seed != 0 ? $", seed {options.Seed}" : "");
        bool autoExposure = options.AutoExposure;
        float ev = camera.Ev100;
        int written = 0;
        long samples = 0;
        bool cancelled = false;
        for (int f = 0; f < plan.Count && !cancelled; f++)
        {
            if (f > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                Simulate(engine, plan.TimeOf(f - 1), plan.TimeOf(f));
            }

            VulkanFinalRender render;
            try
            {
                render = renderer.StartFinalRender(camera, settings, new FinalRenderOptions
                {
                    Seed = options.Seed,
                    TimeLimit = options.Seconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                    FireflyClamp = options.Clamp,
                    AutoExposure = autoExposure,
                    BackgroundLuminance = new ViewOptions().BackgroundLuminance,
                });
            }
            catch (NotSupportedException e)
            {
                error.WriteLine(e.Message);
                error.WriteLine(renderer.Info.SupportsRayTracing
                    ? "Render a smaller image (--width, --height), or pick a GPU with more memory with --device <name>."
                    : "Pick a GPU with ray queries with --device <name>, or render without --final (the real-time view).");
                return new RenderResult(Program.ExitNoVulkan, written, false, "", detail, "");
            }

            using (render)
            {
                var frameWatch = Stopwatch.StartNew();
                double nextReport = ProgressIntervalSeconds;
                while (!render.IsComplete)
                {
                    render.Advance(AdvanceSlice, cancellationToken);
                    if (render.IsComplete || frameWatch.Elapsed.TotalSeconds < nextReport) continue;
                    nextReport += ProgressIntervalSeconds;
                    string of = options.Seconds is null || options.SamplesGiven ? $"/{render.SamplesTotal}" : "";
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"Progress: {(plan.Count > 1 ? $"frame {f + 1}/{plan.Count}, " : "")}{render.SamplesDone}{of} samples per pixel, {frameWatch.Elapsed.TotalSeconds:0} s"));
                }

                cancelled = render.IsCancelled;
                if (render.SamplesDone == 0) break;
                string path = plan.PathOf(options.Output, f);
                ImageImporter.SavePng(render.Preview(), path);
                if (options.Exr is { } exr) WriteExr(render, plan.PathOf(exr, f), options.Denoise);
                written++;
                samples += render.SamplesDone;
                ev = Ev100Of(render.Exposure);
                if (autoExposure && plan.Count > 1)
                {
                    // The rest of the sequence keeps the first frame's exposure.
                    camera.Ev100 = ev;
                    autoExposure = false;
                }
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Frame:    {f + 1}/{plan.Count} at {CliArgs.F(plan.TimeOf(f), "0.###")} s: {render.SamplesDone} samples per pixel in " +
                    $"{render.Elapsed.TotalSeconds:0.0} s, EV100 {ev:0.0}{(cancelled ? " (cancelled)" : "")} → {path}"));
            }
        }

        string exposure = options.AutoExposure
            ? string.Create(CultureInfo.InvariantCulture, $"auto exposure (metered from the {(plan.Count > 1 ? "first " : "")}image: EV100 {ev:0.0})")
            : string.Create(CultureInfo.InvariantCulture, $"EV100 {ev:0.#}{(options.Ev100 is null ? " (incident reading of the floor)" : "")}");
        return new RenderResult(Program.ExitOk, written, cancelled, exposure, detail,
            written > 0 ? $"{samples / written} samples per pixel per frame" : "no samples");
    }

    /// <summary>
    /// Writes the final render as OpenEXR (ZIP): the result as R, G, B, A (32-bit float, linear Rec.2020, scaled by the
    /// exposure of the PNG; the header's <c>whiteLuminance</c> = 1 / exposure turns the values back into cd/m²), the
    /// raw result as the layer "raw" when denoised, and the denoiser's guides: "albedo" (half), "N" (world normal, half)
    /// and Z (view depth in metres, float; 10⁵ where the camera sees no surface).
    /// </summary>
    internal static void WriteExr(VulkanFinalRender render, string path, bool denoised)
    {
        var result = render.ResultHdr();
        var channels = new List<ExrChannel>();
        if (denoised) channels.AddRange(OpenExr.SplitRgba(render.RawResultHdr(), "raw.", ExrPixelType.Float));
        var (albedo, normalDepth) = render.ReadGuides();
        channels.AddRange(OpenExr.SplitRgba(albedo, "albedo.", ExrPixelType.Half));
        channels.AddRange(OpenExr.SplitRgba(normalDepth, "N.", ExrPixelType.Half, names: "XYZ"));
        channels.Add(OpenExr.SplitRgba(normalDepth, "", ExrPixelType.Float, includeAlpha: true, names: "XYZZ").Last());
        float exposure = render.Exposure > 0f && float.IsFinite(render.Exposure) ? render.Exposure : 1f;
        OpenExr.WriteRgba(path, result, ExrPixelType.Float, ExrCompression.Zip, channels, ExrColorimetry.Rec2020 with { WhiteLuminance = 1f / exposure });
    }

    /// <summary>The EV100 of an exposure factor (inverse of <see cref="Core.Math.Units.ExposureFromEv100"/>).</summary>
    internal static float Ev100Of(float exposure) =>
        exposure > 0f && float.IsFinite(exposure) ? MathF.Log2(1f / (1.2f * exposure)) : 0f;

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
            // frame the content itself (the corners of every object's box), not the scene's bounding box with its
            // empty volume, so the stage fills the picture
            var corners = new List<Vector3>();
            SceneBounds.CollectContentCorners(engine.Scene, corners, engine.Models.GetBounds);
            SceneBounds.CollectFixtureParts(engine.Poses, corners);
            camera = ViewFraming.Frame(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners), ViewFraming.Direction(options.View),
                options.FovY, (float)options.Width / options.Height, FramingMargin);
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

    /// <summary>
    /// The lux overlay's gradient for this scene: the default (1 … 5000 lx) when the floor stays within it, otherwise the
    /// same colours stretched up to the brightest floor reading (rounded up), so a bright rig is not drawn grey (out of
    /// range) everywhere.
    /// </summary>
    public static ColorGradient LuxGradientFor(StageEngine engine, Core.Math.Aabb bounds)
    {
        var gradient = ColorGradient.DefaultLux();
        if (bounds.IsEmpty) return gradient;
        const int n = 32;
        var size = bounds.Size;
        var lux = engine.LightMeter.IlluminanceOnGrid(
            new Vector3(bounds.Min.X + size.X / (2 * n), bounds.Min.Y + size.Y / (2 * n), bounds.Min.Z + 0.01f),
            new Vector3(size.X / n, 0f, 0f), new Vector3(0f, size.Y / n, 0f), n, n);
        float max = 0f;
        foreach (float e in lux)
            if (float.IsFinite(e)) max = MathF.Max(max, e);
        // the hot spots of narrow beams fall between grid points: read the floor where every beam axis meets it as well
        float floor = bounds.Min.Z + 0.01f;
        foreach (ref readonly var light in engine.Lights)
        {
            if (!(light.Direction.Z < -1e-3f)) continue;
            float t = (floor - light.Position.Z) / light.Direction.Z;
            if (!(t > 0f) || !float.IsFinite(t)) continue;
            var hit = light.Position + t * light.Direction;
            if (hit.X < bounds.Min.X || hit.X > bounds.Max.X || hit.Y < bounds.Min.Y || hit.Y > bounds.Max.Y) continue;
            float e = engine.LightMeter.HorizontalIlluminanceAt(hit);
            if (float.IsFinite(e)) max = MathF.Max(max, e);
        }
        if (!(max > gradient.RangeMax)) return gradient;
        float top = MathF.Pow(10f, MathF.Ceiling(MathF.Log10(max) * 4f) / 4f); // 10^(k/4): 5623, 10000, 17783 …
        float k = top / gradient.RangeMax;
        var stretched = new ColorGradient { RangeMin = gradient.RangeMin, RangeMax = top, Logarithmic = gradient.Logarithmic, OutOfRangeColor = gradient.OutOfRangeColor };
        for (int i = 0; i < gradient.Stops.Count; i++)
        {
            var stop = gradient.Stops[i];
            stretched.Stops.Add(i == 0 ? stop : stop with { Value = stop.Value * k });
        }
        return stretched;
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
