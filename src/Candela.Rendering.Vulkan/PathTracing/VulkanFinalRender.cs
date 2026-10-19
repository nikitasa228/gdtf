using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// A progressive final render of the path tracer (<see cref="VulkanRenderer.StartFinalRender(RenderCamera, FinalRenderSettings, FinalRenderOptions)"/>):
/// accumulates one sample per pixel per pass in RGBA32F images (radiance in cd/m² with per-pixel sample counts, and
/// the first surface's albedo, normal and depth for the denoiser) until the samples or the time budget are spent.
/// <see cref="Advance(TimeSpan, CancellationToken)"/> renders in submissions of whole or partial passes sized from
/// measured GPU times (a dispatch stays below <see cref="MaxDispatchSeconds"/>), so the caller's frame loop stays
/// responsive. The camera, the settings and the lights are fixed when the render starts; geometry, materials and
/// media are read from the world as it is when the samples are taken. Main thread only.
/// </summary>
public sealed unsafe class VulkanFinalRender : IFinalRender
{
    /// <summary>Longest a single dispatch is planned to take, s (drivers reset GPUs that are stuck for about 2 s).</summary>
    public const double MaxDispatchSeconds = 0.25;

    /// <summary>Most samples per pixel.</summary>
    public const int MaxSamples = 65536;

    /// <summary>Most surface or volume bounces.</summary>
    public const int MaxBounceLimit = 64;

    /// <summary>Shadow rays stop this far before the lens (the fixture's own lens and housing never shadow its light), m.</summary>
    public const float LensClearance = ShadowAtlas.NearPlane;

    private const double MaxSubmitSeconds = 0.5;
    private const int InitialPixelsPerDispatch = 1 << 18;

    private readonly VulkanRenderer _renderer;
    private readonly VulkanContext _context;
    private readonly VulkanRenderWorld _world;
    private readonly PathTracer _tracer;
    private readonly RayTracingScene _scene;
    private readonly BarrierBatch _barriers;
    private readonly DescriptorWriter _writer = new();
    private readonly DescriptorSignature _signature = new();
    private readonly TopLevelStructure _tlas;
    private readonly GpuLight[] _lights;
    private readonly int _lightCount;
    private readonly int _width;
    private readonly int _height;
    private readonly int _samplesTotal;
    private readonly bool _denoise;
    private readonly int _denoiseIterations;
    private readonly uint _toneMode;
    private readonly bool _autoExposure;
    private readonly ExposureMetering _metering;
    private readonly float _compensation;
    private readonly float _cameraExposure;
    private readonly TimeSpan? _timeLimit;
    private readonly Vector3 _mediaMin;
    private readonly Vector3 _mediaMax;

    private readonly GpuImage _accum;
    private readonly GpuImage _aux;
    private readonly GpuImage _guide;
    private readonly GpuImage _ping;
    private readonly GpuImage _pong;
    private readonly GpuImage _display;
    private readonly GpuBuffer _lightBuffer;
    private readonly GpuBuffer _paramsBuffer;
    private readonly GpuBuffer _frameBuffer;
    private readonly GpuBuffer _histogram;
    private GpuBuffer? _volumeBuffer;
    private GpuBuffer? _lensBuffer;
    private BottomLevelStructure? _volumeBlas;
    private BottomLevelStructure? _lensBlas;
    private GpuBuffer? _readback;
    private TlasInstance[] _instances = new TlasInstance[64];

    private GpuFrameData _frameData;
    private GpuPtParams _params;
    private DescriptorPool _pool;
    private DescriptorSet _viewSet;
    private DescriptorSet _traceSet;
    private DescriptorSet _resolveSet;
    private DescriptorSet _pingToPong;
    private DescriptorSet _pongToPing;
    private DescriptorSet _displayAccum;
    private DescriptorSet _displayPing;
    private DescriptorSet _displayPong;

    private bool _setUp;
    private ulong _tlasVersion;
    private int _sample;
    private int _row;
    private int _samplesDone;
    private int _rowsPerDispatch;
    private double _secondsPerRow;
    private bool _complete;
    private bool _cancelled;
    private bool _disposed;
    private TimeSpan _elapsed;
    private float _exposure;
    private long _work;
    private long _denoisedWork = -1;
    private GpuImage? _denoised;

