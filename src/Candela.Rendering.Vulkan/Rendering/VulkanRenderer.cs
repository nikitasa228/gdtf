using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Photometry;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>Options of a <see cref="VulkanRenderer"/>.</summary>
public sealed class VulkanRendererOptions
{
    /// <summary>
    /// Draws the Wireframe view with the edge shader even when the device supports <c>fillModeNonSolid</c> (the
    /// fallback path; used by tests to cover it on every device).
    /// </summary>
    public bool ForceEdgeShaderWireframe { get; set; }

    /// <summary>
    /// Environment variable that lowers the default <see cref="MaxClusteredLights"/> (a positive whole number), so the
    /// light-list overflow warning of the editor and the CLI can be checked on a small scene.
    /// </summary>
    public const string MaxClusteredLightsEnvironmentVariable = "CANDELA_MAX_CLUSTERED_LIGHTS";

    /// <summary>
    /// Most lights the clustered light lists hold (1 … 65 536; default 65 536, or
    /// <see cref="MaxClusteredLightsEnvironmentVariable"/>). Every listed light reaches every pixel it lights, however
    /// many beams overlap there; lights beyond the limit (in world order) are not drawn, the targets report the
    /// overflow (<see cref="IRenderTarget.LightLists"/>) and the log gets a warning. The light masks take
    /// ⌈lights / 32⌉ × 4 bytes per cluster (3456 clusters per view).
    /// </summary>
    public int MaxClusteredLights { get; set; } = ParseMaxClusteredLights(Environment.GetEnvironmentVariable(MaxClusteredLightsEnvironmentVariable));

    /// <summary>The limit an environment value asks for: a positive whole number up to 65 536; anything else = 65 536.</summary>
    internal static int ParseMaxClusteredLights(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int limit) && limit > 0
            ? Math.Min(limit, ShaderConstants.MaxClusteredLights)
            : ShaderConstants.MaxClusteredLights;
}

/// <summary>CPU-side numbers of the most recent <see cref="IRenderer.Render"/> call.</summary>
/// <param name="CpuMilliseconds">Time spent recording the view on the CPU (submission included when the renderer owned the frame).</param>
/// <param name="VisibleInstances">Instances inside the view frustum.</param>
/// <param name="CulledInstances">Instances rejected by frustum culling.</param>
/// <param name="Draws">Indexed draws in the main view pass.</param>
/// <param name="Lights">Spot lights in the world.</param>
/// <param name="ShadowedLights">Lights with a shadow-atlas tile in this view.</param>
/// <param name="ShadowTilesRendered">Shadow tiles (re-)rendered for this view (0 when every tile was cached).</param>
/// <param name="RaymarchedBeams">Volumetric beams: spot lights whose single scattering is integrated per pixel.</param>
/// <param name="VirtualPointLights">Virtual point lights of the reflective shadow maps.</param>
/// <param name="FroxelLights">Lights in view whose volumetric light comes from the froxel volume (not beams).</param>
/// <param name="BeamCoverage">Estimated beam crossings per pixel (sum of the beams' shares of the screen, facets counted).</param>
/// <param name="BeamQuality">Quality factor of the beam integration (1 = full; lower when the load exceeds the budget).</param>
/// <param name="BeamTiles">Culling tiles of the beam integration.</param>
/// <param name="BeamTilePairs">Tile-beam pairs after GPU culling (read back from an earlier frame of this target; 0 until available).</param>
/// <param name="BeamMaxPerTile">Most beams in one tile after GPU culling (earlier frame).</param>
/// <param name="MaxLightsInCluster">Most lights in one cluster of the light assignment (read back from an earlier frame of this target).</param>
/// <param name="OverflowedClusters">Clusters missing a light beyond the clustered-light limit (earlier frame; 0 = every light is drawn).</param>
public readonly record struct RenderStatistics(double CpuMilliseconds, int VisibleInstances, int CulledInstances, int Draws, int Lights,
    int ShadowedLights = 0, int ShadowTilesRendered = 0, int RaymarchedBeams = 0, int VirtualPointLights = 0, int FroxelLights = 0,
    float BeamCoverage = 0f, float BeamQuality = 1f, int BeamTiles = 0, int BeamTilePairs = 0, int BeamMaxPerTile = 0,
    int MaxLightsInCluster = 0, int OverflowedClusters = 0);

/// <summary>Linear HDR read-back of a target (test/diagnostic hook): luminance units, i.e. HDR value / exposure.</summary>
/// <param name="Width">Width, pixels.</param>
/// <param name="Height">Height, pixels.</param>
/// <param name="Rgba">Linear Rec.2020 RGBA per pixel, top row first; RGB in cd/m² (alpha as rendered).</param>
/// <param name="Exposure">Exposure the frame was pre-exposed with.</param>
internal sealed record HdrReadback(int Width, int Height, float[] Rgba, float Exposure)
{
    /// <summary>Linear Rec.2020 colour at a pixel, cd/m² per channel.</summary>
    public Vector3 At(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return new Vector3(Rgba[i], Rgba[i + 1], Rgba[i + 2]);
    }

    /// <summary>Luminance at a pixel, cd/m².</summary>
    public float LuminanceAt(int x, int y) => ColorSpaces.LuminanceRec2020(At(x, y));
}

/// <summary>
/// The real-time Vulkan renderer: shadow atlas with cached, prioritised spot-light shadow maps (PCSS soft shadows),
/// light cookies (gobo, iris, framing blades, prism, frost), depth + pick pre-pass, clustered light assignment, indirect
/// light (reflective shadow maps or ray-traced GI), forward PBR shading with photometric spot lights into a pre-exposed
/// RGBA16F image, volumetric light in haze and smoke (froxels with temporal reprojection, ray-marched narrow beams,
/// flame emission), bloom, auto exposure, tone mapping into an RGBA8 display image, the analysis overlays (lux, noise,
/// cables), grid, lines, beam outlines and the selection outline; Colorless and Wireframe views. Any number of
/// <see cref="VulkanRenderTarget"/>s share one <see cref="VulkanRenderWorld"/>. Main thread only.
/// </summary>
/// <remarks>
/// <para><b>Frames.</b> When the caller has begun a frame (<see cref="VulkanContext.BeginFrame"/>, the editor),
/// <see cref="Render"/> records into it; otherwise it begins and submits a frame of its own (CLI, tests, thumbnails).
/// Nothing waits for the device in steady state. <see cref="ReadPixels"/> and <see cref="Pick"/> are blocking
/// read-backs of the last submitted frame and must not be called while a frame is being recorded.</para>
/// <para><b>Exposure.</b> Full view: HDR = luminance (cd/m²) × <c>Units.ExposureFromEv100(EV)</c> with EV = the camera's
/// EV100 (or, with <see cref="PreviewSettings.AutoExposure"/>, the EV of the adapted scene luminance) +
/// <see cref="PreviewSettings.ExposureCompensation"/> applied in stops of exposure: EV − compensation, so +1 doubles
/// the exposure (a brighter image, like photographic exposure compensation). Colorless and Wireframe are
/// display-referred and not exposed.</para>
/// </remarks>
public sealed unsafe class VulkanRenderer : IRenderer
{
    /// <summary>Weight of the reprojected froxel history (temporal accumulation).</summary>
    public const float VolumeHistoryWeight = 0.8f;

    /// <summary>Weight of the reprojected ray-traced GI history.</summary>
    public const float IndirectHistoryWeight = 0.9f;

    /// <summary>
    /// Weight of the accumulated modulation ratio of the volumetric beams (gobos, shadows, smoke in the air). Until it
    /// is reached the history is a plain average of the frames since it was reset (n / (n + 1) after n frames), so a
    /// few frames of a still view already converge.
    /// </summary>
    public const float BeamHistoryWeight = 0.9f;

    /// <summary>Speed of the auto-exposure adaptation (1/s; 1 − e^(−speed·dt) of the remaining difference per frame).</summary>
    public const float ExposureAdaptationSpeed = 1.5f;

    /// <summary>
    /// Stops below middle grey at which <see cref="ExposureMetering.Stage"/> places the metered lit part of the image
    /// (the 85th to 99th percentile of the luminance, exposure.comp): the EV100 is this much higher than the reading of
    /// that part, so light pools stay well below white and beams in haze stay visible.
    /// </summary>
    public const float StageMeteringStops = 1.5f;

    /// <summary>Absorption coefficient of luminous flames (soot), 1/m.</summary>
    public const float FlameAbsorption = 2f;

    /// <summary>
    /// Density (1/m) of the noise overlay's display-referred coloured fog at the top of the gradient range
    /// (proportional to the level's position in the range below it): 10 m of air at the maximum level shows the
    /// gradient colour at about 40 %.
    /// </summary>
    public const float NoiseFogStrength = 0.05f;

    /// <summary>
    /// Density (1/m) of the smoke overlay's display-referred coloured fog at the top of the gradient range (proportional
    /// to the square of the position of the air's scattering coefficient in the range, times the gradient's alpha): a
    /// metre of fog at the top of the default range shows its colour at about 40 %, 30 m of a light haze (0.02 /m) tint
    /// the view by about a quarter.
    /// </summary>
    public const float SmokeFogStrength = 0.6f;

