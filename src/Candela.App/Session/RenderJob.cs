using System.Diagnostics;
using System.Globalization;
using Candela.App.Ui;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph;
using Candela.Engine;
using Candela.Rendering;
using Candela.Rendering.Vulkan;

namespace Candela.App.Session;

/// <summary>State of the final render job.</summary>
internal enum RenderJobState
{
    /// <summary>Nothing has been rendered yet.</summary>
    Idle,

    /// <summary>Rendering.</summary>
    Running,

    /// <summary>All frames were written (or the user stopped the render and kept the samples done so far).</summary>
    Completed,

    /// <summary>Stopped by an error (see the message).</summary>
    Failed,

    /// <summary>Stopped by the user.</summary>
    Cancelled,
}

/// <summary>How the final render computes its pictures.</summary>
internal enum RenderMode
{
    /// <summary>
    /// The path tracer (<see cref="IRenderer.StartFinalRender(RenderCamera, FinalRenderSettings, FinalRenderOptions)"/>):
    /// physically based light transport — light bounced between surfaces and scattered more than once in haze and smoke —
    /// written as PNG and, on request, as linear HDR OpenEXR. Needs a GPU with ray queries; without them the job renders
    /// in <see cref="Fast"/> mode instead and says so.
    /// </summary>
    PathTraced,

    /// <summary>
    /// The real-time renderer at its highest quality, supersampled and accumulated over frames (seconds instead of
    /// minutes; PNG only).
    /// </summary>
    Fast,
}

/// <summary>A timecode range of a DMX recording rendered as an image sequence.</summary>
/// <param name="Recording">The recording that drives the fixtures.</param>
/// <param name="StartSeconds">First frame, seconds into the recording.</param>
/// <param name="EndSeconds">Last frame, seconds into the recording (inclusive).</param>
/// <param name="FrameRate">Frames per second of the sequence.</param>
internal sealed record RenderRange(DmxRecording Recording, double StartSeconds, double EndSeconds, double FrameRate)
{
    /// <summary>Number of frames of the range (at least 1).</summary>
    public int FrameCount => Math.Max(1, (int)Math.Floor((EndSeconds - StartSeconds) * FrameRate + 1e-6) + 1);

    /// <summary>Recording time of frame <paramref name="index"/>, s.</summary>
    public double TimeOf(int index) => StartSeconds + index / FrameRate;
}

/// <summary>Everything a final render needs, fixed when it starts.</summary>
/// <param name="Camera">Camera of the view (copied by the job; its EV100 is the manual exposure).</param>
/// <param name="View">View options of the <see cref="RenderMode.Fast"/> renderer (full shading, no overlay; copied).</param>
/// <param name="Settings">Real-time quality of the <see cref="RenderMode.Fast"/> renderer (high quality; copied).</param>
/// <param name="Width">Output width, px.</param>
/// <param name="Height">Output height, px.</param>
/// <param name="Supersample">
/// <see cref="RenderMode.Fast"/>: rendered at this many times the output resolution per axis, then box-filtered down.
/// </param>
/// <param name="Passes"><see cref="RenderMode.Fast"/>: frames rendered per output image (temporal accumulation).</param>
/// <param name="OutputPath">PNG file (a sequence numbers it: name_0001.png …; the EXR takes the same name with .exr).</param>
/// <param name="Range">The timecode range of an image sequence, or null for one frame.</param>
internal sealed record RenderRequest(
    RenderCamera Camera,
    ViewOptions View,
    PreviewSettings Settings,
    int Width,
    int Height,
    int Supersample,
    int Passes,
    string OutputPath,
    RenderRange? Range)
{
    /// <summary>
    /// The work light of the render (<see cref="RenderSync.WorkLight"/>): <see cref="WorkLightMode.Off"/> unless the user
    /// asks for it, so a final picture shows the stage light only, whatever the preview uses.
    /// </summary>
    public WorkLightMode WorkLight { get; init; } = WorkLightMode.Off;

    /// <summary>The renderer asked for (<see cref="RenderMode.PathTraced"/> falls back to Fast on a GPU without ray queries).</summary>
    public RenderMode Mode { get; init; } = RenderMode.Fast;

    /// <summary>
    /// The path tracer's settings: samples per pixel, surface and volume bounces, denoising and tone mapper (the size and
    /// output path of the request win over the ones stored here).
    /// </summary>
    public FinalRenderSettings Final { get; init; } = new();

    /// <summary>The path tracer's exposure (auto or the camera's EV100, compensation), background luminance, seed and clamp.</summary>
    public FinalRenderOptions FinalOptions { get; init; } = new();

    /// <summary>
    /// Path tracer only: also write each frame as linear HDR OpenEXR next to its PNG (the same layers as
    /// <c>candela render --final --exr</c>).
    /// </summary>
    public bool WriteExr { get; init; }
}