    internal VulkanFinalRender(VulkanRenderer renderer, PathTracer tracer, RayTracingScene scene, RenderCamera camera, FinalRenderSettings settings,
        FinalRenderOptions options)
    {
        _renderer = renderer;
        _context = renderer.Context;
        _world = renderer.VulkanWorld;
        _tracer = tracer;
        _scene = scene;
        _barriers = new BarrierBatch(_context.Vk);
        _tlas = new TopLevelStructure(scene.Device, "path tracer TLAS");

        int maxSide = (int)Math.Min(_context.Capabilities.MaxImageDimension2D, 16384u);
        _width = Math.Clamp(settings.Width, 1, maxSide);
        _height = Math.Clamp(settings.Height, 1, maxSide);
        _samplesTotal = Math.Clamp(settings.SamplesPerPixel, 1, MaxSamples);
        _denoise = settings.Denoise;
        _denoiseIterations = Math.Clamp(options.DenoiseIterations, 1, 8);
        _toneMode = settings.ToneMapper switch
        {
            ToneMapper.Aces => ShaderConstants.ToneAces,
            ToneMapper.Neutral => ShaderConstants.ToneNeutral,
            _ => ShaderConstants.ToneAgX,
        };
        _autoExposure = options.AutoExposure;
        _metering = options.Metering is ExposureMetering.Average or ExposureMetering.Stage ? options.Metering : ExposureMetering.Stage;
        _compensation = float.IsFinite(options.ExposureCompensation) ? Math.Clamp(options.ExposureCompensation, -20f, 20f) : 0f;
        float ev = (float.IsFinite(camera.Ev100) ? camera.Ev100 : 6f) - _compensation;
        _cameraExposure = Units.ExposureFromEv100(ev);
        _exposure = _cameraExposure;
        _timeLimit = options.TimeLimit is { } limit && limit > TimeSpan.Zero ? limit : null;

        // The lights are fixed for the whole render (their indices address the light geometry).
        _lights = _world.Lights.ToArray();
        _lightCount = _lights.Length;
        var cam = CameraFrame.Compute(camera, _width / (float)_height);
        (_mediaMin, _mediaMax) = PathTracerScene.MediaBox(_world, _lights, cam.Eye);
        _frameData = new GpuFrameData
        {
            View = cam.View,
            Projection = cam.Projection,
            ViewProjection = cam.ViewProjection,
            InverseView = cam.InverseView,
            InverseProjection = cam.InverseProjection,
            InverseViewProjection = cam.InverseViewProjection,
            PreviousViewProjection = cam.ViewProjection,
            CameraPosition = new Vector4(cam.Eye, cam.Orthographic ? 1f : 0f),
            CameraForward = new Vector4(cam.Forward, 0f),
            RenderSize = new Vector4(_width, _height, 1f / _width, 1f / _height),
            DisplaySize = new Vector4(_width, _height, 1f / _width, 1f / _height),
            Exposure = 1f,
            NearPlane = cam.Near,
            FarPlane = cam.Far,
            Flags = cam.Orthographic ? ShaderConstants.FrameOrthographic : 0u,
            FlameLut = new Vector4(AtmosphereResources.FlameLutMinKelvin, 1f / AtmosphereResources.FlameLutStep, AtmosphereResources.FlameLutEntries,
                AtmosphereResources.FlameLutMaxKelvin),
            RenderScale = 1f,
        };
        float background = float.IsFinite(options.BackgroundLuminance) ? Math.Clamp(options.BackgroundLuminance, 0f, 1e9f) : 0f;
        float clamp = float.IsFinite(options.FireflyClamp) ? Math.Clamp(options.FireflyClamp, 0f, FinalRenderOptions.MaxFireflyClamp) : 0f;
        _params = new GpuPtParams
        {
            MediaMin = new Vector4(_mediaMin, 0f),
            MediaMax = new Vector4(_mediaMax, 0f),
            Background = new Vector4(background, background, background, clamp),
            Geometry = new Vector4(LensClearance, PathTracerScene.LensOffset, PathTracerScene.PolygonRadiusFactor, 0f),
            LightCount = (uint)_lightCount,
            MaxBounces = (uint)Math.Clamp(settings.MaxBounces, 0, MaxBounceLimit),
            MaxVolumeBounces = (uint)Math.Clamp(settings.MaxVolumeBounces, 0, MaxBounceLimit),
            Seed = options.Seed,
            Width = (uint)_width,
            Height = (uint)_height,
        };
        // bands of whole 8-row workgroups (the last band of a pass may be shorter)
        _rowsPerDispatch = Math.Clamp(InitialPixelsPerDispatch / _width / 8 * 8, 8, _height);

        try
        {
            var storage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
            uint w = (uint)_width, h = (uint)_height;
            _accum = GpuImage.Create2D(_context, w, h, Format.R32G32B32A32Sfloat, storage, "final render accumulation", dedicated: true);
            _aux = GpuImage.Create2D(_context, w, h, Format.R32G32B32A32Sfloat, storage, "final render albedo", dedicated: true);
            _guide = GpuImage.Create2D(_context, w, h, Format.R32G32B32A32Sfloat, storage, "final render normal and depth", dedicated: true);
            _ping = GpuImage.Create2D(_context, w, h, Format.R32G32B32A32Sfloat, storage, "final render denoise ping", dedicated: true);
            _pong = GpuImage.Create2D(_context, w, h, Format.R32G32B32A32Sfloat, storage, "final render denoise pong", dedicated: true);
            _display = GpuImage.Create2D(_context, w, h, Format.R8G8B8A8Unorm, storage, "final render display", dedicated: true);
            ulong lightBytes = (ulong)Math.Max(_lightCount, 1) * (ulong)sizeof(GpuLight);
            _lightBuffer = GpuBuffer.Create(_context, lightBytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUsage.GpuOnly,
                "final render lights");
            _paramsBuffer = GpuBuffer.Create(_context, (ulong)sizeof(GpuPtParams), BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.GpuOnly, "final render parameters");
            _frameBuffer = GpuBuffer.Create(_context, (ulong)sizeof(GpuFrameData), BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.GpuOnly, "final render frame constants");
            _histogram = GpuBuffer.Create(_context, ShaderConstants.HistogramBins * sizeof(uint),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUsage.Readback, "final render histogram");
        }
        catch
        {
            ReleaseResources(deferred: false);
            throw;
        }

        // Upload the light snapshot, build its structures and clear the images now, so every result is defined.
        bool ownFrame = _context.CurrentFrame is null;
        var frame = ownFrame ? _context.BeginFrame() : _context.CurrentFrame!;
        try
        {
            _barriers.CommandBuffer = frame.CommandBuffer;
            SetUp(frame.CommandBuffer, _barriers);
        }
        finally
        {
            if (ownFrame)
                _context.SubmitFrame(frame);
        }
    }

    /// <inheritdoc/>
    public int SamplesDone => _samplesDone;

    /// <inheritdoc/>
    public int SamplesTotal => _samplesTotal;

    /// <inheritdoc/>
    public bool IsComplete => _complete;

    /// <inheritdoc/>
    public bool IsCancelled => _cancelled;

    /// <inheritdoc/>
    public TimeSpan Elapsed => _elapsed;

    /// <inheritdoc/>
    public float Exposure => _exposure;

    /// <summary>Output width, px.</summary>
    public int Width => _width;

    /// <summary>Output height, px.</summary>
    public int Height => _height;

    /// <summary>Spot lights the render samples (the world's lights when it started).</summary>
    public int LightCount => _lightCount;

    /// <summary>The box the haze fills (the scene's bounds grown by <see cref="PathTracerScene.BeamReach"/>), m.</summary>
    public (Vector3 Min, Vector3 Max) MediaBounds => (_mediaMin, _mediaMax);

