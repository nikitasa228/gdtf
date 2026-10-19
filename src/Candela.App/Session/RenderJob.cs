using System.Diagnostics;
using System.Globalization;
using Candela.App.Ui;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Engine;
using Candela.Rendering;

namespace Candela.App.Session;

/// <summary>State of the final render job.</summary>
internal enum RenderJobState
{
    /// <summary>Nothing has been rendered yet.</summary>
    Idle,

    /// <summary>Rendering.</summary>
    Running,

    /// <summary>All frames were written.</summary>
    Completed,

    /// <summary>Stopped by an error (see the message).</summary>
    Failed,

    /// <summary>Stopped by the user.</summary>
    Cancelled,
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
/// <param name="Camera">Camera of the view (copied).</param>
/// <param name="View">View options (full shading, no overlay; copied).</param>
/// <param name="Settings">Real-time quality used for the render (high quality; copied).</param>
/// <param name="Width">Output width, px.</param>
/// <param name="Height">Output height, px.</param>
/// <param name="Supersample">Rendered at this many times the output resolution per axis, then box-filtered down.</param>
/// <param name="Passes">Frames rendered per output image (temporal accumulation of volumetrics and shadows).</param>
/// <param name="OutputPath">PNG file (a sequence numbers it: name_0001.png …).</param>
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
}

/// <summary>
/// The final render: until the path tracer exists (rendering stage 3), the real-time renderer renders the frame at
/// high quality — every shadow, 256 volumetric slices, ray-traced indirect light when the GPU supports it —
/// supersampled (2 × 2 above 4 samples per pixel) and accumulated over several frames, then box-filtered in linear light
/// to the output size and written as PNG. A timecode range plays the DMX recording through the engine frame by frame
/// (the engine's clock is held by the job meanwhile, see <see cref="HoldsEngineClock"/>) and writes an image
/// sequence. Runs a little each editor frame so the UI stays responsive. Main thread only.
/// </summary>
internal sealed class RenderJobRunner : IDisposable
{
    /// <summary>Largest render target side, px (supersampling is reduced to stay below it).</summary>
    public const int MaxTargetSide = 8192;

    /// <summary>Most frames accumulated per output image.</summary>
    public const int MaxPasses = 256;

    /// <summary>Last frames of the accumulation that are averaged into the output.</summary>
    public const int AveragedPasses = 4;

    /// <summary>Seconds of the recording played before the first frame of a range, so moving heads reach their positions.</summary>
    public const double Preroll = 2.0;

    /// <summary>Time spent rendering per editor frame (at least one render pass runs).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(60);

    private static readonly float[] SrgbToLinear = BuildSrgbTable();

    private RenderRequest? _request;
    private StageEngine? _playing;
    private IRenderTarget? _target;
    private IRenderer? _targetOwner;
    private float[]? _accumulator;
    private int _accumulated;
    private bool _disposed;

    /// <summary>Current state.</summary>
    public RenderJobState State { get; private set; }

    /// <summary>
    /// Releases the render target (the editor defers it until the GPU frames that used it are done); null disposes it
    /// at once (tests, tools with their own renderer).
    /// </summary>
    public Action<IDisposable>? DisposeTarget { get; set; }

    /// <summary>Output frame being rendered (0-based) and the number of frames.</summary>
    public int FrameIndex { get; private set; }

    /// <summary>Frames of the job.</summary>
    public int FrameCount { get; private set; }

    /// <summary>Render pass of the current frame and passes per frame.</summary>
    public int PassIndex { get; private set; }

    /// <summary>Passes per output frame.</summary>
    public int Passes => _request?.Passes ?? 0;

    /// <summary>Progress 0 … 1.</summary>
    public double Progress => FrameCount == 0 || Passes == 0 ? 0 : (FrameIndex + PassIndex / (double)Passes) / FrameCount;

    /// <summary>The last file written, or null.</summary>
    public string? LastOutput { get; private set; }

    /// <summary>A one-line result or error, or null.</summary>
    public string? Message { get; private set; }

    /// <summary>True when <see cref="Message"/> describes a failure.</summary>
    public bool MessageIsError { get; private set; }

    /// <summary>
    /// True while a sequence is rendered: the job ticks the engine itself at the sequence's frame rate, so the editor
    /// must not tick it with wall-clock time meanwhile (<c>EditorSession.EngineClockHeld</c>).
    /// </summary>
    public bool HoldsEngineClock => State == RenderJobState.Running && _request?.Range is not null;

    /// <summary>
    /// The work light the running job renders with (it decides alone while it runs: the viewports' setting must not leak
    /// into a final picture), or null while no job runs.
    /// </summary>
    public WorkLightMode? WorkLightInUse => State == RenderJobState.Running && _request is { } request ? request.WorkLight : null;