/// <summary>
/// The final render of the Render panel, File → Render and F12. <see cref="RenderMode.PathTraced"/> starts the path
/// tracer (<see cref="IRenderer.StartFinalRender(RenderCamera, FinalRenderSettings, FinalRenderOptions)"/>) for every
/// output frame and advances it within each editor frame's <see cref="Budget"/>, showing a progress image about once a
/// second, and writes the tone-mapped PNG and, when asked, the linear HDR EXR with the same layers as
/// <c>candela render --final --exr</c> — the same camera, settings and seed give the same files. On a GPU without ray
/// queries it falls back to <see cref="RenderMode.Fast"/>: the real-time renderer at high quality — every shadow, 256
/// volumetric slices, indirect light — supersampled (2 × 2 above 4 samples per pixel) and accumulated over several
/// frames, box-filtered in linear light to the output size. A timecode range plays the DMX recording through the
/// engine frame by frame (the engine's clock is held by the job meanwhile, see <see cref="HoldsEngineClock"/>; a
/// path-traced still holds it too, so the stage does not move under the samples) and writes an image sequence. Runs a
/// little each editor frame so the UI stays responsive; <see cref="Cancel"/> stops at once. Main thread only.
/// </summary>
internal sealed class RenderJobRunner : IDisposable
{
    /// <summary>Largest render target side of the Fast renderer, px (supersampling is reduced to stay below it).</summary>
    public const int MaxTargetSide = 8192;

    /// <summary>Largest side of a path-traced image, px.</summary>
    public const int MaxFinalSide = 16384;

    /// <summary>Most frames accumulated per output image (Fast).</summary>
    public const int MaxPasses = 256;

    /// <summary>Last frames of the accumulation that are averaged into the output (Fast).</summary>
    public const int AveragedPasses = 4;

    /// <summary>Seconds of the recording played before the first frame of a range, so moving heads reach their positions.</summary>
    public const double Preroll = 2.0;

    /// <summary>Largest side of the progress image shown in the panel, px (larger renders are shown scaled down).</summary>
    public const int PreviewMaxSide = 1024;