    /// <summary>Froxel tile size, pixels.</summary>
    public const int FroxelTileSize = 8;

    private static readonly Vector4 NonFullBackground = new(0.022f, 0.022f, 0.022f, 1f);
    private static readonly Vector4 WireDefaultColor = new(0.55f, 0.57f, 0.6f, 1f);

    private readonly bool _ownsContext;
    private readonly ShaderCompiler _compiler;
    private readonly VulkanRenderWorld _world;
    private readonly BarrierBatch _barriers;
    private readonly PassContext _pass;
    private readonly PassSequence _passes = new();
    private readonly List<VulkanRenderTarget> _targets = new();
    private readonly LightPlanner _planner = new();
    private readonly RendererResources _shared;
    private readonly bool _edgeWireframe;
    private readonly int _maxClusteredLights;
    private ShadowAtlas? _atlas;
    private RayTracingScene? _rayTracing;
    private PathTracer? _pathTracer;
    private readonly List<VulkanFinalRender> _finalRenders = new();
    private GpuBuffer? _readback;
    private ulong _shadowViewFrame;
    private int _shadowViewsThisFrame;
    private int _shadowViewsLastFrame;
    private bool _disposed;

    /// <summary>Creates a renderer on a context the caller owns (the editor shares it with its swapchain and UI).</summary>
    public VulkanRenderer(VulkanContext context, VulkanRendererOptions? options = null)
        : this(context, options ?? new VulkanRendererOptions(), ownsContext: false)
    {
    }

    private VulkanRenderer(VulkanContext context, VulkanRendererOptions options, bool ownsContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfDisposed();
        Context = context;
        _ownsContext = ownsContext;
        _edgeWireframe = options.ForceEdgeShaderWireframe || !context.Capabilities.FillModeNonSolid;
        _maxClusteredLights = Math.Clamp(options.MaxClusteredLights, 1, ShaderConstants.MaxClusteredLights);
        _barriers = new BarrierBatch(context.Vk);
        try
        {
            _compiler = new ShaderCompiler(ShaderLibrary.Default, context.CacheDirectory, context.Log);
            _shared = new RendererResources(context, _barriers);
            _world = new VulkanRenderWorld(context, buildEdges: _edgeWireframe);
            Pipelines = new PipelineLibrary(context, _compiler, _world.SceneSetLayout, polygonWireframe: !_edgeWireframe);
            _pass = new PassContext(context, _world, Pipelines, _barriers, _shared) { EdgeWireframe = _edgeWireframe };
            _passes.Add(new HistoryFillPass());
            _passes.Add(new ShadowPass());
            _passes.Add(new DepthPrepassPass());
            _passes.Add(new ClusterAssignPass());
            _passes.Add(new VplPass());
            _passes.Add(new IndirectGatherPass());
            _passes.Add(new RayTracedGiPass());
            _passes.Add(new ForwardOpaquePass());
            _passes.Add(new ColorlessPass());
            _passes.Add(new WireframePass());
            _passes.Add(new AnalysisPass());
            _passes.Add(new FroxelInjectPass());
            _passes.Add(new FroxelIntegratePass());
            _passes.Add(new BeamCullPass());
            _passes.Add(new BeamScatterPass());
            _passes.Add(new BeamTemporalPass());
            _passes.Add(new VolumeCompositePass());
            _passes.Add(new ForwardTransparentPass());
            _passes.Add(new HdrOverflowPass());
            _passes.Add(new BloomPass());
            _passes.Add(new ExposurePass());
            _passes.Add(new TonemapPass());
            _passes.Add(new OverlayDepthPass());
            _passes.Add(new GridPass());
            _passes.Add(new LinesPass());
            _passes.Add(new SelectionMaskPass());
            _passes.Add(new OutlinePass());
            context.PipelineCacheStore.Save();
        }
        catch
        {
            DisposeCore();
            throw;
        }
    }

    /// <summary>The Vulkan context (shared with the editor's swapchain and Dear ImGui backend).</summary>
    public VulkanContext Context { get; }

    /// <inheritdoc/>
    public RendererInfo Info => Context.Info;

    /// <inheritdoc/>
    public IRenderWorld World => _world;

    /// <summary>The world with its Vulkan-specific diagnostics.</summary>
    public VulkanRenderWorld VulkanWorld => _world;

    /// <summary>True when the Wireframe view uses the edge shader (no <c>fillModeNonSolid</c>, or forced).</summary>
    public bool UsesEdgeShaderWireframe => _edgeWireframe;

    /// <summary>Numbers of the most recent <see cref="Render"/> call.</summary>
    public RenderStatistics LastStatistics { get; private set; }

    /// <summary>Linear clamp-to-edge sampler of the display images (for Dear ImGui).</summary>
    public Sampler DisplaySampler => _shared.LinearClamp;

    internal PipelineLibrary Pipelines { get; } = null!;

    /// <summary>Samplers and placeholder resources shared by the views and the path tracer.</summary>
    internal RendererResources SharedResources => _shared;

    internal PassSequence Passes => _passes;

    /// <summary>The shadow atlas (null until a view used shadows).</summary>
    internal ShadowAtlas? Atlas => _atlas;

    /// <summary>Per-view light decisions of the last <see cref="Render"/>.</summary>
    internal LightPlanner Planner => _planner;