    /// <summary>
    /// Most rows per dispatch (a multiple of 8, at least 8): tests split small images into several bands per pass, as
    /// large images are split by their measured cost.
    /// </summary>
    internal int MaxRowsPerDispatch { get; set; } = int.MaxValue;

    /// <inheritdoc/>
    public void Advance(TimeSpan budget) => Advance(budget, CancellationToken.None);

    /// <inheritdoc/>
    public void Advance(TimeSpan budget, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_complete)
            return;
        var watch = Stopwatch.StartNew();
        double budgetSeconds = budget > TimeSpan.Zero ? budget.TotalSeconds : 0.0;
        try
        {
            while (!_complete)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancel();
                    break;
                }

                if (_timeLimit is { } limit && _row == 0 && _samplesDone > 0 && _elapsed + watch.Elapsed >= limit)
                {
                    _complete = true;
                    break;
                }

                if (_context.CurrentFrame is { } open)
                {
                    // The caller is recording a frame: add one dispatch to it (no waiting inside someone else's frame).
                    RecordWork(open, 1);
                    break;
                }

                int dispatches = PlanDispatches(Math.Max(budgetSeconds - watch.Elapsed.TotalSeconds, 0.0));
                var frame = _context.BeginFrame();
                long start = Stopwatch.GetTimestamp();
                int rows;
                try
                {
                    rows = RecordWork(frame, dispatches);
                }
                finally
                {
                    _context.SubmitFrame(frame);
                }