    /// <summary>Time spent rendering per editor frame (at least one render pass or path tracer dispatch runs).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// Planned GPU time of one path tracer dispatch (<see cref="VulkanFinalRender.DispatchSeconds"/>): a third of the
    /// <see cref="Budget"/>, so a frame whose budget runs out during a dispatch ends soon after it (the renderer's own
    /// default, a quarter of a second, would hold every editor frame that long on a large image).
    /// </summary>
    public static readonly TimeSpan DispatchTarget = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Shortest time between two progress images of the path tracer (each tone-maps — and denoises, when asked — and
    /// reads the whole image back).
    /// </summary>
    public static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A progress image is followed by at least this many times its own cost before the next one, so progress images
    /// take at most a tenth of the render (a large or denoised image on a slow GPU is shown less often instead of
    /// stalling the editor every second).
    /// </summary>
    public const int PreviewCostFactor = 9;

    /// <summary>Longest wait for fixture models still loading in the background before a path-traced frame starts anyway.</summary>
    public static readonly TimeSpan MaxLoadWait = TimeSpan.FromSeconds(30);

    private static readonly float[] SrgbToLinear = BuildSrgbTable();

    private RenderRequest? _request;
    private StageEngine? _playing;
    private StageEngine? _stage;
    private Scene? _scene;
    private double _rangeEngineTime;
    private IRenderTarget? _target;
    private IRenderer? _targetOwner;
    private float[]? _accumulator;
    private int _accumulated;
    private IFinalRender? _final;
    private RenderCamera? _camera;
    private bool _autoExposure;
    private bool _frameReady;
    private long _waitStarted;
    private long _lastPreview;
    private TimeSpan _previewWait;
    private long _startedAt;
    private long _endedAt;
    private bool _disposed;

    /// <summary>Raised on the main thread when the job starts, completes, fails or is cancelled (not when disposed).</summary>
    public event Action<RenderJobRunner>? StateChanged;

    /// <summary>Current state.</summary>
    public RenderJobState State { get; private set; }

    /// <summary>
    /// Releases the Fast renderer's target (the editor defers it until the GPU frames that used it are done); null
    /// disposes it at once (tests, tools with their own renderer).
    /// </summary>
    public Action<IDisposable>? DisposeTarget { get; set; }

    /// <summary>The renderer the job uses: the one asked for, or Fast after a fall-back (see <see cref="FellBack"/>).</summary>
    public RenderMode Mode { get; private set; } = RenderMode.Fast;

    /// <summary>True when a path-traced render was asked for but the GPU has no ray queries, so the Fast renderer was used.</summary>
    public bool FellBack { get; private set; }

    /// <summary>True while a path-traced frame waits for fixture models that are still loading in the background.</summary>
    public bool IsPreparing { get; private set; }

    /// <summary>Output frame being rendered (0-based) and the number of frames.</summary>
    public int FrameIndex { get; private set; }

    /// <summary>Frames of the job.</summary>
    public int FrameCount { get; private set; }

    /// <summary>Fast: render pass of the current frame.</summary>
    public int PassIndex { get; private set; }

    /// <summary>Fast: passes per output frame.</summary>
    public int Passes => _request?.Passes ?? 0;

    /// <summary>Path tracer: samples per pixel accumulated in the current frame.</summary>
    public int SamplesDone { get; private set; }

    /// <summary>Path tracer: samples per pixel of each frame.</summary>
    public int SamplesTotal { get; private set; }

    /// <summary>Path tracer: samples per pixel of the last frame written.</summary>
    public int LastSamples { get; private set; }

    /// <summary>Progress 0 … 1 over all frames (passes in Fast mode, samples when path traced).</summary>
    public double Progress
    {
        get
        {
            if (FrameCount == 0)
                return 0;
            double within = Mode == RenderMode.PathTraced
                ? SamplesTotal > 0 ? SamplesDone / (double)SamplesTotal : 0
                : Passes > 0 ? PassIndex / (double)Passes : 0;
            return Math.Clamp((FrameIndex + within) / FrameCount, 0, 1);
        }
    }

    /// <summary>Wall-clock time since the job started (until it ended).</summary>
    public TimeSpan Elapsed => _startedAt == 0 ? TimeSpan.Zero
        : _endedAt != 0 ? Stopwatch.GetElapsedTime(_startedAt, _endedAt)
        : Stopwatch.GetElapsedTime(_startedAt);

    /// <summary>The last PNG written, or null.</summary>
    public string? LastOutput { get; private set; }

    /// <summary>The last EXR written, or null.</summary>
    public string? LastExr { get; private set; }

    /// <summary>The PNG path the job writes to (the first frame of a sequence), or null before the first start.</summary>
    public string? OutputPath => _request is { } r ? FramePath(r.OutputPath, 0, FrameCount) : null;

    /// <summary>
    /// The latest picture of the job (8-bit sRGB RGBA, at most <see cref="PreviewMaxSide"/> px per side): the path
    /// tracer's progress about once a second and every frame written; null before the first one.
    /// </summary>
    public ImageData? PreviewImage { get; private set; }

    /// <summary>Incremented whenever <see cref="PreviewImage"/> changes (the panel uploads a new texture then).</summary>
    public int PreviewVersion { get; private set; }

    /// <summary>A one-line result or error, or null.</summary>
    public string? Message { get; private set; }

    /// <summary>True when <see cref="Message"/> describes a failure.</summary>
    public bool MessageIsError { get; private set; }

    /// <summary>
    /// True while a sequence or a path-traced frame is rendered: the job ticks the engine itself at the sequence's
    /// frame rate (or keeps the stage still under a path tracer's samples), so the editor must not tick it with
    /// wall-clock time meanwhile (<c>EditorSession.EngineClockHeld</c>).
    /// </summary>
    public bool HoldsEngineClock => State == RenderJobState.Running && _request is { } request &&
                                    (request.Range is not null || Mode == RenderMode.PathTraced);

    /// <summary>
    /// The work light the running job renders with (it decides alone while it runs: the viewports' setting must not leak
    /// into a final picture), or null while no job runs.
    /// </summary>
    public WorkLightMode? WorkLightInUse => State == RenderJobState.Running && _request is { } request ? request.WorkLight : null;

    /// <summary>
    /// Supersampling and accumulation of the Fast renderer for a requested sample count: 2 × 2 supersampling from 4
    /// samples per pixel (when the target stays within <see cref="MaxTargetSide"/>), the rest as accumulated frames (at
    /// most <see cref="MaxPasses"/>).
    /// </summary>
    public static (int Supersample, int Passes) Plan(int width, int height, int samplesPerPixel)
    {
        int spp = Math.Max(1, samplesPerPixel);
        int ss = spp >= 4 && width * 2 <= MaxTargetSide && height * 2 <= MaxTargetSide ? 2 : 1;
        int passes = Math.Clamp((int)Math.Ceiling(spp / (double)(ss * ss)), 1, MaxPasses);
        return (ss, passes);
    }

    /// <summary>The output path of frame <paramref name="index"/> (a sequence numbers the files, 4 digits at least).</summary>
    public static string FramePath(string outputPath, int index, int frameCount)
    {
        if (frameCount <= 1)
            return outputPath;
        string dir = Path.GetDirectoryName(outputPath) ?? "";
        string name = Path.GetFileNameWithoutExtension(outputPath);
        string ext = Path.GetExtension(outputPath);
        int digits = Math.Max(4, frameCount.ToString(CultureInfo.InvariantCulture).Length);
        return Path.Combine(dir, name + "_" + (index + 1).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + ext);
    }

    /// <summary>The EXR path that belongs to a PNG output path (same folder and name, extension .exr).</summary>
    public static string ExrPath(string outputPath) => Path.ChangeExtension(outputPath, ".exr");

    /// <summary>
    /// Starts a render (a running one is cancelled first). Returns false with a message when the request cannot run.
    /// </summary>
    public bool Start(RenderRequest request, StageEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        _startedAt = 0;
        _endedAt = 0;
        bool pathTraced = request.Mode == RenderMode.PathTraced;
        int maxSide = pathTraced ? MaxFinalSide : MaxTargetSide;
        if (request.Width < 16 || request.Height < 16 || request.Width > MaxFinalSide || request.Height > MaxFinalSide ||
            (!pathTraced && !FitsFastTarget(request)))
            return Fail(StageLoc.Format(StageStr.RenderBadSize, request.Width, request.Height, maxSide));
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            return Fail(StageLoc.Get(StageStr.RenderNoOutput));
        if (request.Range is { } range)
        {
            if (engine is null)
                return Fail(StageLoc.Get(StageStr.RenderNeedsEngine));
            if (!(range.FrameRate > 0) || !(range.EndSeconds >= range.StartSeconds) || range.FrameCount > 100_000)
                return Fail(StageLoc.Get(StageStr.RenderBadRange));
        }

        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath)) ?? ".";
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(StageLoc.Format(StageStr.RenderOutputFailed, request.OutputPath, e.Message));
        }

        _request = request;
        _stage = engine;
        _scene = engine?.Scene;
        Mode = request.Mode;
        FellBack = false;
        IsPreparing = false;
        FrameCount = request.Range?.FrameCount ?? 1;
        FrameIndex = 0;
        PassIndex = 0;
        SamplesDone = 0;
        SamplesTotal = Math.Clamp(request.Final.SamplesPerPixel, 1, VulkanFinalRender.MaxSamples);
        LastSamples = 0;
        _accumulated = 0;
        _accumulator = null;
        _camera = Copy(request.Camera);
        _autoExposure = request.FinalOptions.AutoExposure;
        _frameReady = false;
        LastOutput = null;
        LastExr = null;
        SetPreview(null);
        _startedAt = Stopwatch.GetTimestamp();
        _endedAt = 0;
        State = RenderJobState.Running;
        Message = null;
        MessageIsError = false;
        if (request.Range is { } r && engine is not null)
        {
            // Play from a little before the first frame so motors, haze and flames have settled; the clock is ours now.
            _playing = engine;
            try
            {
                double preroll = Math.Min(Preroll, r.StartSeconds);
                engine.Play(r.Recording, r.StartSeconds - preroll);
                int steps = (int)Math.Round(preroll * r.FrameRate);
                for (int i = 0; i < steps; i++)
                    engine.Tick(1.0 / r.FrameRate);
                engine.Player?.Seek(r.StartSeconds, engine.Time);
                engine.Tick(0);
                _rangeEngineTime = engine.Time;
            }
            catch (Exception e) when (!IsFatal(e))
            {
                StopPlayback();
                return Fail(StageLoc.Format(StageStr.RenderFailed, e.Message));
            }
        }

        RaiseStateChanged();
        return true;
    }

    /// <summary>
    /// Stops the job at once — no further GPU work is issued; the frame in progress is dropped, files already written
    /// stay — and the recording playback a sequence started.
    /// </summary>
    public void Cancel()
    {
        if (State != RenderJobState.Running)
            return;
        State = RenderJobState.Cancelled;
        Message = StageLoc.Format(StageStr.RenderCancelled, FrameIndex, FrameCount);
        MessageIsError = false;
        _accumulator = null;
        End();
        RaiseStateChanged();
    }

    /// <summary>
    /// Path tracer: stops sampling now and writes the current frame with the samples done so far (a sequence ends with
    /// it). Without samples to keep (or in Fast mode) this is <see cref="Cancel"/>. Returns true when a frame was written.
    /// </summary>
    public bool StopAndSave()
    {
        if (State != RenderJobState.Running || Mode != RenderMode.PathTraced || _final is not { SamplesDone: > 0 } final ||
            _request is not { } request || StageReplaced(_stage))
        {
            Cancel();
            return false;
        }

        try
        {
            final.Cancel();
            FinishFinalFrame(request, final, stopped: true);
            return State == RenderJobState.Completed;
        }
        catch (Exception e) when (!IsFatal(e))
        {
            Fail(StageLoc.Format(StageStr.RenderFailed, e.Message));
            return false;
        }
    }

    /// <summary>Errors that must not be turned into a failed job (the process cannot continue safely).</summary>
    private static bool IsFatal(Exception e) => e is OutOfMemoryException or StackOverflowException or AccessViolationException;

    /// <summary>
    /// True when <paramref name="engine"/> is not the stage the job started on any more: another engine, no engine, or
    /// the same engine with another scene — File → New, Open and recovery replace the scene inside the editor's one
    /// <see cref="StageEngine"/> (and reset its clock), so the engine reference alone does not tell.
    /// </summary>
    private bool StageReplaced(StageEngine? engine) => !ReferenceEquals(engine, _stage) || !ReferenceEquals(engine?.Scene, _scene);

    /// <summary>
    /// Renders for about <see cref="Budget"/> (at least one pass or path tracer dispatch) and writes the frames that are
    /// complete. Call once per editor frame, outside the GPU frame, with the stage the job was started with: another
    /// stage (a document opened, created or closed meanwhile, also when the engine replaced its scene) cancels the job.
    /// </summary>
    public void Step(StageEngine? engine, IRenderer renderer, double now)
    {
        _ = now;
        ArgumentNullException.ThrowIfNull(renderer);
        if (State != RenderJobState.Running || _request is not { } request)
        {
            ReleaseTargetWhenIdle();
            return;
        }

        if (StageReplaced(engine))
        {
            // Another stage was opened (or the stage was closed): the picture asked for no longer exists, and the path
            // tracer's light snapshot belongs to the old scene.
            Cancel();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            if (Mode == RenderMode.PathTraced && !renderer.Info.SupportsRayTracing && !FallBack(request))
                return;
            if (Mode == RenderMode.PathTraced)
                StepPathTraced(engine, renderer, request, start);
            else
                StepFast(engine, renderer, request, start);
        }
        catch (OutOfMemoryException e)
        {
            // An output too large for this computer (accumulation buffer, read-back): the job stops, the editor goes on.
            Fail(StageLoc.Format(StageStr.RenderFailed, e.Message));
        }
        catch (Exception e) when (!IsFatal(e))
        {
            // Any renderer or file error (Vulkan device errors included, a size the GPU cannot hold) ends the job with the
            // reason instead of escaping into the editor's frame loop, which would retry it — and fail — every frame.
            Fail(StageLoc.Format(StageStr.RenderFailed, e.Message));
            ReleaseTargetWhenIdle();
        }
    }

    // ------------------------------------------------------------------------------------------------ path tracer

    private void StepPathTraced(StageEngine? engine, IRenderer renderer, RenderRequest request, long start)
    {
        if (_final is null && !BeginFinalFrame(engine, renderer, request))
            return;
        var final = _final!;
        var remaining = Budget - Stopwatch.GetElapsedTime(start);
        final.Advance(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        SamplesDone = final.SamplesDone;
        if (final.IsComplete)
        {
            FinishFinalFrame(request, final, stopped: false);
            return;
        }

        // The first sample and then about once a second (less often when a progress image is expensive): each progress
        // image reads the whole image back.
        long now = Stopwatch.GetTimestamp();
        if (SamplesDone > 0 && (_lastPreview == 0 || Stopwatch.GetElapsedTime(_lastPreview, now) >= _previewWait))
        {
            SetPreview(final.Preview());
            _lastPreview = Stopwatch.GetTimestamp();
            var cost = Stopwatch.GetElapsedTime(now, _lastPreview);
            _previewWait = cost * PreviewCostFactor > PreviewInterval ? cost * PreviewCostFactor : PreviewInterval;
        }
    }

    /// <summary>
    /// Prepares the next path-traced frame — the next frame of a sequence played, the job's work light set, fixture
    /// models loaded — and starts the path tracer on it. False while it waits (or after falling back to Fast).
    /// </summary>
    private bool BeginFinalFrame(StageEngine? engine, IRenderer renderer, RenderRequest request)
    {
        if (!_frameReady)
        {
            if (request.Range is { } range && engine is not null)
                PlayFrame(engine, range);
            _frameReady = true;
            _waitStarted = Stopwatch.GetTimestamp();
        }

        ApplyWorkLight(engine, request);
        if (engine?.Render is { } sync && (sync.PendingModels > 0 || sync.PendingFixtureTypes > 0) &&
            Stopwatch.GetElapsedTime(_waitStarted) < MaxLoadWait)
        {
            // The light snapshot and the geometry must be complete: wait (without blocking the editor) for the fixture
            // models still loading in the background. The engine's clock is held, so nothing else updates the world.
            IsPreparing = true;
            sync.Update();
            return false;
        }

        IsPreparing = false;
        var settings = new FinalRenderSettings
        {
            Width = request.Width,
            Height = request.Height,
            SamplesPerPixel = SamplesTotal,
            MaxBounces = Math.Clamp(request.Final.MaxBounces, 0, VulkanFinalRender.MaxBounceLimit),
            MaxVolumeBounces = Math.Clamp(request.Final.MaxVolumeBounces, 0, VulkanFinalRender.MaxBounceLimit),
            Denoise = request.Final.Denoise,
            ToneMapper = request.Final.ToneMapper,
            Format = request.WriteExr ? ImageFileFormat.Exr : ImageFileFormat.Png,
            OutputPath = request.OutputPath,
            FrameRate = request.Final.FrameRate,
        };
        var o = request.FinalOptions;
        var options = new FinalRenderOptions
        {
            Seed = o.Seed,
            TimeLimit = o.TimeLimit,
            FireflyClamp = o.FireflyClamp,
            ExposureCompensation = o.ExposureCompensation,
            AutoExposure = _autoExposure,
            Metering = o.Metering,
            BackgroundLuminance = o.BackgroundLuminance,
            DenoiseIterations = o.DenoiseIterations,
        };
        try
        {
            _final = renderer.StartFinalRender(_camera!, settings, options);
        }
        catch (NotSupportedException) when (!renderer.Info.SupportsRayTracing)
        {
            FallBack(request);
            return false;
        }

        if (_final is VulkanFinalRender vulkan)
            vulkan.DispatchSeconds = DispatchTarget.TotalSeconds;
        SamplesTotal = _final.SamplesTotal;
        SamplesDone = 0;
        _lastPreview = 0;
        _previewWait = PreviewInterval;
        return true;
    }

    /// <summary>Writes the path tracer's frame (PNG, EXR when asked) and moves on to the next frame or completes the job.</summary>
    private void FinishFinalFrame(RenderRequest request, IFinalRender final, bool stopped)
    {
        string png = FramePath(request.OutputPath, FrameIndex, FrameCount);
        var image = final.Preview();
        EnsureFolder(png);
        ImageImporter.SavePng(image, png);
        LastOutput = png;
        if (request.WriteExr)
        {
            string exr = FramePath(ExrPath(request.OutputPath), FrameIndex, FrameCount);
            WriteExr(final, exr, request.Final.Denoise);
            LastExr = exr;
        }

        LastSamples = final.SamplesDone;
        SetPreview(image);
        if (_autoExposure && FrameCount > 1 && _camera is { } camera)
        {
            // The rest of the sequence keeps the first frame's exposure (no flicker between frames). The metered exposure
            // already includes the compensation, which the path tracer subtracts from the camera's EV100 again: add it
            // back so the following frames are not compensated twice.
            camera.Ev100 = Ev100Of(final.Exposure) + Compensation(request.FinalOptions);
            _autoExposure = false;
        }

        ReleaseFinal();
        FrameIndex++;
        SamplesDone = 0;
        _frameReady = false;
        if (stopped || FrameIndex >= FrameCount)
            Complete(request, stopped);
    }

    /// <summary>
    /// Writes a path-traced frame as OpenEXR (ZIP), like <c>candela render --final --exr</c>: the result as R, G, B, A
    /// (32-bit float, linear Rec.2020, scaled by the PNG's exposure; the header's <c>whiteLuminance</c> = 1 / exposure
    /// turns the values back into cd/m²), the raw result as the layer "raw" when denoised, and the denoiser's guides —
    /// "albedo" (half), "N" (world normal, half) and Z (view depth in metres, float) — when the renderer provides them.
    /// </summary>
    internal static void WriteExr(IFinalRender render, string path, bool denoised)
    {
        ArgumentNullException.ThrowIfNull(render);
        var result = render.ResultHdr();
        var channels = new List<ExrChannel>();
        if (denoised)
            channels.AddRange(OpenExr.SplitRgba(render.RawResultHdr(), "raw.", ExrPixelType.Float));
        if (render is VulkanFinalRender vulkan)
        {
            var (albedo, normalDepth) = vulkan.ReadGuides();
            channels.AddRange(OpenExr.SplitRgba(albedo, "albedo.", ExrPixelType.Half));
            channels.AddRange(OpenExr.SplitRgba(normalDepth, "N.", ExrPixelType.Half, names: "XYZ"));
            channels.Add(OpenExr.SplitRgba(normalDepth, "", ExrPixelType.Float, includeAlpha: true, names: "XYZZ").Last());
        }

        float exposure = render.Exposure > 0f && float.IsFinite(render.Exposure) ? render.Exposure : 1f;
        OpenExr.WriteRgba(path, result, ExrPixelType.Float, ExrCompression.Zip, channels, ExrColorimetry.Rec2020 with { WhiteLuminance = 1f / exposure });
    }

    /// <summary>
    /// Creates the folder of an output file again when it went away during the render (moved or deleted while the samples
    /// were taken), so the picture is not lost for a folder that <see cref="Start"/> had created.
    /// </summary>
    private static void EnsureFolder(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
    }

    /// <summary>The EV100 of an exposure factor (inverse of <c>Units.ExposureFromEv100</c>: exposure = 1 / (1.2 · 2^EV100)).</summary>
    internal static float Ev100Of(float exposure) =>
        exposure > 0f && float.IsFinite(exposure) ? MathF.Log2(1f / (1.2f * exposure)) : 0f;

    /// <summary>
    /// The exposure compensation the path tracer applies, stops (non-finite counts as 0, clamped to ±20 like
    /// <see cref="VulkanFinalRender"/>): its exposure is that of the camera's EV100 minus this.
    /// </summary>
    internal static float Compensation(FinalRenderOptions options) =>
        float.IsFinite(options.ExposureCompensation) ? Math.Clamp(options.ExposureCompensation, -20f, 20f) : 0f;

    /// <summary>
    /// Switches a path-traced job to the Fast renderer (the GPU has no ray queries). False (the job failed) when the
    /// size does not fit the Fast renderer's target.
    /// </summary>
    private bool FallBack(RenderRequest request)
    {
        ReleaseFinal();
        IsPreparing = false;
        Mode = RenderMode.Fast;
        FellBack = true;
        PassIndex = 0;
        if (FitsFastTarget(request))
            return true;
        Fail(StageLoc.Format(StageStr.RenderBadSize, request.Width, request.Height, MaxTargetSide));
        return false;
    }

    private static bool FitsFastTarget(RenderRequest request) =>
        request.Supersample >= 1 && request.Passes >= 1 &&
        request.Width * request.Supersample <= MaxTargetSide && request.Height * request.Supersample <= MaxTargetSide;

    private void ReleaseFinal()
    {
        var final = _final;
        _final = null;
        if (final is null)
            return;
        final.Cancel();
        final.Dispose();
    }

    // ------------------------------------------------------------------------------------------------ fast

    private void StepFast(StageEngine? engine, IRenderer renderer, RenderRequest request, long start)
    {
        EnsureTarget(renderer, request);
        ApplyWorkLight(engine, request);
        do
        {
            if (PassIndex == 0 && request.Range is { } range && engine is not null)
                PlayFrame(engine, range);

            float delta = PassIndex == 0 ? 0f : 1f / 60f;
            renderer.Render(_target!, request.Camera, request.View, request.Settings, delta);
            PassIndex++;
            if (PassIndex > request.Passes - Math.Min(AveragedPasses, request.Passes))
                Accumulate(renderer.ReadPixels(_target!), request);
            if (PassIndex >= request.Passes)
            {
                WriteFrame(request);
                FrameIndex++;
                PassIndex = 0;
                if (FrameIndex >= FrameCount)
                {
                    Complete(request, stopped: false);
                    return;
                }
            }
        }
        while (Stopwatch.GetElapsedTime(start) < Budget);
    }

    /// <summary>
    /// Brings a sequence's stage to output frame <see cref="FrameIndex"/>: a following frame is exactly one frame interval
    /// of the recording and of the engine clock later; the first frame is the range's start, played again when the engine
    /// was ticked after <see cref="Start"/> (the editor ticks the stage once more in the frame after a render starts from
    /// its UI, before it sees the clock held — by up to a second when that frame was slow).
    /// </summary>
    private void PlayFrame(StageEngine engine, RenderRange range)
    {
        if (FrameIndex > 0)
        {
            double dt = 1.0 / range.FrameRate;
            engine.Player?.Seek(range.TimeOf(FrameIndex) - dt, engine.Time);
            engine.Tick(dt);
        }
        else if (engine.Time != _rangeEngineTime)
        {
            engine.Player?.Seek(range.StartSeconds, engine.Time);
            engine.Tick(0);
            _rangeEngineTime = engine.Time;
        }
    }

    /// <summary>
    /// Gives the render binding the job's work light before a pass is rendered (or the path tracer takes its light
    /// snapshot): the light list the editor's last tick sent may hold the preview's work light, which a final picture
    /// must not show unless it was asked for.
    /// </summary>
    private static void ApplyWorkLight(StageEngine? engine, RenderRequest request)
    {
        if (engine?.Render is not { } sync || sync.WorkLight == request.WorkLight)
            return;
        sync.WorkLight = request.WorkLight;
        sync.Update();
    }

    private void EnsureTarget(IRenderer renderer, RenderRequest request)
    {
        int w = request.Width * request.Supersample, h = request.Height * request.Supersample;
        if (_target is not null && ReferenceEquals(_targetOwner, renderer))
        {
            if (_target.Width != w || _target.Height != h)
                _target.Resize(w, h);
            return;
        }

        Release(_target);
        _target = renderer.CreateTarget(w, h);
        _targetOwner = renderer;
    }

    private void Accumulate(ImageData image, RenderRequest request)
    {
        int ss = request.Supersample, w = request.Width, h = request.Height;
        if (image.Width != w * ss || image.Height != h * ss || image.Pixels.Length < image.Width * image.Height * 4)
            throw new InvalidOperationException(StageLoc.Get(StageStr.RenderReadbackSize));
        var acc = _accumulator ??= new float[w * h * 3];
        if (_accumulated == 0)
            Array.Clear(acc);
        var px = image.Pixels;
        int stride = image.Width * 4;
        float norm = 1f / (ss * ss);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    int row = (y * ss + sy) * stride;
                    for (int sx = 0; sx < ss; sx++)
                    {
                        int i = row + (x * ss + sx) * 4;
                        r += SrgbToLinear[px[i]];
                        g += SrgbToLinear[px[i + 1]];
                        b += SrgbToLinear[px[i + 2]];
                    }
                }

                int o = (y * w + x) * 3;
                acc[o] += r * norm;
                acc[o + 1] += g * norm;
                acc[o + 2] += b * norm;
            }
        }

        _accumulated++;
    }

    private void WriteFrame(RenderRequest request)
    {
        var acc = _accumulator ?? throw new InvalidOperationException(StageLoc.Get(StageStr.RenderReadbackSize));
        int w = request.Width, h = request.Height;
        var pixels = new byte[w * h * 4];
        float inv = 1f / Math.Max(1, _accumulated);
        for (int i = 0, o = 0; i < acc.Length; i += 3, o += 4)
        {
            pixels[o] = Encode(acc[i] * inv);
            pixels[o + 1] = Encode(acc[i + 1] * inv);
            pixels[o + 2] = Encode(acc[i + 2] * inv);
            pixels[o + 3] = 255;
        }

        string path = FramePath(request.OutputPath, FrameIndex, FrameCount);
        var image = new ImageData { Width = w, Height = h, Pixels = pixels };
        EnsureFolder(path);
        ImageImporter.SavePng(image, path);
        LastOutput = path;
        SetPreview(image);
        _accumulated = 0;
    }

    // ------------------------------------------------------------------------------------------------ common

    private void Complete(RenderRequest request, bool stopped)
    {
        State = RenderJobState.Completed;
        MessageIsError = false;
        string written = LastOutput ?? request.OutputPath;
        string message = stopped
            ? StageLoc.Format(StageStr.RenderStopped, LastSamples, written)
            : FrameCount > 1
                ? StageLoc.Format(StageStr.RenderSequenceDone, FrameCount, Path.GetDirectoryName(written) ?? "")
                : LastExr is { } exr
                    ? StageLoc.Format(StageStr.RenderFrameDoneExr, written, exr)
                    : StageLoc.Format(StageStr.RenderFrameDone, written);
        Message = FellBack ? message + " " + StageLoc.Get(StageStr.RenderFellBack) : message;
        _accumulator = null;
        End();
        RaiseStateChanged();
    }

    private bool Fail(string message)
    {
        State = RenderJobState.Failed;
        Message = message;
        MessageIsError = true;
        _accumulator = null;
        End();
        RaiseStateChanged();
        return false;
    }

    /// <summary>Releases what only a running job needs: the path tracer's render and the recording playback.</summary>
    private void End()
    {
        IsPreparing = false;
        _stage = null;
        _scene = null;
        _endedAt = _startedAt != 0 ? Stopwatch.GetTimestamp() : 0;
        try
        {
            ReleaseFinal();
        }
        finally
        {
            StopPlayback();
        }
    }

    /// <summary>Stops the playback of the recording the running sequence plays (the engine returns to live input).</summary>
    private void StopPlayback()
    {
        var engine = _playing;
        _playing = null;
        if (engine is null)
            return;
        try
        {
            engine.Play(null);
        }
        catch (ObjectDisposedException)
        {
            // The stage was closed meanwhile: nothing plays any more.
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this);

    /// <summary>
    /// Shows <paramref name="image"/> (RGBA8) as the job's picture, scaled down to <see cref="PreviewMaxSide"/> by an
    /// integer box filter; null clears it.
    /// </summary>
    private void SetPreview(ImageData? image)
    {
        PreviewImage = image is null ? null : Downscale(image, PreviewMaxSide);
        PreviewVersion++;
    }

    /// <summary>
    /// <paramref name="image"/> (RGBA8) reduced by the smallest integer factor that brings both sides to at most
    /// <paramref name="maxSide"/> (averaging each block); the image itself when it already fits.
    /// </summary>
    internal static ImageData Downscale(ImageData image, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        if (image.Format != PixelFormat.Rgba8 || w <= 0 || h <= 0 || image.Pixels.Length < (long)w * h * 4)
            return image;
        int f = Math.Max((w + maxSide - 1) / Math.Max(maxSide, 1), (h + maxSide - 1) / Math.Max(maxSide, 1));
        if (f <= 1)
            return image;
        int ow = Math.Max(1, w / f), oh = Math.Max(1, h / f);
        var source = image.Pixels;
        var pixels = new byte[ow * oh * 4];
        int area = f * f;
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < f; sy++)
                {
                    int row = ((y * f + sy) * w + x * f) * 4;
                    for (int sx = 0; sx < f; sx++)
                    {
                        int i = row + sx * 4;
                        r += source[i];
                        g += source[i + 1];
                        b += source[i + 2];
                        a += source[i + 3];
                    }
                }

                int o = (y * ow + x) * 4;
                pixels[o] = (byte)(r / area);
                pixels[o + 1] = (byte)(g / area);
                pixels[o + 2] = (byte)(b / area);
                pixels[o + 3] = (byte)(a / area);
            }
        }

        return new ImageData { Width = ow, Height = oh, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    private static RenderCamera Copy(RenderCamera camera) => new()
    {
        View = camera.View,
        Projection = camera.Projection,
        FovY = camera.FovY,
        OrthoHeight = camera.OrthoHeight,
        Near = camera.Near,
        Far = camera.Far,
        Ev100 = camera.Ev100,
    };

    private void ReleaseTargetWhenIdle()
    {
        if (_target is null)
            return;
        Release(_target);
        _target = null;
        _targetOwner = null;
    }

    private void Release(IDisposable? target)
    {
        if (target is null)
            return;
        if (DisposeTarget is { } defer)
            defer(target);
        else
            target.Dispose();
    }

    private static byte Encode(float linear)
    {
        float v = float.IsFinite(linear) ? Math.Clamp(linear, 0f, 1f) : 0f;
        float s = v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255f), 0, 255);
    }

    private static float[] BuildSrgbTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        return table;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (State == RenderJobState.Running)
            State = RenderJobState.Cancelled;
        try
        {
            ReleaseFinal();
        }
        finally
        {
            StopPlayback();
            Release(_target);
            _target = null;
        }
    }
}