    /// <summary>
    /// Creates a context (options as given; null = defaults with validation from <c>CANDELA_VK_VALIDATION</c>) and a
    /// renderer that owns it. Throws <see cref="VulkanUnavailableException"/> when no suitable device exists.
    /// </summary>
    public static VulkanRenderer Create(VulkanContextOptions? contextOptions = null, VulkanRendererOptions? options = null)
    {
        var context = VulkanContext.Create(contextOptions);
        try
        {
            return new VulkanRenderer(context, options ?? new VulkanRendererOptions(), ownsContext: true);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public IRenderTarget CreateTarget(int width, int height) => CreateVulkanTarget(width, height);

    /// <summary>Creates a target (sizes are clamped to 1 … the device's image limit).</summary>
    public VulkanRenderTarget CreateVulkanTarget(int width, int height)
    {
        ThrowIfDisposed();
        var target = new VulkanRenderTarget(this, width, height);
        _targets.Add(target);
        return target;
    }

    /// <inheritdoc/>
    public void Render(IRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, float deltaTime)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(settings);
        long start = Stopwatch.GetTimestamp();

        bool ownFrame = Context.CurrentFrame is null;
        var frame = ownFrame ? Context.BeginFrame() : Context.CurrentFrame!;
        int tilesRendered = 0;
        try
        {
            var cmd = frame.CommandBuffer;
            var barriers = _barriers;
            barriers.CommandBuffer = cmd;
            _world.PrepareFrame(frame, barriers);
            var p = _pass;
            var decisions = Decide(t, view, settings);
            float dt = float.IsFinite(deltaTime) ? Math.Clamp(deltaTime, 0f, 1f) : 0f;
            float exposure = ComputeExposure(t, camera, settings, decisions, frame, dt);

            p.Frame = frame;
            p.Cmd = cmd;
            p.Target = t;
            p.View = view;
            p.Settings = settings;
            p.Shading = decisions.Shading;
            p.Overlay = decisions.Overlay;
            p.LightsActive = decisions.Lights;
            p.ShadowsActive = decisions.Shadows;
            p.RsmActive = decisions.Indirect == IndirectLighting.ReflectiveShadowMaps;
            p.VolumetricsActive = decisions.Volumetrics;
            p.Indirect = decisions.Indirect;
            p.BloomActive = decisions.Bloom;
            p.AutoExposureActive = decisions.AutoExposure;
            p.Scaled = decisions.Scaled;
            p.ToneMode = decisions.Shading != ViewShading.Full ? ShaderConstants.TonePassthrough : settings.ToneMapper switch
            {
                ToneMapper.Aces => ShaderConstants.ToneAces,
                ToneMapper.Neutral => ShaderConstants.ToneNeutral,
                _ => ShaderConstants.ToneAgX,
            };
            p.WireColor = WireDefaultColor;
            var selection = ColorSpaces.SrgbDecode(new Vector3(view.SelectionColor.X, view.SelectionColor.Y, view.SelectionColor.Z));
            p.WireSelectedColor = new Vector4(selection, 1f);

            // The cluster mask layout goes into the frame data: grow the buffer first. Statistics of an earlier frame of
            // this slot (busiest cluster, overflow) are picked up now.
            t.EnsureClusterCapacity(decisions.Lights ? _world.LightCount : 0, _maxClusteredLights);
            ReadClusterStatistics(t, frame, decisions.Lights);
            BuildFrameData(t, camera, view, decisions, exposure, ref p.Data, out var frustum);
            var forward = new Vector3(p.Data.CameraForward.X, p.Data.CameraForward.Y, p.Data.CameraForward.Z);
            var eye = new Vector3(p.Data.CameraPosition.X, p.Data.CameraPosition.Y, p.Data.CameraPosition.Z);
            p.Draws.Build(_world, frustum, eye, forward, useMaterials: decisions.Shading == ViewShading.Full,
                blendReferences: decisions.Shading != ViewShading.Wireframe);
            p.SelectionActive = view.ShowSelectionOutline && p.Draws.SelectedInstances > 0;

            // Shadow atlas, light plan, acceleration structures.
            if (decisions.Shadows)
            {
                _atlas ??= new ShadowAtlas(Context);
                _atlas.Configure(settings.ShadowMapResolution, decisions.Indirect == IndirectLighting.ReflectiveShadowMaps);
                CountShadowedView(frame.Number);
            }
            else if (_atlas is not null && _world.ShadowCastingLights == 0)
            {
                // No light casts shadows any more: give the atlas memory back (destroyed after the frames using it).
                Context.DeferDispose(_atlas);
                _atlas = null;
            }

            if (_atlas is not null)
                _atlas.ProcessChanges(_world.ShadowChanges);
            else
                _world.ShadowChanges.Clear();
            p.Atlas = _atlas;
            var plan = new PlanInputs
            {
                Frustum = frustum,
                CameraPosition = eye,
                Orthographic = (p.Data.Flags & ShaderConstants.FrameOrthographic) != 0,
                TanHalfFovY = MathF.Abs(p.Data.Projection.M22) > 1e-6f ? 1f / MathF.Abs(p.Data.Projection.M22) : 1f,
                Aspect = t.Width / (float)t.Height,
                OrthoHeight = float.IsFinite(camera.OrthoHeight) && camera.OrthoHeight > 0f ? camera.OrthoHeight : 10f,
                LightRangeScale = p.Data.LightRangeScale,
                Shadows = decisions.Shadows,
                MaxShadowedLights = Math.Clamp(settings.MaxShadowedLights, 0, ShadowAtlas.MaxTiles),
                AtlasShare = Math.Max(_shadowViewsThisFrame, _shadowViewsLastFrame),
                Rsm = decisions.Indirect == IndirectLighting.ReflectiveShadowMaps,
                RayTracedGi = decisions.Indirect == IndirectLighting.RayTraced,
                VplCapacity = decisions.VplCapacity,
                VolumeLights = decisions.VolumeLights,
                MaxRaymarchedBeams = decisions.VolumeLights ? Math.Clamp(settings.MaxRaymarchedBeams, 0, ShaderConstants.MaxBeams) : 0,
                BeamOutlines = view.ShowBeamOutlines && _world.LightCount > 0,
                FrameNumber = frame.Number,
                ListedLights = _maxClusteredLights,
            };
            _planner.Plan(_world, t, decisions.Shadows ? _atlas : null, plan);
            SmokeMarkers.Build(_world.SmokeEmitters, t.EmitterLines, decisions.Overlay == OverlayMode.Smoke);
            tilesRendered = decisions.Shadows ? _atlas!.PendingRenders.Length : 0;
            p.IndirectLightCount = _planner.IndirectLightCount;
            p.RayTracing = null;
            if (decisions.Indirect == IndirectLighting.RayTraced)
            {
                _rayTracing ??= new RayTracingScene(Context, _world);
                _rayTracing.Build(frame, barriers);
                p.RayTracing = _rayTracing;
            }

            // Volumetric beams: half-resolution images, culling tiles, the scattering table (created on first use).
            bool beams = decisions.Volumetrics && decisions.VolumeLights && _planner.BeamCount > 0;
            if (beams)
            {
                ref var r = ref decisions.Requirements;
                r.BeamWidth = Math.Max((r.RenderWidth + 1) / 2, 1);
                r.BeamHeight = Math.Max((r.RenderHeight + 1) / 2, 1);
                r.BeamTilesX = (r.BeamWidth + ShaderConstants.BeamTileSize - 1) / ShaderConstants.BeamTileSize;
                r.BeamTilesY = (r.BeamHeight + ShaderConstants.BeamTileSize - 1) / ShaderConstants.BeamTileSize;
                r.BeamWords = (_planner.BeamCount + 31) / 32;
                r.BeamHistory = settings.TemporalAccumulation;
                _shared.EnsureBeamTable(barriers);
            }

            p.BeamsActive = beams;
            ReadBeamStatistics(t, frame);
            if (beams && t.LastBeamGpuStats.TotalTiles > 0)
                _planner.UseMeasuredBeamLoad(t.LastBeamGpuStats.Pairs / (float)t.LastBeamGpuStats.TotalTiles);

            // Per-view resources and descriptors.
            t.Prepare(decisions.Requirements);
            UpdateGradient(t, decisions, barriers);
            bool lightViews = t.LightViews.PrepareUpload(barriers);
            bool lists = t.Lists.PrepareUpload(barriers);
            bool beamLines = t.BeamLines.PrepareUpload(barriers);
            bool emitterLines = t.EmitterLines.PrepareUpload(barriers);
            bool slots = _atlas is not null && _atlas.Slots.PrepareUpload(barriers);
            barriers.Flush();
            if (lightViews)
                t.LightViews.RecordUpload(cmd);
            if (lists)
                t.Lists.RecordUpload(cmd);
            if (beamLines)
                t.BeamLines.RecordUpload(cmd);
            if (emitterLines)
                t.EmitterLines.RecordUpload(cmd);
            if (slots)
                _atlas!.Slots.RecordUpload(cmd);
            t.UpdateDescriptors(_shared, _world, _atlas, _rayTracing?.Tlas ?? default,
                decisions.Indirect == IndirectLighting.RayTraced ? _rayTracing?.GeometryTable : null);
            p.SceneSet = _world.CurrentSceneSet;

            FinishFrameData(t, settings, ref decisions, ref p.Data);
            p.IndirectHistoryWeight = decisions.Indirect == IndirectLighting.RayTraced && settings.TemporalAccumulation && t.IndirectHistoryValid &&
                                      t.HasPreviousFrame &&
                                      t.IndirectHistoryMode == IndirectLighting.RayTraced ? IndirectHistoryWeight : 0f;

            // Per-view constants and the light-list counter are reset by transfer commands in this command buffer.
            barriers.Use(t.FrameUniform, BufferUse.TransferDestination);
            barriers.Use(t.ClusterCounter, BufferUse.TransferDestination);
            barriers.Flush();
            fixed (GpuFrameData* data = &p.Data)
                Context.Vk.CmdUpdateBuffer(cmd, t.FrameUniform.Handle, 0, (ulong)sizeof(GpuFrameData), data);
            Context.Vk.CmdFillBuffer(cmd, t.ClusterCounter.Handle, 0, Vk.WholeSize, 0);

            _passes.Execute(p);

            // Leave the display image readable by fragment shaders (Dear ImGui) of this or later submissions.
            barriers.Use(t.Display, ImageUse.Display);
            barriers.Flush();

            t.IndirectHistoryValid = decisions.Indirect == IndirectLighting.RayTraced && p.RayTracing is { HasTlas: true };
            if (!beams || !settings.TemporalAccumulation)
                t.BeamHistoryValid = false;
            t.IndirectHistoryMode = decisions.Indirect;
            if (!decisions.Volumetrics || !settings.TemporalAccumulation)
                t.VolumeHistoryValid = false;
            t.VolumeHistoryKey = decisions.VolumeKey;
            t.VolumeHistoryExposure = exposure;
            t.PreviousViewProjection = p.Data.ViewProjection;
            t.HasPreviousFrame = true;
            t.FrameIndex++;
            t.Last = new LastFrameInfo
            {
                Valid = true,
                ViewProjection = p.Data.ViewProjection,
                InverseViewProjection = p.Data.InverseViewProjection,
                CameraPosition = eye,
                CameraForward = forward,
                Orthographic = (p.Data.Flags & ShaderConstants.FrameOrthographic) != 0,
                Exposure = p.Data.Exposure,
                Shading = decisions.Shading,
                Overlay = decisions.Overlay,
                Bloom = p.BloomActive && t.BloomLevels > 0,
                Lights = decisions.Lights ? _world.LightCount : 0,
                ListedLights = decisions.Lights ? t.ClusterListedLights : 0,
            };
        }
        finally
        {
            if (ownFrame)
                Context.SubmitFrame(frame);
        }

        int draws = _pass.Draws.Opaque.Length + _pass.Draws.Masked.Length + _pass.Draws.Blended.Length;
        bool beamsDrawn = _pass.BeamsActive;
        var beamGpu = t.LastBeamGpuStats;
        var lightLists = t.LightLists;
        LastStatistics = new RenderStatistics(Stopwatch.GetElapsedTime(start).TotalMilliseconds, _pass.Draws.VisibleInstances,
            _pass.Draws.CulledInstances, draws, _world.LightCount, _planner.ShadowedLights, tilesRendered, beamsDrawn ? _planner.BeamCount : 0,
            _planner.VplCount, _planner.FroxelLights, beamsDrawn ? _planner.BeamCoverage : 0f, beamsDrawn ? _planner.BeamQuality : 1f,
            beamsDrawn ? (int)(_pass.Data.BeamTilesX * _pass.Data.BeamTilesY) : 0, beamsDrawn ? (int)beamGpu.Pairs : 0,
            beamsDrawn ? (int)beamGpu.MaxPerTile : 0, lightLists.MaxLightsInCluster, lightLists.OverflowedClusters);
    }

    /// <summary>
    /// Reads the display image of the target's last frame as 8-bit sRGB-encoded RGBA, top row first (blocking). A
    /// target that has not been rendered since it was created or resized reads as transparent black.
    /// </summary>
    public ImageData ReadPixels(IRenderTarget target)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        int w = t.Width, h = t.Height;
        ulong size = (ulong)w * (ulong)h * 4;
        if (!t.Last.Valid)
            return new ImageData { Width = w, Height = h, Format = PixelFormat.Rgba8, Pixels = new byte[(int)size] };
        var readback = Readback(size);
        var cmd = Context.Immediate.Begin();
        _barriers.CommandBuffer = cmd;
        _barriers.Use(t.Display, ImageUse.TransferSource);
        _barriers.Use(readback, BufferUse.TransferDestination);
        _barriers.Flush();
        CopyToBuffer(cmd, t.Display, ImageAspectFlags.ColorBit, 0, 0, (uint)w, (uint)h, readback, 0);
        _barriers.Use(t.Display, ImageUse.Display);
        _barriers.Use(readback, BufferUse.HostRead);
        _barriers.Flush();
        Context.Immediate.SubmitAndWait();
        Context.Allocator.Invalidate(readback.Allocation, 0, size);
        var pixels = new byte[(int)size];
        new ReadOnlySpan<byte>(readback.MappedPointer, (int)size).CopyTo(pixels);
        return new ImageData { Width = w, Height = h, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <summary>
    /// The instance under pixel (<paramref name="x"/>, <paramref name="y"/>) (origin top-left, display resolution) of the
    /// target's last frame: its pick id, the world position reconstructed from the depth buffer, the surface normal
    /// from the depth gradient of the neighbouring pixels on the same object (facing the camera), and the view depth in
    /// metres (distance along the view direction). With a resolution scale below 1 the scene images are sampled at the
    /// corresponding internal pixel. Null for the background, non-pickable objects, pixels outside the target, or a
    /// target without a frame. Blocking.
    /// </summary>
    public PickResult? Pick(IRenderTarget target, int x, int y)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        if (!t.Last.Valid || x < 0 || y < 0 || x >= t.Width || y >= t.Height)
            return null;
        int rw = t.RenderWidth, rh = t.RenderHeight;
        int displayX = x, displayY = y;
        x = Math.Clamp((int)((x + 0.5f) * rw / t.Width), 0, rw - 1);
        y = Math.Clamp((int)((y + 0.5f) * rh / t.Height), 0, rh - 1);
        int x0 = Math.Max(x - 1, 0), y0 = Math.Max(y - 1, 0);
        int x1 = Math.Min(x + 1, rw - 1), y1 = Math.Min(y + 1, rh - 1);
        uint bw = (uint)(x1 - x0 + 1), bh = (uint)(y1 - y0 + 1);
        ulong depthBytes = bw * bh * 4ul;
        ulong pickOffset = VkUtil.AlignUp(depthBytes, 16);
        ulong size = pickOffset + depthBytes;
        var readback = Readback(size);

        var cmd = Context.Immediate.Begin();
        _barriers.CommandBuffer = cmd;
        _barriers.Use(t.Depth, ImageUse.TransferSource);
        _barriers.Use(t.PickIds, ImageUse.TransferSource);
        _barriers.Use(readback, BufferUse.TransferDestination);
        _barriers.Flush();
        CopyToBuffer(cmd, t.Depth, ImageAspectFlags.DepthBit, x0, y0, bw, bh, readback, 0);
        CopyToBuffer(cmd, t.PickIds, ImageAspectFlags.ColorBit, x0, y0, bw, bh, readback, pickOffset);
        _barriers.Use(readback, BufferUse.HostRead);
        _barriers.Flush();
        Context.Immediate.SubmitAndWait();
        Context.Allocator.Invalidate(readback.Allocation, 0, size);

        var depths = new ReadOnlySpan<float>(readback.MappedPointer, (int)(bw * bh)).ToArray();
        var ids = new ReadOnlySpan<uint>(readback.MappedPointer + pickOffset, (int)(bw * bh)).ToArray();
        int centre = (int)((y - y0) * bw + (x - x0));
        uint id = ids[centre];
        float depth = depths[centre];
        if (id == 0 || !(depth > 0f))
            return null;

        var last = t.Last;
        Vector3 UnprojectNdc(float nx, float ny, float d)
        {
            var h = Vector4.Transform(new Vector4(nx, ny, d, 1f), last.InverseViewProjection);
            return new Vector3(h.X, h.Y, h.Z) / h.W;
        }

        Vector3 Unproject(int px, int py, float d) => UnprojectNdc((px + 0.5f) / rw * 2f - 1f, (py + 0.5f) / rh * 2f - 1f, d);

        var position = Unproject(x, y, depth);
        var toCamera = last.Orthographic ? -last.CameraForward : Vector3.Normalize(last.CameraPosition - position);

        // Tangent along one axis: the neighbour on the same object with the smaller depth step (avoids silhouettes).
        Vector3? Tangent(int dx, int dy)
        {
            Vector3? best = null;
            float bestStep = float.MaxValue;
            for (int s = -1; s <= 1; s += 2)
            {
                int px = x + s * dx, py = y + s * dy;
                if (px < x0 || px > x1 || py < y0 || py > y1)
                    continue;
                int i = (int)((py - y0) * bw + (px - x0));
                if (ids[i] != id || !(depths[i] > 0f))
                    continue;
                float step = MathF.Abs(depths[i] - depth);
                if (step < bestStep)
                {
                    bestStep = step;
                    best = (Unproject(px, py, depths[i]) - position) * s;
                }
            }

            return best;
        }

        var tx = Tangent(1, 0);
        var ty = Tangent(0, 1);
        var normal = toCamera;
        if (tx is { } a && ty is { } b)
        {
            var n = Vector3.Cross(a, b);
            if (n.LengthSquared() > 1e-30f)
            {
                n = Vector3.Normalize(n);
                normal = Vector3.Dot(n, toCamera) < 0f ? -n : n;

                // At a reduced resolution scale the internal pixel centre is up to half an internal pixel away from
                // the display pixel's ray: move the point along the surface's tangent plane onto that ray.
                if (rw != t.Width || rh != t.Height)
                {
                    float nx = (displayX + 0.5f) / t.Width * 2f - 1f, ny = (displayY + 0.5f) / t.Height * 2f - 1f;
                    var origin = UnprojectNdc(nx, ny, 1f);
                    var direction = Vector3.Normalize(UnprojectNdc(nx, ny, 0.5f) - origin);
                    float denominator = Vector3.Dot(direction, normal);
                    if (MathF.Abs(denominator) > 1e-4f)
                    {
                        var hit = origin + direction * (Vector3.Dot(position - origin, normal) / denominator);
                        if (Vector3.Distance(hit, position) <= MathF.Max(a.Length(), b.Length()))
                            position = hit;
                    }
                }
            }
        }

        float viewDepth = Vector3.Dot(position - last.CameraPosition, last.CameraForward);
        return new PickResult(id, position, normal, viewDepth);
    }

    /// <summary>
    /// Starts a progressive path-traced render of the current world with default <see cref="FinalRenderOptions"/>
    /// (camera exposure, no firefly clamp, seed 0); see
    /// <see cref="StartFinalRender(RenderCamera, FinalRenderSettings, FinalRenderOptions)"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The device has no ray query support (or too few descriptors).</exception>
    public IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings) =>
        StartFinalRender(camera, settings, new FinalRenderOptions());