                _context.WaitForSubmittedFrames();
                Measure(Stopwatch.GetElapsedTime(start).TotalSeconds, rows);
                if (watch.Elapsed.TotalSeconds >= budgetSeconds)
                    break;
            }
        }
        finally
        {
            _elapsed += watch.Elapsed;
        }
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        if (_complete)
            return;
        _cancelled = true;
        _complete = true;
    }

    /// <inheritdoc/>
    public ImageData Preview()
    {
        ThrowIfDisposed();
        var (source, mean) = PrepareResult(_denoise);
        UpdateExposure(source, mean);
        var cmd = _context.Immediate.Begin();
        var b = _barriers;
        b.CommandBuffer = cmd;
        DeclareDisplayReads(b, source);
        b.Use(_display, ImageUse.StorageCompute, discard: true);
        b.Flush();
        var vk = _context.Vk;
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _tracer.Display);
        BindPassSet(cmd, DisplaySetFor(source));
        _tracer.Push(cmd, new GpuDrawConstants { InstanceIndex = mean ? ShaderConstants.PtDisplayMean : 0u, Mode = _toneMode, Width = _exposure });
        vk.CmdDispatch(cmd, ((uint)_width + 7) / 8, ((uint)_height + 7) / 8, 1);
        ulong size = (ulong)_width * (ulong)_height * 4;
        var readback = Readback(size);
        b.Use(_display, ImageUse.TransferSource);
        b.Use(readback, BufferUse.TransferDestination);
        b.Flush();
        CopyImage(cmd, _display, readback);
        b.Use(readback, BufferUse.HostRead);
        b.Flush();
        _context.Immediate.SubmitAndWait();
        _context.Allocator.Invalidate(readback.Allocation, 0, size);
        var pixels = new ReadOnlySpan<byte>(readback.MappedPointer, checked((int)size)).ToArray();
        return new ImageData { Width = _width, Height = _height, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <inheritdoc/>
    public ImageData ResultHdr()
    {
        ThrowIfDisposed();
        var (source, mean) = PrepareResult(_denoise);
        UpdateExposure(source, mean);
        return ReadHdr(source, mean, _exposure);
    }

    /// <inheritdoc/>
    public ImageData RawResultHdr()
    {
        ThrowIfDisposed();
        var (source, mean) = PrepareResult(false);
        if (!_denoise)
            UpdateExposure(source, mean);
        return ReadHdr(source, mean, _exposure);
    }

    /// <summary>
    /// The denoised linear HDR result (RGBA float, cd/m² × <see cref="Exposure"/>) whether or not the settings ask for
    /// denoising; the raw result is <see cref="RawResultHdr"/>.
    /// </summary>
    public ImageData DenoisedResultHdr()
    {
        ThrowIfDisposed();
        var (source, mean) = PrepareResult(true);
        if (_denoise)
            UpdateExposure(source, mean);
        return ReadHdr(source, mean, _exposure);
    }

    /// <summary>
    /// The auxiliary images the denoiser is guided by, averaged over the samples: albedo (RGB, linear Rec.2020, A = 1)
    /// and the first surface's world normal (RGB) with its view depth in metres (A; 10⁵ where the camera sees no surface).
    /// </summary>
    public (ImageData Albedo, ImageData NormalDepth) ReadGuides()
    {
        ThrowIfDisposed();
        var accum = ReadFloats(_accum);
        var aux = ReadFloats(_aux);
        var guide = ReadFloats(_guide);
        int pixels = _width * _height;
        var albedo = new float[pixels * 4];
        var normal = new float[pixels * 4];
        for (int i = 0; i < pixels; i++)
        {
            float n = MathF.Max(accum[4 * i + 3], 1f);
            albedo[4 * i] = Finite(aux[4 * i] / n);
            albedo[4 * i + 1] = Finite(aux[4 * i + 1] / n);
            albedo[4 * i + 2] = Finite(aux[4 * i + 2] / n);
            albedo[4 * i + 3] = 1f;
            var v = new Vector3(guide[4 * i], guide[4 * i + 1], guide[4 * i + 2]) / n;
            float length = v.Length();
            v = length > 1e-6f && float.IsFinite(length) ? v / length : Vector3.Zero;
            normal[4 * i] = v.X;
            normal[4 * i + 1] = v.Y;
            normal[4 * i + 2] = v.Z;
            normal[4 * i + 3] = Finite(guide[4 * i + 3] / n);
        }

        return (ToImage(albedo), ToImage(normal));
    }

    /// <summary>The fewest and the most samples any pixel has accumulated (the accumulation's per-pixel counts).</summary>
    internal (int Min, int Max) SampleCountRange()
    {
        ThrowIfDisposed();
        var accum = ReadFloats(_accum);
        float min = float.MaxValue, max = 0f;
        for (int i = 3; i < accum.Length; i += 4)
        {
            min = MathF.Min(min, accum[i]);
            max = MathF.Max(max, accum[i]);
        }

        return ((int)min, (int)max);
    }

    /// <summary>Stops the render and releases its GPU resources (after the GPU has finished with them).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer.OnFinalRenderDisposed(this);
        if (_context.IsDisposed)
            return;
        bool idle = _context.CurrentFrame is null;
        if (idle)
            _context.WaitForSubmittedFrames();
        ReleaseResources(deferred: !idle);
    }

    /// <summary>Releases everything at once (the renderer is being disposed and has waited for the device).</summary>
    internal void DisposeNow()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseResources(deferred: false);
    }

    // ------------------------------------------------------------------------------------------------ rendering

    /// <summary>Rows of the next dispatches: the measured band, within <see cref="MaxRowsPerDispatch"/>.</summary>
    private int RowsPerDispatch => Math.Min(_rowsPerDispatch, Math.Max(MaxRowsPerDispatch / 8 * 8, 8));

    private int PlanDispatches(double remainingSeconds)
    {
        if (!(_secondsPerRow > 0.0))
            return 1;
        double perDispatch = _secondsPerRow * RowsPerDispatch;
        double target = Math.Min(Math.Max(remainingSeconds, perDispatch), MaxSubmitSeconds);
        return (int)Math.Clamp(Math.Floor(target / perDispatch), 1, 4096);
    }

    private void Measure(double seconds, int rows)
    {
        if (rows <= 0 || !(seconds > 0.0))
            return;
        double perRow = seconds / rows;
        _secondsPerRow = _secondsPerRow > 0.0 ? 0.5 * (_secondsPerRow + perRow) : perRow;
        int rowsForBudget = (int)Math.Floor(MaxDispatchSeconds / _secondsPerRow);
        _rowsPerDispatch = rowsForBudget >= _height ? _height : Math.Clamp(rowsForBudget / 8 * 8, 8, _height);
    }

    /// <summary>Records up to <paramref name="dispatches"/> bands of rows (one sample each) into <paramref name="frame"/>; returns the rows dispatched.</summary>
    private int RecordWork(VulkanFrame frame, int dispatches)
    {
        var cmd = frame.CommandBuffer;
        var b = _barriers;
        b.CommandBuffer = cmd;
        _world.PrepareFrame(frame, b);
        _scene.PrepareGeometry(frame, b);
        RefreshConstants(cmd, b);
        if (!_tlas.IsBuilt || _tlasVersion != _scene.PreparedVersion)
            BuildTopLevel(cmd, b);
        UpdateDescriptors();

        var vk = _context.Vk;
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _tracer.Trace);
        var sets = stackalloc DescriptorSet[3];
        sets[ShaderConstants.SetScene] = _world.CurrentSceneSet;
        sets[ShaderConstants.SetView] = _viewSet;
        sets[ShaderConstants.SetPass] = _traceSet;
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _tracer.Layout, 0, 3, sets, 0, null);
        int rows = 0;
        for (int i = 0; i < dispatches && !_complete; i++)
        {
            DeclareTraceResources(b);
            b.Flush();
            int count = Math.Min(RowsPerDispatch, _height - _row);
            // rows [_row, _row + count): the last workgroup's overhang is not traced (it belongs to the next band)
            _tracer.Push(cmd, new GpuDrawConstants { InstanceIndex = (uint)_sample, MaterialIndex = (uint)_row, Mode = (uint)(_row + count) });
            vk.CmdDispatch(cmd, ((uint)_width + 7) / 8, ((uint)count + 7) / 8, 1);
            rows += count;
            _row += count;
            if (_row >= _height)
            {
                _row = 0;
                _sample++;
                _samplesDone = _sample;
                if (_sample >= _samplesTotal)
                    _complete = true;
                // With a time limit every submission ends at a pass boundary, where Advance checks the time.
                if (_timeLimit is not null)
                    break;
            }
        }

        _work++;
        return rows;
    }

    private void DeclareTraceResources(BarrierBatch b)
    {
        b.Use(_accum, ImageUse.StorageCompute);
        b.Use(_aux, ImageUse.StorageCompute);
        b.Use(_guide, ImageUse.StorageCompute);
        _tlas.DeclareRead(b);
        _scene.DeclareGeometryReads(b);
        b.Use(_lightBuffer, BufferUse.ComputeRead);
        b.Use(_paramsBuffer, BufferUse.UniformRead);
        b.Use(_frameBuffer, BufferUse.UniformRead);
        b.Use(_world.MaterialBuffer, BufferUse.ShaderRead);
        b.Use(_world.InstanceBuffer, BufferUse.ShaderRead);
        b.Use(_world.MaterialSlotBuffer, BufferUse.ShaderRead);
        var atmosphere = _world.AtmosphereGpu;
        if (atmosphere.Smoke.Present)
            b.Use(atmosphere.Smoke.Image!, ImageUse.SampledCompute);
        if (atmosphere.Flame.Present)
            b.Use(atmosphere.Flame.Image!, ImageUse.SampledCompute);
        if (atmosphere.Noise.Present)
            b.Use(atmosphere.Noise.Image!, ImageUse.SampledCompute);
        b.Use(atmosphere.FlameLut, BufferUse.ShaderRead);
        b.Use(atmosphere.NoiseSources.Buffer, BufferUse.ShaderRead);
    }

    /// <summary>First recording: the light snapshot and its geometry, their bottom-level structures, cleared images.</summary>
    private void SetUp(CommandBuffer cmd, BarrierBatch b)
    {
        Upload<GpuLight>(cmd, b, _lightBuffer, _lightCount > 0 ? _lights : new GpuLight[1]);
        if (_lightCount > 0)
        {
            var volumes = new AabbPositions[_lightCount];
            var vertices = new Vector3[_lightCount * PathTracerScene.LensVertices];
            var indices = new uint[_lightCount * PathTracerScene.LensIndices];
            for (int i = 0; i < _lightCount; i++)
            {
                volumes[i] = PathTracerScene.LightVolume(_lights[i], _mediaMin, _mediaMax);
                PathTracerScene.WriteLens(_lights[i], i, vertices, indices);
            }

            var input = BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit |
                        BufferUsageFlags.TransferDstBit;
            _volumeBuffer = GpuBuffer.Create(_context, (ulong)(volumes.Length * sizeof(AabbPositions)), input, MemoryUsage.GpuOnly, "light volumes");
            Upload<AabbPositions>(cmd, b, _volumeBuffer, volumes);
            ulong vertexBytes = (ulong)(vertices.Length * sizeof(Vector3));
            ulong indexOffset = VkUtil.AlignUp(vertexBytes, 16);
            _lensBuffer = GpuBuffer.Create(_context, indexOffset + (ulong)(indices.Length * sizeof(uint)), input, MemoryUsage.GpuOnly, "light lenses");
            Upload<Vector3>(cmd, b, _lensBuffer, vertices);
            Upload<uint>(cmd, b, _lensBuffer, indices, indexOffset);

            _volumeBlas = BottomLevelStructure.BuildAabbs(_scene.Device, cmd, b, _volumeBuffer, (uint)volumes.Length, "light volumes");
            var lens = stackalloc BottomLevelStructure.TriangleGeometry[1];
            lens[0] = new BottomLevelStructure.TriangleGeometry(indexOffset, indices.Length, Opaque: false);
            _lensBlas = BottomLevelStructure.BuildTriangles(_scene.Device, cmd, b, _lensBuffer, (uint)vertices.Length, (ulong)sizeof(Vector3),
                new ReadOnlySpan<BottomLevelStructure.TriangleGeometry>(lens, 1), "light lenses");
            b.AddGlobal(PipelineStageFlags2.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureWriteBitKhr,
                PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.AccelerationStructureReadBitKhr);
            b.Flush();
        }

        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        foreach (var image in new[] { _accum, _aux, _guide, _ping, _pong })
        {
            b.Use(image, ImageUse.TransferDestination, discard: true);
            b.Flush();
            _context.Vk.CmdClearColorImage(cmd, image.Handle, ImageLayout.TransferDstOptimal, &clear, 1, &range);
        }

        // Every image of the path tracer's sets lives in GENERAL from now on (shaders may statically use any of them).
        foreach (var image in new[] { _accum, _aux, _guide, _ping, _pong })
            b.Use(image, ImageUse.StorageCompute);
        b.Use(_display, ImageUse.StorageCompute, discard: true);
        b.Flush();
        _setUp = true;
    }

    /// <summary>Writes the frame constants and the path tracer parameters with the world's current media.</summary>
    private void RefreshConstants(CommandBuffer cmd, BarrierBatch b)
    {
        var atmosphere = _world.Atmosphere;
        var gpu = _world.AtmosphereGpu;
        bool haze = atmosphere is not null && float.IsFinite(atmosphere.BaseScattering) && atmosphere.BaseScattering > 0f;
        float smokeSigma = atmosphere is not null && float.IsFinite(atmosphere.SmokeScatteringAtFullDensity)
            ? Math.Clamp(atmosphere.SmokeScatteringAtFullDensity, 0f, 1e4f)
            : 0f;
        float smokeMajorant = gpu.Smoke.Present && smokeSigma > 0f ? MaxDensity(atmosphere!.SmokeDensity) * smokeSigma * 1.001f : 0f;
        bool smoke = smokeMajorant > 0f;
        bool flame = gpu.Flame.Present;
        float g = atmosphere is not null && float.IsFinite(atmosphere.Anisotropy) ? Math.Clamp(atmosphere.Anisotropy, -0.95f, 0.95f) : 0f;
        float hazeSigma = haze ? Math.Min(atmosphere!.BaseScattering, 1e4f) : 0f;

        uint features = 0;
        uint flags = _lightCount > 0 ? ShaderConstants.PtFlagLights : 0u;
        if (haze)
        {
            features |= ShaderConstants.FeatureHaze;
            flags |= ShaderConstants.PtFlagHaze;
        }

        if (smoke)
        {
            features |= ShaderConstants.FeatureSmoke;
            flags |= ShaderConstants.PtFlagSmoke;
        }

        if (flame)
        {
            features |= ShaderConstants.FeatureFlame;
            flags |= ShaderConstants.PtFlagFlame;
        }

        _frameData.Features = features | ShaderConstants.FeatureVolumetrics;
        _frameData.Media = new Vector4(hazeSigma, g, smoke ? smokeSigma : 0f, VulkanRenderer.FlameAbsorption);
        _frameData.SmokeMin = new Vector4(gpu.Smoke.BoxMin, gpu.Smoke.Present ? 1f : 0f);
        _frameData.SmokeMax = new Vector4(gpu.Smoke.BoxMax, 0f);
        _frameData.FlameMin = new Vector4(gpu.Flame.BoxMin, flame ? 1f : 0f);
        _frameData.FlameMax = new Vector4(gpu.Flame.BoxMax, flame ? gpu.FlameGridCount : 0f);
        _frameData.NoiseMin = new Vector4(gpu.Noise.BoxMin, gpu.Noise.Present ? 1f : 0f);
        _frameData.NoiseMax = new Vector4(gpu.Noise.BoxMax, 0f);
        _frameData.NoiseSourceCount = (uint)gpu.NoiseSources.Count;
        _params.MediaMax.W = hazeSigma;
        _params.Majorants = new Vector4(smokeMajorant, flame ? VulkanRenderer.FlameAbsorption : 0f, g, 1f / MathF.Max(_exposure, 1e-30f));
        _params.Flags = flags;

        b.Use(_frameBuffer, BufferUse.TransferDestination);
        b.Use(_paramsBuffer, BufferUse.TransferDestination);
        b.Flush();
        var vk = _context.Vk;
        fixed (GpuFrameData* data = &_frameData)
            vk.CmdUpdateBuffer(cmd, _frameBuffer.Handle, 0, (ulong)sizeof(GpuFrameData), data);
        fixed (GpuPtParams* data = &_params)
            vk.CmdUpdateBuffer(cmd, _paramsBuffer.Handle, 0, (ulong)sizeof(GpuPtParams), data);
    }

    private static float MaxDensity(VolumeGrid? grid)
    {
        if (grid?.Values is not { } values)
            return 0f;
        long count = (long)Math.Max(grid.SizeX, 0) * Math.Max(grid.SizeY, 0) * Math.Max(grid.SizeZ, 0);
        int n = (int)Math.Min(count, values.Length);
        float max = 0f;
        for (int i = 0; i < n; i++)
        {
            float v = values[i];
            if (v > max && float.IsFinite(v))
                max = v;
        }

        return Math.Min(max, 65000f);
    }

    private void BuildTopLevel(CommandBuffer cmd, BarrierBatch b)
    {
        var scene = _scene.Instances;
        int count = scene.Length + (_lensBlas is null ? 0 : 1) + (_volumeBlas is null ? 0 : 1);
        if (_instances.Length < count)
            _instances = new TlasInstance[Math.Max(count, _instances.Length * 2)];
        scene.CopyTo(_instances);
        int n = scene.Length;
        if (_lensBlas is not null)
            _instances[n++] = TlasInstance.Create(Matrix4x4.Identity, ShaderConstants.PtCustomDisc, (byte)ShaderConstants.PtMaskDisc, _lensBlas.Address);
        if (_volumeBlas is not null)
        {
            _instances[n++] = TlasInstance.Create(Matrix4x4.Identity, ShaderConstants.PtCustomVolume, (byte)ShaderConstants.PtMaskLightVolume,
                _volumeBlas.Address);
        }

        _tlas.Build(cmd, b, _instances.AsSpan(0, n));
        _tlasVersion = _scene.PreparedVersion;
    }

    private void Upload<T>(CommandBuffer cmd, BarrierBatch b, GpuBuffer buffer, ReadOnlySpan<T> data, ulong offset = 0)
        where T : unmanaged
    {
        ulong bytes = (ulong)(data.Length * sizeof(T));
        if (bytes == 0)
            return;
        var region = _context.Upload.Allocate(bytes, 16);
        fixed (T* src = data)
            System.Buffer.MemoryCopy(src, region.Pointer, (long)bytes, (long)bytes);
        b.Use(buffer, BufferUse.TransferDestination);
        b.Flush();
        var copy = new BufferCopy(region.Offset, offset, bytes);
        _context.Vk.CmdCopyBuffer(cmd, region.Buffer, buffer.Handle, 1, &copy);
    }

    // ------------------------------------------------------------------------------------------------ descriptors

    private void UpdateDescriptors()
    {
        var atmosphere = _world.AtmosphereGpu;
        var sig = _signature;
        sig.Begin();
        sig.Add(_tlas.Handle.Handle);
        sig.Add(_scene.GeometryTable.Handle.Handle);
        sig.Add(atmosphere.Smoke.Present ? atmosphere.Smoke.Image!.View.Handle : 0);
        sig.Add(atmosphere.Flame.Present ? atmosphere.Flame.Image!.View.Handle : 0);
        sig.Add(atmosphere.Noise.Present ? atmosphere.Noise.Image!.View.Handle : 0);
        sig.Add(atmosphere.FlameLut.Handle.Handle);
        sig.Add(atmosphere.NoiseSources.Buffer.Handle.Handle);
        if (!sig.Changed() && _pool.Handle != 0)
            return;

        RetirePool();
        CreatePool();
        var shared = _renderer.SharedResources;
        var w = _writer;
        const ImageLayout read = ImageLayout.ShaderReadOnlyOptimal;
        const ImageLayout depthRead = ImageLayout.DepthStencilReadOnlyOptimal;
        var storage = DescriptorType.StorageBuffer;
        var image = DescriptorType.CombinedImageSampler;

        // Set 1: the real-time view layout with the path tracer's frame constants and the world's media.
        var set = _viewSet = Allocate(_renderer.Pipelines.ViewSetLayout);
        for (uint binding = 0; binding < ShaderConstants.ViewBindingCount; binding++)
        {
            switch (PipelineLibrary.ViewBindingType(binding))
            {
                case DescriptorType.UniformBuffer:
                    w.Buffer(set, binding, DescriptorType.UniformBuffer, _frameBuffer.Handle, 0, _frameBuffer.Size);
                    break;
                case DescriptorType.StorageBuffer:
                    var buffer = binding switch
                    {
                        ShaderConstants.BindingFlameLut => atmosphere.FlameLut,
                        ShaderConstants.BindingNoiseSources => atmosphere.NoiseSources.Buffer,
                        _ => shared.DummyBuffer,
                    };
                    w.Buffer(set, binding, storage, buffer.Handle);
                    break;
                default:
                    if (binding is ShaderConstants.BindingShadowMap or ShaderConstants.BindingShadowDepth or ShaderConstants.BindingSceneDepth)
                    {
                        w.Image(set, binding, image, binding == ShaderConstants.BindingShadowMap ? shared.ShadowCompare : shared.NearestClamp,
                            shared.DummyDepth.View, depthRead);
                    }
                    else if (binding is ShaderConstants.BindingVolume or ShaderConstants.BindingSmoke or ShaderConstants.BindingFlame or
                             ShaderConstants.BindingNoiseField)
                    {
                        var volume = binding switch
                        {
                            ShaderConstants.BindingSmoke when atmosphere.Smoke.Present => atmosphere.Smoke.Image!,
                            ShaderConstants.BindingFlame when atmosphere.Flame.Present => atmosphere.Flame.Image!,
                            ShaderConstants.BindingNoiseField when atmosphere.Noise.Present => atmosphere.Noise.Image!,
                            _ => shared.Dummy3D,
                        };
                        w.Image(set, binding, image, shared.LinearClamp, volume.View, read);
                    }
                    else
                    {
                        // (the instance-id binding is a usampler2D the path tracer never reads)
                        w.Image(set, binding, image, shared.LinearClamp, shared.Dummy2D.View, read);
                    }

                    break;
            }
        }

        w.Flush(_context);

        // Set 2: one per use of the path tracer's images.
        _traceSet = WritePassSet(_accum, _ping, _accum);
        _resolveSet = WritePassSet(_ping, _ping, _accum);
        _pingToPong = WritePassSet(_pong, _ping, _accum);
        _pongToPing = WritePassSet(_ping, _pong, _accum);
        _displayAccum = WritePassSet(_display, _ping, _accum);
        _displayPing = WritePassSet(_display, _ping, _accum);
        _displayPong = WritePassSet(_display, _pong, _accum);
        sig.Commit();
    }

    private DescriptorSet WritePassSet(GpuImage output, GpuImage input, GpuImage accum)
    {
        var set = Allocate(_tracer.SetLayout);
        var w = _writer;
        const ImageLayout general = ImageLayout.General;
        w.Image(set, ShaderConstants.BindingPtAccum, DescriptorType.StorageImage, default, accum.View, general);
        w.Image(set, ShaderConstants.BindingPtAux, DescriptorType.StorageImage, default, _aux.View, general);
        w.Image(set, ShaderConstants.BindingPtGuide, DescriptorType.StorageImage, default, _guide.View, general);
        w.Image(set, ShaderConstants.BindingPtOutput, DescriptorType.StorageImage, default, output.View, general);
        w.Image(set, ShaderConstants.BindingPtInput, DescriptorType.StorageImage, default, input.View, general);
        w.AccelerationStructure(set, ShaderConstants.BindingPtTlas, _tlas.Handle);
        w.Buffer(set, ShaderConstants.BindingPtGeometry, DescriptorType.StorageBuffer, _scene.GeometryTable.Handle);
        w.Buffer(set, ShaderConstants.BindingPtLights, DescriptorType.StorageBuffer, _lightBuffer.Handle);
        w.Buffer(set, ShaderConstants.BindingPtParams, DescriptorType.UniformBuffer, _paramsBuffer.Handle, 0, _paramsBuffer.Size);
        w.Buffer(set, ShaderConstants.BindingPtHistogram, DescriptorType.StorageBuffer, _histogram.Handle);
        w.Flush(_context);
        return set;
    }

    private DescriptorSet DisplaySetFor(GpuImage source) =>
        ReferenceEquals(source, _ping) ? _displayPing : ReferenceEquals(source, _pong) ? _displayPong : _displayAccum;

    private void CreatePool()
    {
        var sizes = stackalloc DescriptorPoolSize[5];
        sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, 16);
        sizes[1] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 64);
        sizes[2] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 32);
        sizes[3] = new DescriptorPoolSize(DescriptorType.StorageImage, 48);
        sizes[4] = new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, 8);
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 8,
            PoolSizeCount = 5,
            PPoolSizes = sizes,
        };
        DescriptorPool pool;
        VkUtil.Check(_context.Vk.CreateDescriptorPool(_context.Device, &info, null, &pool), "vkCreateDescriptorPool (final render)");
        _pool = pool;
    }

    private DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        DescriptorSet set;
        VkUtil.Check(_context.Vk.AllocateDescriptorSets(_context.Device, &info, &set), "vkAllocateDescriptorSets (final render)");
        return set;
    }

    private void RetirePool()
    {
        _signature.Invalidate();
        if (_pool.Handle != 0)
        {
            var pool = _pool;
            var vk = _context.Vk;
            var device = _context.Device;
            _context.DeferDestroy(() => vk.DestroyDescriptorPool(device, pool, null));
            _pool = default;
        }
    }

    private void BindPassSet(CommandBuffer cmd, DescriptorSet set) =>
        _context.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _tracer.Layout, (uint)ShaderConstants.SetPass, 1, &set, 0, null);

    // ------------------------------------------------------------------------------------------------ results

    /// <summary>
    /// The image holding the result: the accumulation (sum + count; <c>mean</c> false), or the denoiser's output (mean
    /// radiance; recomputed only when samples were added since).
    /// </summary>
    private (GpuImage Source, bool Mean) PrepareResult(bool denoise)
    {
        if (_context.CurrentFrame is not null)
            throw new InvalidOperationException("Final render results are read with blocking GPU work; call outside of a frame.");
        if (!_setUp)
            return (_accum, false);
        if (!denoise)
            return (_accum, false);
        if (_denoisedWork == _work && _denoised is not null)
            return (_denoised, true);
        UpdateDescriptors();
        var cmd = _context.Immediate.Begin();
        var b = _barriers;
        b.CommandBuffer = cmd;
        var vk = _context.Vk;
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _tracer.Denoise);
        uint groupsX = ((uint)_width + 7) / 8, groupsY = ((uint)_height + 7) / 8;
        // Storage reads are declared as read-write: the tracker then orders them after the earlier storage writes of the
        // same (compute) stage, which a read-only use would consider visible already.
        b.Use(_accum, ImageUse.StorageCompute);
        b.Use(_aux, ImageUse.StorageCompute);
        b.Use(_guide, ImageUse.StorageCompute);
        b.Use(_ping, ImageUse.StorageCompute, discard: true);
        b.Flush();
        BindPassSet(cmd, _resolveSet);
        _tracer.Push(cmd, new GpuDrawConstants { Mode = ShaderConstants.PtDenoiseResolve });
        vk.CmdDispatch(cmd, groupsX, groupsY, 1);
        var current = _ping;
        for (int i = 0; i < _denoiseIterations; i++)
        {
            bool toPong = ReferenceEquals(current, _ping);
            var target = toPong ? _pong : _ping;
            b.Use(current, ImageUse.StorageCompute);
            b.Use(target, ImageUse.StorageCompute, discard: true);
            b.Flush();
            BindPassSet(cmd, toPong ? _pingToPong : _pongToPing);
            _tracer.Push(cmd, new GpuDrawConstants { Mode = ShaderConstants.PtDenoiseIterate, MaterialIndex = 1u << i });
            vk.CmdDispatch(cmd, groupsX, groupsY, 1);
            current = target;
        }

        _context.Immediate.SubmitAndWait();
        _denoised = current;
        _denoisedWork = _work;
        return (current, true);
    }

    /// <summary>With auto exposure: meters the result like the real-time renderer and sets <see cref="Exposure"/>.</summary>
    private void UpdateExposure(GpuImage source, bool mean)
    {
        if (!_autoExposure || !_setUp || (_samplesDone == 0 && _row == 0))
            return;
        UpdateDescriptors();
        var cmd = _context.Immediate.Begin();
        var b = _barriers;
        b.CommandBuffer = cmd;
        b.Use(_histogram, BufferUse.TransferDestination);
        b.Flush();
        _context.Vk.CmdFillBuffer(cmd, _histogram.Handle, 0, Vk.WholeSize, 0);
        b.Use(_histogram, BufferUse.ComputeWrite);
        DeclareDisplayReads(b, source);
        b.Use(_display, ImageUse.StorageCompute);
        b.Flush();
        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _tracer.Display);
        BindPassSet(cmd, DisplaySetFor(source));
        _tracer.Push(cmd, new GpuDrawConstants
        {
            InstanceIndex = ShaderConstants.PtDisplayHistogram | (mean ? ShaderConstants.PtDisplayMean : 0u),
            Mode = _toneMode,
            Width = 1f,
        });
        _context.Vk.CmdDispatch(cmd, ((uint)_width + 7) / 8, ((uint)_height + 7) / 8, 1);
        b.Use(_histogram, BufferUse.HostRead);
        b.Flush();
        _context.Immediate.SubmitAndWait();
        _context.Allocator.Invalidate(_histogram.Allocation, 0, _histogram.Size);
        var bins = new ReadOnlySpan<uint>(_histogram.MappedPointer, (int)ShaderConstants.HistogramBins);
        if (ExposureMeter.AverageLog2Luminance(bins, _metering) is { } log2)
            _exposure = ExposureMeter.ExposureFor(log2, _metering, _compensation);
    }

    /// <summary>
    /// The images a display set reads: the accumulation and the set's input (the source or a placeholder), declared as
    /// read-write so they are ordered after earlier storage writes of the same stage.
    /// </summary>
    private void DeclareDisplayReads(BarrierBatch b, GpuImage source)
    {
        b.Use(_accum, ImageUse.StorageCompute);
        b.Use(ReferenceEquals(source, _pong) ? _pong : _ping, ImageUse.StorageCompute);
    }

    private ImageData ReadHdr(GpuImage source, bool mean, float exposure)
    {
        var values = ReadFloats(source);
        int pixels = _width * _height;
        for (int i = 0; i < pixels; i++)
        {
            float scale = exposure / (mean ? 1f : MathF.Max(values[4 * i + 3], 1f));
            values[4 * i] = Finite(values[4 * i] * scale);
            values[4 * i + 1] = Finite(values[4 * i + 1] * scale);
            values[4 * i + 2] = Finite(values[4 * i + 2] * scale);
            values[4 * i + 3] = 1f;
        }

        return ToImage(values);
    }

    private float[] ReadFloats(GpuImage image)
    {
        if (_context.CurrentFrame is not null)
            throw new InvalidOperationException("Final render results are read with blocking GPU work; call outside of a frame.");
        ulong size = (ulong)_width * (ulong)_height * 16;
        var readback = Readback(size);
        var cmd = _context.Immediate.Begin();
        var b = _barriers;
        b.CommandBuffer = cmd;
        if (!_setUp)
        {
            // Nothing rendered yet: an empty (black, zero-sample) image.
            _context.Immediate.SubmitAndWait();
            return new float[_width * _height * 4];
        }

        b.Use(image, ImageUse.TransferSource);
        b.Use(readback, BufferUse.TransferDestination);
        b.Flush();
        CopyImage(cmd, image, readback);
        b.Use(readback, BufferUse.HostRead);
        b.Flush();
        _context.Immediate.SubmitAndWait();
        _context.Allocator.Invalidate(readback.Allocation, 0, size);
        return new ReadOnlySpan<float>(readback.MappedPointer, _width * _height * 4).ToArray();
    }

    private void CopyImage(CommandBuffer cmd, GpuImage image, GpuBuffer buffer)
    {
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)_width, (uint)_height, 1),
        };
        _context.Vk.CmdCopyImageToBuffer(cmd, image.Handle, ImageLayout.TransferSrcOptimal, buffer.Handle, 1, &region);
    }

    private GpuBuffer Readback(ulong size)
    {
        if (_readback is null || _readback.Size < size)
        {
            _readback?.Dispose(); // only used by immediate submissions, which have completed
            _readback = GpuBuffer.Create(_context, VkUtil.AlignUp(Math.Max(size, 64 << 10), 64 << 10), BufferUsageFlags.TransferDstBit,
                MemoryUsage.Readback, "final render readback");
        }

        return _readback;
    }

    private ImageData ToImage(float[] rgba)
    {
        var bytes = new byte[rgba.Length * 4];
        System.Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return new ImageData { Width = _width, Height = _height, Format = PixelFormat.RgbaFloat, Pixels = bytes };
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ReleaseResources(bool deferred)
    {
        void Release(IDisposable? resource)
        {
            if (resource is null)
                return;
            if (deferred)
                _context.DeferDispose(resource);
            else
                resource.Dispose();
        }

        if (deferred)
            _tlas.DisposeDeferred();
        else
            _tlas.Dispose();
        if (_pool.Handle != 0)
        {
            if (deferred)
            {
                RetirePool();
            }
            else
            {
                _context.Vk.DestroyDescriptorPool(_context.Device, _pool, null);
                _pool = default;
            }
        }

        Release(_volumeBlas);
        Release(_lensBlas);
        Release(_volumeBuffer);
        Release(_lensBuffer);
        Release(_accum);
        Release(_aux);
        Release(_guide);
        Release(_ping);
        Release(_pong);
        Release(_display);
        Release(_lightBuffer);
        Release(_paramsBuffer);
        Release(_frameBuffer);
        Release(_histogram);
        Release(_readback);
        _volumeBlas = null;
        _lensBlas = null;
        _volumeBuffer = null;
        _lensBuffer = null;
        _readback = null;
    }
}