    /// <summary>
    /// Supersampling and accumulation for a requested sample count: 2 × 2 supersampling from 4 samples per pixel (when
    /// the target stays within <see cref="MaxTargetSide"/>), the rest as accumulated frames (at most <see cref="MaxPasses"/>).
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
        int digits = Math.Max(4, (frameCount).ToString(CultureInfo.InvariantCulture).Length);
        return Path.Combine(dir, name + "_" + (index + 1).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + ext);
    }

    /// <summary>Starts a render (a running one is cancelled first). Returns false with a message when the request cannot run.</summary>
    public bool Start(RenderRequest request, StageEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        if (request.Width < 16 || request.Height < 16 || request.Width * request.Supersample > MaxTargetSide ||
            request.Height * request.Supersample > MaxTargetSide)
            return Fail(StageLoc.Format(StageStr.RenderBadSize, request.Width, request.Height));
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
        FrameCount = request.Range?.FrameCount ?? 1;
        FrameIndex = 0;
        PassIndex = 0;
        _accumulated = 0;
        _accumulator = null;
        LastOutput = null;
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
            }
            catch (Exception e) when (!IsFatal(e))
            {
                StopPlayback();
                return Fail(StageLoc.Format(StageStr.RenderFailed, e.Message));
            }
        }

        return true;
    }

    /// <summary>Stops the job (files already written stay) and the recording playback a sequence started.</summary>
    public void Cancel()
    {
        if (State != RenderJobState.Running)
            return;
        State = RenderJobState.Cancelled;
        Message = StageLoc.Format(StageStr.RenderCancelled, FrameIndex, FrameCount);
        MessageIsError = false;
        _accumulator = null;
        StopPlayback();
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

    /// <summary>Errors that must not be turned into a failed job (the process cannot continue safely).</summary>
    private static bool IsFatal(Exception e) => e is OutOfMemoryException or StackOverflowException or AccessViolationException;

    /// <summary>Renders for about <see cref="Budget"/> (at least one pass) and writes the frames that are complete.</summary>
    public void Step(StageEngine? engine, IRenderer renderer, double now)
    {
        _ = now;
        ArgumentNullException.ThrowIfNull(renderer);
        if (State != RenderJobState.Running || _request is not { } request)
        {
            ReleaseTargetWhenIdle();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            EnsureTarget(renderer, request);
            ApplyWorkLight(engine, request);
            do
            {
                if (PassIndex == 0 && FrameIndex > 0 && request.Range is { } range && engine is not null)
                {
                    // Next frame of the sequence: exactly one frame interval of the recording and of the engine clock.
                    double dt = 1.0 / range.FrameRate;
                    engine.Player?.Seek(range.TimeOf(FrameIndex) - dt, engine.Time);
                    engine.Tick(dt);
                }

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
                        Complete(request);
                        return;
                    }
                }
            }
            while (Stopwatch.GetElapsedTime(start) < Budget);
        }
        catch (OutOfMemoryException e)
        {
            // An output too large for this computer (accumulation buffer, read-back): the job stops, the editor goes on.
            Fail(StageLoc.Format(StageStr.RenderFailed, e.Message));
            StopPlayback();
        }
        catch (Exception e) when (!IsFatal(e))
        {
            // Any renderer or file error (Vulkan device errors included) ends the job with the reason instead of
            // escaping into the editor's frame loop, which would retry it — and fail — every frame.
            Fail(StageLoc.Format(StageStr.RenderFailed, e.Message));
            StopPlayback();
            ReleaseTargetWhenIdle();
        }
    }

    /// <summary>
    /// Gives the render binding the job's work light before a pass is rendered: the light list the editor's last tick
    /// sent may hold the preview's work light, which a final picture must not show unless it was asked for.
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
        ImageImporter.SavePng(new ImageData { Width = w, Height = h, Pixels = pixels }, path);
        LastOutput = path;
        _accumulated = 0;
    }

    private void Complete(RenderRequest request)
    {
        State = RenderJobState.Completed;
        MessageIsError = false;
        Message = FrameCount > 1
            ? StageLoc.Format(StageStr.RenderSequenceDone, FrameCount, Path.GetDirectoryName(LastOutput ?? request.OutputPath) ?? "")
            : StageLoc.Format(StageStr.RenderFrameDone, LastOutput ?? request.OutputPath);
        _accumulator = null;
        StopPlayback();
    }

    private bool Fail(string message)
    {
        State = RenderJobState.Failed;
        Message = message;
        MessageIsError = true;
        _accumulator = null;
        return false;
    }

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
        {
            State = RenderJobState.Cancelled;
            StopPlayback();
        }

        Release(_target);
        _target = null;
    }
}