    /// <summary>
    /// Starts a progressive path-traced render (<see cref="VulkanFinalRender"/>) of the current world: the camera,
    /// <paramref name="settings"/>, <paramref name="options"/> and the lights are taken now; call
    /// <see cref="IFinalRender.Advance(TimeSpan)"/> until <see cref="IFinalRender.IsComplete"/>, then read the results.
    /// Renders on the device's single queue between (or inside) the caller's frames.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The device cannot run the path tracer (no ray query, or descriptor limits below its layout): the message says
    /// "not supported on this GPU" and names the device.
    /// </exception>
    IFinalRender IRenderer.StartFinalRender(RenderCamera camera, FinalRenderSettings settings, FinalRenderOptions options) =>
        StartFinalRender(camera, settings, options);

    /// <inheritdoc cref="IRenderer.StartFinalRender(RenderCamera, FinalRenderSettings, FinalRenderOptions)"/>
    public VulkanFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings, FinalRenderOptions options)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (PathTracer.Unsupported(Context) is { } reason)
            throw new NotSupportedException(reason);
        _pathTracer ??= new PathTracer(Context, _compiler, _world.SceneSetLayout, Pipelines.ViewSetLayout);
        _rayTracing ??= new RayTracingScene(Context, _world);
        var render = new VulkanFinalRender(this, _pathTracer, _rayTracing, camera, settings, options);
        _finalRenders.Add(render);
        return render;
    }

    /// <summary>Waits for the GPU, destroys the world, the targets and the pipelines, and the context when owned.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        DisposeCore();
    }

    /// <summary>
    /// Test/diagnostic hook: the target's pre-tone-mapping HDR image (internal resolution) divided by the exposure it
    /// was rendered with, i.e. scene luminance per Rec.2020 channel in cd/m². Blocking.
    /// </summary>
    internal HdrReadback ReadHdr(IRenderTarget target)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        if (!t.Last.Valid)
            throw new InvalidOperationException("The target has not been rendered yet.");
        return ReadRgba16(t.Hdr, t.Last.Exposure, 1f);
    }

    /// <summary>
    /// Test/diagnostic hook: the bloom image of the target's last frame — level 0 of the pyramid (half the internal
    /// resolution) divided by the number of levels, i.e. the image the display transform mixes in, in cd/m² (exposure
    /// removed like <see cref="ReadHdr"/>). Its mean equals the HDR image's mean when the bloom conserves energy.
    /// Blocking.
    /// </summary>
    internal HdrReadback ReadBloom(IRenderTarget target)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        if (!t.Last.Valid || !t.Last.Bloom || t.BloomLevels == 0)
            throw new InvalidOperationException("The target's last frame has no bloom.");
        return ReadRgba16(t.BloomLevel(0), t.Last.Exposure, 1f / t.BloomLevels);
    }

    private HdrReadback ReadRgba16(GpuImage image, float exposure, float scale)
    {
        int w = (int)image.Width, h = (int)image.Height;
        ulong size = (ulong)w * (ulong)h * 8;
        var readback = Readback(size);
        var cmd = Context.Immediate.Begin();
        _barriers.CommandBuffer = cmd;
        _barriers.Use(image, ImageUse.TransferSource);
        _barriers.Use(readback, BufferUse.TransferDestination);
        _barriers.Flush();
        CopyToBuffer(cmd, image, ImageAspectFlags.ColorBit, 0, 0, (uint)w, (uint)h, readback, 0);
        // Back to a sampled layout: view descriptor sets keep referencing images (bloom) that a later frame with the
        // feature off never transitions.
        _barriers.Use(image, ImageUse.SampledAnyShader);
        _barriers.Use(readback, BufferUse.HostRead);
        _barriers.Flush();
        Context.Immediate.SubmitAndWait();
        Context.Allocator.Invalidate(readback.Allocation, 0, size);
        var halves = new ReadOnlySpan<Half>(readback.MappedPointer, w * h * 4);
        var result = new float[w * h * 4];
        float colorScale = (exposure > 0f ? 1f / exposure : 1f) * scale;
        for (int i = 0; i < result.Length; i++)
            result[i] = (i & 3) == 3 ? (float)halves[i] : (float)halves[i] * colorScale;
        return new HdrReadback(w, h, result, exposure);
    }

    /// <summary>
    /// Test/diagnostic hook: the analysis image of the target's last frame (internal resolution, top row first) —
    /// illuminance in lux with <see cref="OverlayMode.Lux"/>, sound level in dB with <see cref="OverlayMode.Noise"/>, the
    /// scattering coefficient of the air at the surface in 1/m with <see cref="OverlayMode.Smoke"/>. Blocking.
    /// </summary>
    internal float[] ReadAnalysis(IRenderTarget target)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        if (!t.Last.Valid || t.Analysis is null || t.Last.Overlay is not (OverlayMode.Lux or OverlayMode.Noise or OverlayMode.Smoke))
            throw new InvalidOperationException("The target's last frame has no lux, noise or smoke overlay.");
        int w = t.RenderWidth, h = t.RenderHeight;
        ulong size = (ulong)w * (ulong)h * 4;
        var readback = Readback(size);
        var cmd = Context.Immediate.Begin();
        _barriers.CommandBuffer = cmd;
        _barriers.Use(t.Analysis, ImageUse.TransferSource);
        _barriers.Use(readback, BufferUse.TransferDestination);
        _barriers.Flush();
        CopyToBuffer(cmd, t.Analysis, ImageAspectFlags.ColorBit, 0, 0, (uint)w, (uint)h, readback, 0);
        _barriers.Use(readback, BufferUse.HostRead);
        _barriers.Flush();
        Context.Immediate.SubmitAndWait();
        Context.Allocator.Invalidate(readback.Allocation, 0, size);
        return new ReadOnlySpan<float>(readback.MappedPointer, w * h).ToArray();
    }

    /// <summary>
    /// Test/diagnostic hook: the light-list statistics of the target's last frame, read back now (blocking) instead of
    /// a frame or two later like <see cref="VulkanRenderTarget.LightLists"/>.
    /// </summary>
    internal LightListDiagnostics ReadLightLists(IRenderTarget target)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        if (!t.Last.Valid)
            throw new InvalidOperationException("The target has not been rendered yet.");
        if (t.Last.Lights == 0)
            return default;
        ulong size = (ulong)sizeof(GpuClusterCounter);
        var readback = Readback(size);
        var cmd = Context.Immediate.Begin();
        _barriers.CommandBuffer = cmd;
        _barriers.Use(t.ClusterCounter, BufferUse.TransferSource);
        _barriers.Use(readback, BufferUse.TransferDestination);
        _barriers.Flush();
        var copy = new BufferCopy(0, 0, size);
        Context.Vk.CmdCopyBuffer(cmd, t.ClusterCounter.Handle, readback.Handle, 1, &copy);
        _barriers.Use(readback, BufferUse.HostRead);
        _barriers.Flush();
        Context.Immediate.SubmitAndWait();
        Context.Allocator.Invalidate(readback.Allocation, 0, size);
        var counter = *(GpuClusterCounter*)readback.MappedPointer;
        return new LightListDiagnostics(t.Last.Lights, t.Last.ListedLights, (int)Math.Min(counter.MaxLightsInCluster, int.MaxValue),
            (int)Math.Min(counter.OverflowedClusters, ShaderConstants.ClusterCount));
    }

    internal void OnTargetDisposed(VulkanRenderTarget target) => _targets.Remove(target);

    internal void OnFinalRenderDisposed(VulkanFinalRender render) => _finalRenders.Remove(render);

    /// <summary>Feature decisions of one view.</summary>
    private struct Decisions
    {
        public ViewShading Shading;
        public OverlayMode Overlay;
        public bool Lights;
        public bool Shadows;
        public bool Haze;
        public bool Smoke;
        public bool Flame;
        public bool NoiseField;
        public bool VolumeLights;
        public bool Volumetrics;
        public IndirectLighting Indirect;
        public bool Bloom;
        public bool AutoExposure;
        public bool Scaled;
        public float Scale;
        public int VplCapacity;
        public Vector4 VolumeKey;
        public ViewRequirements Requirements;
    }

    private Decisions Decide(VulkanRenderTarget t, ViewOptions view, PreviewSettings settings)
    {
        var d = new Decisions
        {
            // Plain range checks: Enum.IsDefined reads the enum's runtime type cache, which the GC may collect under
            // memory pressure and which is then rebuilt (allocating) on the next call.
            Shading = view.Shading is ViewShading.Full or ViewShading.Colorless or ViewShading.Wireframe ? view.Shading : ViewShading.Full,
            Overlay = view.Overlay is OverlayMode.Lux or OverlayMode.Noise or OverlayMode.Cables or OverlayMode.Smoke ? view.Overlay : OverlayMode.None,
        };
        bool full = d.Shading == ViewShading.Full;
        int lights = _world.LightCount;
        var atmosphere = _world.Atmosphere;
        var gpu = _world.AtmosphereGpu;
        d.Haze = atmosphere is not null && float.IsFinite(atmosphere.BaseScattering) && atmosphere.BaseScattering > 0f;
        d.Smoke = atmosphere is not null && gpu.Smoke.Present && float.IsFinite(atmosphere.SmokeScatteringAtFullDensity) &&
                  atmosphere.SmokeScatteringAtFullDensity > 0f;
        d.Flame = gpu.Flame.Present;
        d.NoiseField = gpu.Noise.Present;
        d.Lights = lights > 0 && (full || d.Overlay == OverlayMode.Lux);
        d.Shadows = d.Lights && settings.Shadows && settings.MaxShadowedLights > 0 && _world.ShadowCastingLights > 0;
        d.VolumeLights = full && settings.Volumetrics && (d.Haze || d.Smoke) && lights > 0;
        d.Volumetrics = full && settings.Volumetrics && (d.Haze || d.Smoke || d.Flame);
        var indirect = full && d.Lights ? settings.Indirect : IndirectLighting.Off;
        if (indirect is not (IndirectLighting.Off or IndirectLighting.ReflectiveShadowMaps or IndirectLighting.RayTraced))
            indirect = IndirectLighting.Off;
        if (indirect == IndirectLighting.RayTraced && !Pipelines.RayTracing)
            indirect = IndirectLighting.ReflectiveShadowMaps;
        if (indirect == IndirectLighting.ReflectiveShadowMaps && !d.Shadows)
            indirect = IndirectLighting.Off;
        d.Indirect = indirect;
        d.Bloom = full && settings.Bloom && float.IsFinite(settings.BloomIntensity) && settings.BloomIntensity > 0f;
        d.AutoExposure = full && settings.AutoExposure;
        float scale = float.IsFinite(settings.ResolutionScale) ? Math.Clamp(settings.ResolutionScale, 0.5f, 1f) : 1f;
        int rw = Math.Max(1, (int)MathF.Round(t.Width * scale)), rh = Math.Max(1, (int)MathF.Round(t.Height * scale));
        d.Scaled = rw != t.Width || rh != t.Height;
        d.Scale = scale;
        d.VplCapacity = indirect == IndirectLighting.ReflectiveShadowMaps ? LightPlanner.VplBudget : 0;

        var r = new ViewRequirements
        {
            RenderWidth = rw,
            RenderHeight = rh,
            Scaled = d.Scaled,
            Analysis = d.Overlay is OverlayMode.Lux or OverlayMode.Noise or OverlayMode.Smoke,
            Selection = view.ShowSelectionOutline,
            IndirectHistory = indirect == IndirectLighting.RayTraced,
            VplCapacity = d.VplCapacity,
            BloomLevels = d.Bloom ? BloomLevelCount(rw, rh) : 0,
            AutoExposure = d.AutoExposure,
        };
        if (indirect != IndirectLighting.Off)
        {
            r.IndirectWidth = Math.Max((rw + 1) / 2, 1);
            r.IndirectHeight = Math.Max((rh + 1) / 2, 1);
        }

        if (d.Volumetrics)
        {
            // 8 x 8 px froxels; beyond the device's 3D image limit the froxels grow (the shaders map them by uv).
            int maxDimension = (int)Math.Min(Context.Capabilities.MaxImageDimension3D, 16384u);
            r.VolumeX = Math.Min((rw + FroxelTileSize - 1) / FroxelTileSize, maxDimension);
            r.VolumeY = Math.Min((rh + FroxelTileSize - 1) / FroxelTileSize, maxDimension);
            r.VolumeZ = Math.Clamp(settings.VolumetricSlices, 8, Math.Min(256, maxDimension));
        }

        d.Requirements = r;
        return d;
    }

    /// <summary>
    /// Counts the views that use the shadow atlas in a frame: the editor renders several viewports into one frame, and
    /// each plans its shadow tiles within an equal share of the atlas (the larger of this frame's and the previous
    /// frame's view count), so one view cannot take the tiles another view of the same frame needs.
    /// </summary>
    private void CountShadowedView(ulong frameNumber)
    {
        if (frameNumber != _shadowViewFrame)
        {
            _shadowViewsLastFrame = frameNumber == _shadowViewFrame + 1 ? _shadowViewsThisFrame : 0;
            _shadowViewsThisFrame = 0;
            _shadowViewFrame = frameNumber;
        }

        _shadowViewsThisFrame++;
    }

    private static int BloomLevelCount(int width, int height)
    {
        int levels = 0;
        int size = Math.Min(width, height) / 2;
        while (levels < 6 && size >= 4)
        {
            levels++;
            size /= 2;
        }

        return Math.Max(levels, 1);
    }

    private float ComputeExposure(VulkanRenderTarget t, RenderCamera camera, PreviewSettings settings, in Decisions d, VulkanFrame frame, float dt)
    {
        if (d.Shading != ViewShading.Full)
            return 1f;
        float compensation = float.IsFinite(settings.ExposureCompensation) ? settings.ExposureCompensation : 0f;
        float cameraEv = (float.IsFinite(camera.Ev100) ? camera.Ev100 : 6f) - compensation;
        if (!d.AutoExposure)
        {
            t.AdaptedLog2Luminance = float.NaN;
            return Units.ExposureFromEv100(cameraEv);
        }

        int slot = frame.SlotIndex;
        if (t.ExposureReadback[slot] is { } readback && t.ExposureReadbackFrame[slot] != 0 && t.ExposureReadbackFrame[slot] < frame.Number)
        {
            Context.Allocator.Invalidate(readback.Allocation, 0, (ulong)sizeof(GpuExposureResult));
            var result = *(GpuExposureResult*)readback.MappedPointer;
            t.ExposureReadbackFrame[slot] = 0;
            if (result.Count > 0 && float.IsFinite(result.AverageLog2Luminance))
            {
                float measured = result.AverageLog2Luminance;
                t.AdaptedLog2Luminance = float.IsNaN(t.AdaptedLog2Luminance)
                    ? measured
                    : t.AdaptedLog2Luminance + (measured - t.AdaptedLog2Luminance) * (1f - MathF.Exp(-dt * ExposureAdaptationSpeed));
            }
        }

        if (float.IsNaN(t.AdaptedLog2Luminance))
            return Units.ExposureFromEv100(cameraEv);
        float metering = settings.Metering == ExposureMetering.Stage ? StageMeteringStops : 0f;
        float ev = Units.Ev100FromLuminance(MathF.Pow(2f, t.AdaptedLog2Luminance)) + metering - compensation;
        return Units.ExposureFromEv100(ev);
    }

    private void BuildFrameData(VulkanRenderTarget target, RenderCamera camera, ViewOptions view, in Decisions dec, float exposure, ref GpuFrameData d,
        out Frustum frustum)
    {
        int rw = dec.Requirements.RenderWidth, rh = dec.Requirements.RenderHeight;
        var cam = CameraFrame.Compute(camera, target.Width / (float)target.Height);
        bool ortho = cam.Orthographic;
        float near = cam.Near, far = cam.Far;
        var viewMatrix = cam.View;
        var inverseView = cam.InverseView;
        var projection = cam.Projection;
        var viewProjection = cam.ViewProjection;
        var inverseProjection = cam.InverseProjection;
        var inverseViewProjection = cam.InverseViewProjection;
        float orthoHeight = cam.OrthoHeight;
        frustum = Frustum.FromViewProjection(viewProjection, finiteFar: ortho);

        var eye = cam.Eye;
        var up = cam.Up;
        var forward = cam.Forward;
        // Colorless key light: from above, behind the camera and over its left shoulder (camera yaw only, so it does
        // not swing with tilting); a top view uses the camera's up vector as "forward" on the floor.
        var flatForward = new Vector3(forward.X, forward.Y, 0f);
        if (flatForward.LengthSquared() < 1e-4f)
            flatForward = new Vector3(up.X, up.Y, 0f);
        flatForward = flatForward.LengthSquared() > 1e-8f ? Vector3.Normalize(flatForward) : Vector3.UnitY;
        var flatRight = Vector3.Cross(flatForward, Vector3.UnitZ);
        var keyLight = Vector3.Normalize(Vector3.UnitZ - 0.6f * flatForward - 0.35f * flatRight);

        bool full = dec.Shading == ViewShading.Full;
        float background = float.IsFinite(view.BackgroundLuminance) ? MathF.Max(view.BackgroundLuminance, 0f) : 0f;
        float spacing = float.IsFinite(view.GridSpacing) && view.GridSpacing > 1e-4f ? view.GridSpacing : 1f;
        float logRange = MathF.Log(far / near);

        d = default;
        d.View = viewMatrix;
        d.Projection = projection;
        d.ViewProjection = viewProjection;
        d.InverseView = inverseView;
        d.InverseProjection = inverseProjection;
        d.InverseViewProjection = inverseViewProjection;
        d.CameraPosition = new Vector4(eye, ortho ? 1f : 0f);
        d.CameraForward = new Vector4(forward, 0f);
        d.RenderSize = new Vector4(rw, rh, 1f / rw, 1f / rh);
        d.DisplaySize = new Vector4(target.Width, target.Height, 1f / target.Width, 1f / target.Height);
        d.Background = full ? new Vector4(background * exposure, background * exposure, background * exposure, 1f) : NonFullBackground;
        d.GridColorMinor = new Vector4(0.55f, 0.55f, 0.55f, 0.28f);
        d.GridColorMajor = new Vector4(0.72f, 0.72f, 0.72f, 0.5f);
        d.GridAxisX = new Vector4(0.9f, 0.22f, 0.22f, 0.9f);
        d.GridAxisY = new Vector4(0.3f, 0.82f, 0.3f, 0.9f);
        d.Exposure = exposure;
        d.NearPlane = near;
        d.FarPlane = far;
        d.ClusterLogScale = ShaderConstants.ClusterCountZ / logRange;
        d.ClusterLogBias = -ShaderConstants.ClusterCountZ * MathF.Log(near) / logRange;
        d.ClusterCountX = ShaderConstants.ClusterCountX;
        d.ClusterCountY = ShaderConstants.ClusterCountY;
        d.ClusterCountZ = ShaderConstants.ClusterCountZ;
        d.LightCount = dec.Lights ? (uint)_world.LightCount : 0u;
        d.ViewMode = dec.Shading switch
        {
            ViewShading.Colorless => ShaderConstants.ViewColorless,
            ViewShading.Wireframe => ShaderConstants.ViewWireframe,
            _ => ShaderConstants.ViewFull,
        };
        d.Flags = ortho ? ShaderConstants.FrameOrthographic : 0u;
        d.GridSpacing = spacing;
        d.GridMajorEvery = 10f;
        d.GridFadeDistance = ortho ? 0f : 150f * spacing;
        d.RangeEpsilon = ShaderConstants.LightRangeEpsilon;
        d.ClusterMaskWords = dec.Lights ? target.ClusterMaskWords : 0u;
        d.ClusterListedLights = dec.Lights ? (uint)target.ClusterListedLights : 0u;
        d.Headlight = new Vector4(keyLight, 0f);
        d.PixelFootprint = new Vector4(2f / (MathF.Abs(projection.M22) * rh), orthoHeight / rh, 0f, 0f);
        d.RenderScale = dec.Scale;
        d.AmbientLuminance = full ? AmbientLuminanceOf(_world.Atmosphere) : 0f;

        // Light range: the display threshold of the Full view; for the lux overlay far enough that the range window
        // is 1 wherever the illuminance is within 10⁻⁴ of the gradient's lower limit.
        float scale = full ? exposure / (MathF.PI * ShaderConstants.LightRangeEpsilon) : 0f;
        if (dec.Overlay == OverlayMode.Lux)
        {
            float rangeMin = float.IsFinite(view.LuxGradient.RangeMin) ? view.LuxGradient.RangeMin : 0f;
            float minimumLux = MathF.Max(1e-4f * MathF.Max(rangeMin, 1e-2f), 1e-6f);
            scale = MathF.Max(scale, 1f / minimumLux);
        }

        d.LightRangeScale = scale;
        var gradient = GradientOf(dec.Overlay, view);
        d.OverlayMode = dec.Overlay switch
        {
            OverlayMode.Lux => ShaderConstants.OverlayLux,
            OverlayMode.Noise => ShaderConstants.OverlayNoise,
            OverlayMode.Cables => ShaderConstants.OverlayCables,
            OverlayMode.Smoke => ShaderConstants.OverlaySmoke,
            _ => ShaderConstants.OverlayNone,
        };
        d.OverlayRange = new Vector4(Finite(gradient.RangeMin, 0f), Finite(gradient.RangeMax, 1f), gradient.Logarithmic ? 1f : 0f,
            dec.Overlay == OverlayMode.Smoke ? SmokeFogStrength : NoiseFogStrength);
        d.OverlayOutOfRange = RenderMath.Sanitize(gradient.OutOfRangeColor);
        d.SelectionColor = RenderMath.Sanitize(view.SelectionColor);
    }

    /// <summary>
    /// The ambient luminance of an atmosphere (<see cref="AtmosphereState.AmbientLuminance"/>, cd/m²) as the shaders get
    /// it: 0 for none, non-finite or negative values; at most 10⁶ cd/m².
    /// </summary>
    internal static float AmbientLuminanceOf(AtmosphereState? atmosphere) =>
        atmosphere is { AmbientLuminance: var l } && float.IsFinite(l) ? Math.Clamp(l, 0f, 1e6f) : 0f;

    /// <summary>Volumetric, media and feature constants (after the light plan and the per-view resources).</summary>
    private void FinishFrameData(VulkanRenderTarget t, PreviewSettings settings, ref Decisions dec, ref GpuFrameData d)
    {
        uint features = 0;
        if (dec.Shadows)
            features |= ShaderConstants.FeatureShadows;
        if (dec.Volumetrics)
            features |= ShaderConstants.FeatureVolumetrics;
        if (dec.Indirect == IndirectLighting.ReflectiveShadowMaps)
            features |= ShaderConstants.FeatureIndirectRsm;
        if (dec.Indirect == IndirectLighting.RayTraced)
            features |= ShaderConstants.FeatureIndirectRayTraced;
        if (dec.Haze && dec.Shading == ViewShading.Full)
            features |= ShaderConstants.FeatureHaze;
        if (dec.Smoke && dec.Shading == ViewShading.Full)
            features |= ShaderConstants.FeatureSmoke;
        if (dec.Flame && dec.Shading == ViewShading.Full && settings.Volumetrics)
            features |= ShaderConstants.FeatureFlame;
        if (dec.NoiseField)
            features |= ShaderConstants.FeatureNoiseField;
        if (dec.Bloom)
            features |= ShaderConstants.FeatureBloom;
        if (dec.Scaled)
            features |= ShaderConstants.FeatureScaled;
        if (dec.Volumetrics && settings.TemporalAccumulation)
            features |= ShaderConstants.FeatureTemporal;
        if (dec.Overlay == OverlayMode.Noise)
            features |= ShaderConstants.FeatureNoiseFog;
        if (dec.Overlay == OverlayMode.Smoke)
            features |= ShaderConstants.FeatureSmokeFog;
        if (dec.VolumeLights)
            features |= ShaderConstants.FeatureVolumeLights;
        d.Features = features;

        var atmosphere = _world.Atmosphere;
        var gpu = _world.AtmosphereGpu;
        float g = atmosphere is not null && float.IsFinite(atmosphere.Anisotropy) ? Math.Clamp(atmosphere.Anisotropy, -0.95f, 0.95f) : 0f;
        d.Media = new Vector4(dec.Haze ? atmosphere!.BaseScattering : 0f, g, dec.Smoke ? atmosphere!.SmokeScatteringAtFullDensity : 0f, FlameAbsorption);
        d.SmokeMin = new Vector4(gpu.Smoke.BoxMin, gpu.Smoke.Present ? 1f : 0f);
        d.SmokeMax = new Vector4(gpu.Smoke.BoxMax, 0f);
        d.FlameMin = new Vector4(gpu.Flame.BoxMin, gpu.Flame.Present ? 1f : 0f);
        d.FlameMax = new Vector4(gpu.Flame.BoxMax, gpu.Flame.Present ? gpu.FlameGridCount : 0f);
        d.NoiseMin = new Vector4(gpu.Noise.BoxMin, gpu.Noise.Present ? 1f : 0f);
        d.NoiseMax = new Vector4(gpu.Noise.BoxMax, 0f);
        d.FlameLut = new Vector4(AtmosphereResources.FlameLutMinKelvin, 1f / AtmosphereResources.FlameLutStep, AtmosphereResources.FlameLutEntries,
            AtmosphereResources.FlameLutMaxKelvin);
        d.NoiseSourceCount = (uint)gpu.NoiseSources.Count;
        d.BeamCount = _pass.BeamsActive ? (uint)_planner.BeamCount : 0u;
        d.VplCount = (uint)_planner.VplCount;
        d.ShadowTileCount = (uint)(_atlas?.TileCount ?? 0);
        d.FrameIndex = t.FrameIndex;
        d.BloomIntensity = dec.Bloom ? Math.Clamp(settings.BloomIntensity, 0f, 1f) : 0f;
        d.IndirectIntensity = 1f;
        d.PreviousViewProjection = t.HasPreviousFrame ? t.PreviousViewProjection : d.ViewProjection;

        if (_pass.BeamsActive)
        {
            var r = dec.Requirements;
            d.BeamTilesX = (uint)r.BeamTilesX;
            d.BeamTilesY = (uint)r.BeamTilesY;
            d.BeamWordsPerTile = (uint)r.BeamWords;
            bool history = settings.TemporalAccumulation && t.BeamHistoryValid && t.HasPreviousFrame;
            if (!history)
                t.BeamHistoryFrames = 0;
            d.BeamHistoryWeight = history ? MathF.Min(BeamHistoryWeight, t.BeamHistoryFrames / (t.BeamHistoryFrames + 1f)) : 0f;
            d.BeamFlags = history ? ShaderConstants.BeamFlagHistory : 0u;
            d.BeamQuality = _planner.BeamQuality;
            float smokeReference = dec.Smoke ? 0.1f * atmosphere!.SmokeScatteringAtFullDensity : 0f;
            d.BeamSigmaRef = MathF.Max(dec.Haze ? atmosphere!.BaseScattering : smokeReference, 1e-3f);
        }

        if (dec.Volumetrics)
        {
            var r = dec.Requirements;
            ComputeVolumeRange(d, out float near, out float far);
            d.VolumeGrid = new Vector4(r.VolumeX, r.VolumeY, r.VolumeZ, 1f / r.VolumeZ);
            float logRange = MathF.Log(far / near);
            d.VolumeDepth = new Vector4(near, far, r.VolumeZ / logRange, MathF.Log(near));
            int i = (int)(t.FrameIndex % 64) + 1;
            bool temporal = settings.TemporalAccumulation;
            var key = new Vector4(r.VolumeX * 4096f + r.VolumeY, r.VolumeZ, near, far);
            bool orthoMoved = (d.Flags & ShaderConstants.FrameOrthographic) != 0 && t.PreviousViewProjection != d.ViewProjection;
            bool history = temporal && t.VolumeHistoryValid && t.HasPreviousFrame && key == t.VolumeHistoryKey && !orthoMoved;
            d.VolumeJitter = temporal
                ? new Vector4(RenderMath.Halton(i, 2) - 0.5f, RenderMath.Halton(i, 3) - 0.5f, RenderMath.Halton(i, 5) - 0.5f,
                    t.VolumeHistoryExposure > 0f ? d.Exposure / t.VolumeHistoryExposure : 1f)
                : new Vector4(0f, 0f, 0f, 1f);
            d.VolumeHistoryWeight = history ? VolumeHistoryWeight : 0f;
            dec.VolumeKey = key;
        }
    }

    /// <summary>
    /// Depth range of the froxel volume: from max(camera near, 5 cm) to the far side of the scene (instances and
    /// lights, each light with up to 30 m of beam), quantised to steps of 25 % so it stays stable while things move,
    /// limited to 16 × near … min(camera far, 500 m).
    /// </summary>
    private void ComputeVolumeRange(in GpuFrameData d, out float near, out float far)
    {
        var eye = new Vector3(d.CameraPosition.X, d.CameraPosition.Y, d.CameraPosition.Z);
        var forward = new Vector3(d.CameraForward.X, d.CameraForward.Y, d.CameraForward.Z);
        near = MathF.Max(d.NearPlane, 0.05f);
        float maxDepth = 0f;
        foreach (int index in _world.LiveInstances)
        {
            var s = _world.InstanceAt(index).WorldSphere;
            if (!float.IsFinite(s.W))
                continue;
            float depth = Vector3.Dot(new Vector3(s.X, s.Y, s.Z) - eye, forward) + s.W;
            maxDepth = MathF.Max(maxDepth, depth);
        }

        foreach (ref readonly var light in _world.Lights)
        {
            float reach = MathF.Min(MathF.Sqrt(light.MaxIntensity * MathF.Max(d.LightRangeScale, 0f)), 30f);
            float depth = Vector3.Dot(light.Position - eye, forward) + reach;
            maxDepth = MathF.Max(maxDepth, depth);
        }

        if (maxDepth <= 0f)
            maxDepth = 100f;
        float limit = MathF.Min(d.FarPlane, 500f);
        float lo = near * 16f;
        float quantised = lo * MathF.Pow(1.25f, MathF.Ceiling(MathF.Log(MathF.Max(maxDepth, lo) / lo) / MathF.Log(1.25f)));
        far = Math.Clamp(quantised, lo, MathF.Max(limit, lo * 1.01f));
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    /// <summary>
    /// Picks up the beam statistics the GPU copied into this frame slot's read-back buffer the last time the slot was
    /// used (no wait: the slot's fence has been waited for by <see cref="VulkanContext.BeginFrame"/>).
    /// </summary>
    private void ReadBeamStatistics(VulkanRenderTarget t, VulkanFrame frame)
    {
        int slot = frame.SlotIndex;
        if (t.BeamStatsReadback[slot] is not { } readback || t.BeamStatsFrame[slot] == 0 || t.BeamStatsFrame[slot] >= frame.Number)
            return;
        Context.Allocator.Invalidate(readback.Allocation, 0, 16);
        var words = new ReadOnlySpan<uint>(readback.MappedPointer, 4);
        t.LastBeamGpuStats = (words[0], words[1], words[2], t.BeamStatsTiles[slot]);
        t.BeamStatsFrame[slot] = 0;
    }

    /// <summary>
    /// Picks up the light-assignment statistics the GPU copied into this frame slot's read-back buffer the last time the
    /// slot was used (no wait: <see cref="VulkanContext.BeginFrame"/> has waited for the slot's fence) into
    /// <see cref="VulkanRenderTarget.LightLists"/>, and logs a warning when lights start to be missing from a view (a
    /// note when they are back). A frame without lights clears the diagnostics and the pending read-backs.
    /// </summary>
    private void ReadClusterStatistics(VulkanRenderTarget t, VulkanFrame frame, bool lights)
    {
        if (!lights)
        {
            Array.Clear(t.ClusterStatsFrame);
            t.LightLists = default;
            ReportLightListOverflow(t);
            return;
        }

        int slot = frame.SlotIndex;
        if (t.ClusterStatsReadback[slot] is not { } readback || t.ClusterStatsFrame[slot] == 0 || t.ClusterStatsFrame[slot] >= frame.Number)
            return;
        Context.Allocator.Invalidate(readback.Allocation, 0, (ulong)sizeof(GpuClusterCounter));
        var counter = *(GpuClusterCounter*)readback.MappedPointer;
        var (count, listed) = t.ClusterStatsLights[slot];
        t.ClusterStatsFrame[slot] = 0;
        t.LightLists = new LightListDiagnostics(count, listed, (int)Math.Min(counter.MaxLightsInCluster, int.MaxValue),
            (int)Math.Min(counter.OverflowedClusters, ShaderConstants.ClusterCount));
        ReportLightListOverflow(t);
    }

    /// <summary>Logs the start (warning) and the end (note) of a light-list overflow of a target; nothing in between.</summary>
    private void ReportLightListOverflow(VulkanRenderTarget t)
    {
        var lists = t.LightLists;
        if (lists.HasOverflow == t.LightListOverflowLogged)
            return;
        t.LightListOverflowLogged = lists.HasOverflow;
        if (lists.HasOverflow)
        {
            Context.Log(VulkanLogLevel.Warning,
                $"Light lists overflowed: {lists.UnlistedLights} of {lists.Lights} lights are beyond the limit of {lists.ListedLights} clustered " +
                $"lights and are not drawn; {lists.OverflowedClusters} of {ShaderConstants.ClusterCount} clusters of a {t.Width} × {t.Height} " +
                "view miss light from them, so surfaces, the lux overlay and the volumetric light read too low there.");
        }
        else
        {
            Context.Log(VulkanLogLevel.Info, $"Light lists of a {t.Width} × {t.Height} view hold every light again.");
        }
    }

    /// <summary>The gradient an analysis overlay maps its values through (the lux gradient for the overlays without one).</summary>
    internal static ColorGradient GradientOf(OverlayMode overlay, ViewOptions view) => overlay switch
    {
        OverlayMode.Noise => view.NoiseGradient,
        OverlayMode.Smoke => view.SmokeGradient,
        _ => view.LuxGradient,
    };

    /// <summary>Re-bakes the overlay gradient table of the target when the gradient changed (allocation only then).</summary>
    private void UpdateGradient(VulkanRenderTarget t, in Decisions d, BarrierBatch barriers)
    {
        var lut = t.GradientLut;
        if (lut is null)
            return;
        var gradient = GradientOf(d.Overlay, _pass.View);
        var hash = new HashCode();
        hash.Add(gradient.RangeMin);
        hash.Add(gradient.RangeMax);
        hash.Add(gradient.Logarithmic);
        hash.Add(gradient.OutOfRangeColor);
        foreach (var stop in gradient.Stops)
        {
            hash.Add(stop.Value);
            hash.Add(stop.Color);
        }

        int value = hash.ToHashCode() | 1;
        if (value == t.GradientHash)
            return;
        t.GradientHash = value;
        int kind = d.Overlay switch { OverlayMode.Noise => 1, OverlayMode.Smoke => 2, _ => 0 };
        var colors = t.GradientBakes[kind];
        if (colors is null || colors.Length != lut.Width || t.GradientBakeHashes[kind] != value)
        {
            colors = gradient.Bake((int)lut.Width);
            t.GradientBakes[kind] = colors;
            t.GradientBakeHashes[kind] = value;
        }

        ulong bytes = lut.Width * 8ul;
        var region = Context.Upload.Allocate(bytes, 16);
        var dst = new Span<Half>(region.Pointer, (int)lut.Width * 4);
        for (int i = 0; i < colors.Length; i++)
        {
            var c = RenderMath.Sanitize(colors[i]);
            dst[4 * i] = (Half)c.X;
            dst[4 * i + 1] = (Half)c.Y;
            dst[4 * i + 2] = (Half)c.Z;
            dst[4 * i + 3] = (Half)c.W;
        }

        barriers.Use(lut, ImageUse.TransferDestination, discard: true);
        barriers.Flush();
        var copy = new BufferImageCopy
        {
            BufferOffset = region.Offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(lut.Width, 1, 1),
        };
        Context.Vk.CmdCopyBufferToImage(barriers.CommandBuffer, region.Buffer, lut.Handle, ImageLayout.TransferDstOptimal, 1, &copy);
    }

    private VulkanRenderTarget Validate(IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target is not VulkanRenderTarget t || !ReferenceEquals(t.Owner, this))
            throw new ArgumentException("The target was not created by this renderer.", nameof(target));
        ObjectDisposedException.ThrowIf(t.IsDisposed, t);
        return t;
    }

    private void CopyToBuffer(CommandBuffer cmd, GpuImage image, ImageAspectFlags aspect, int x, int y, uint width, uint height, GpuBuffer buffer,
        ulong offset)
    {
        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        Context.Vk.CmdCopyImageToBuffer(cmd, image.Handle, ImageLayout.TransferSrcOptimal, buffer.Handle, 1, &region);
    }

    private GpuBuffer Readback(ulong size)
    {
        if (_readback is null || _readback.Size < size)
        {
            _readback?.Dispose(); // only used by immediate submissions, which have completed
            _readback = GpuBuffer.Create(Context, VkUtil.AlignUp(Math.Max(size, 64 << 10), 64 << 10), BufferUsageFlags.TransferDstBit,
                MemoryUsage.Readback, "readback");
        }

        return _readback;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void DisposeCore()
    {
        _disposed = true;
        if (!Context.IsDisposed)
        {
            Context.WaitIdle();
            Context.DrainDeferred();
        }

        foreach (var target in _targets.ToArray())
            target.DisposeNow();
        _targets.Clear();
        foreach (var render in _finalRenders.ToArray())
            render.DisposeNow();
        _finalRenders.Clear();
        _pathTracer?.Dispose();
        _pathTracer = null;
        _passes.Dispose();
        Pipelines?.Dispose();
        _rayTracing?.Dispose();
        _rayTracing = null;
        _atlas?.Dispose();
        _atlas = null;
        _world?.Dispose();
        _shared?.Dispose();
        _readback?.Dispose();
        _readback = null;
        if (!Context.IsDisposed)
        {
            Context.DrainDeferred();
            Context.PipelineCacheStore.Save();
        }

        _compiler?.Dispose();
        if (_ownsContext)
            Context.Dispose();
    }
}
