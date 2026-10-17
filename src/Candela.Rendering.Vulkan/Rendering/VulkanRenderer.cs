using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
}

/// <summary>CPU-side numbers of the most recent <see cref="IRenderer.Render"/> call.</summary>
/// <param name="CpuMilliseconds">Time spent recording the view on the CPU (submission included when the renderer owned the frame).</param>
/// <param name="VisibleInstances">Instances inside the view frustum.</param>
/// <param name="CulledInstances">Instances rejected by frustum culling.</param>
/// <param name="Draws">Indexed draws in the main view pass.</param>
/// <param name="Lights">Spot lights in the world.</param>
public readonly record struct RenderStatistics(double CpuMilliseconds, int VisibleInstances, int CulledInstances, int Draws, int Lights);

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
/// The real-time Vulkan renderer (stage 1): depth + pick pre-pass, clustered light assignment, forward PBR shading
/// with photometric spot lights into a pre-exposed RGBA16F image, tone mapping into an RGBA8 display image, ground
/// grid and lines; Colorless and Wireframe views. Any number of <see cref="VulkanRenderTarget"/>s share one
/// <see cref="VulkanRenderWorld"/>. Main thread only.
/// </summary>
/// <remarks>
/// <para><b>Frames.</b> When the caller has begun a frame (<see cref="VulkanContext.BeginFrame"/>, the editor),
/// <see cref="Render"/> records into it; otherwise it begins and submits a frame of its own (CLI, tests, thumbnails).
/// Nothing waits for the device in steady state. <see cref="ReadPixels"/> and <see cref="Pick"/> are blocking
/// read-backs of the last submitted frame and must not be called while a frame is being recorded.</para>
/// <para><b>Exposure.</b> Full view: HDR = luminance (cd/m²) × <c>Units.ExposureFromEv100(camera.Ev100 +
/// settings.ExposureCompensation)</c> — the compensation is added to the EV (positive values darken, like closing
/// the aperture). Colorless and Wireframe are display-referred and not exposed.</para>
/// <para><b>Not yet used (stage 2):</b> <see cref="PreviewSettings"/> shadows, volumetrics, indirect light, bloom,
/// temporal accumulation, auto exposure and resolution scale; <see cref="ViewOptions"/> overlays, selection outline
/// and beam outlines. They are accepted and ignored.</para>
/// </remarks>
public sealed unsafe class VulkanRenderer : IRenderer
{
    private const string StageThreeMessage = "path tracer arrives in stage 3";

    private static readonly Vector4 NonFullBackground = new(0.022f, 0.022f, 0.022f, 1f);
    private static readonly Vector4 WireDefaultColor = new(0.55f, 0.57f, 0.6f, 1f);

    private readonly bool _ownsContext;
    private readonly ShaderCompiler _compiler;
    private readonly VulkanRenderWorld _world;
    private readonly BarrierBatch _barriers;
    private readonly PassContext _pass;
    private readonly PassSequence _passes = new();
    private readonly List<VulkanRenderTarget> _targets = new();
    private readonly bool _edgeWireframe;
    private GpuBuffer? _readback;
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
        _barriers = new BarrierBatch(context.Vk);
        try
        {
            _compiler = new ShaderCompiler(ShaderLibrary.Default, context.CacheDirectory, context.Log);
            DisplaySampler = CreateSampler(Filter.Linear, "display sampler");
            HdrSampler = CreateSampler(Filter.Nearest, "hdr sampler");
            _world = new VulkanRenderWorld(context, buildEdges: _edgeWireframe);
            Pipelines = new PipelineLibrary(context, _compiler, _world.SceneSetLayout, polygonWireframe: !_edgeWireframe);
            _pass = new PassContext(context, _world, Pipelines, _barriers) { EdgeWireframe = _edgeWireframe };
            _passes.Add(new DepthPrepassPass());
            _passes.Add(new ClusterAssignPass());
            _passes.Add(new ForwardOpaquePass());
            _passes.Add(new ColorlessPass());
            _passes.Add(new WireframePass());
            _passes.Add(new ForwardTransparentPass());
            _passes.Add(new TonemapPass());
            _passes.Add(new GridPass());
            _passes.Add(new LinesPass());
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
    public Sampler DisplaySampler { get; }

    internal Sampler HdrSampler { get; }

    internal PipelineLibrary Pipelines { get; } = null!;

    internal PassSequence Passes => _passes;

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
        try
        {
            var cmd = frame.CommandBuffer;
            _barriers.CommandBuffer = cmd;
            _world.PrepareFrame(frame, _barriers);
            var shading = Enum.IsDefined(view.Shading) ? view.Shading : ViewShading.Full;
            if (shading == ViewShading.Full)
                t.EnsureClusterCapacity(_world.LightCount);

            var p = _pass;
            p.Frame = frame;
            p.Cmd = cmd;
            p.Target = t;
            p.View = view;
            p.Settings = settings;
            p.Shading = shading;
            p.SceneSet = _world.CurrentSceneSet;
            p.ToneMode = shading != ViewShading.Full ? ShaderConstants.TonePassthrough : settings.ToneMapper switch
            {
                ToneMapper.Aces => ShaderConstants.ToneAces,
                ToneMapper.Neutral => ShaderConstants.ToneNeutral,
                _ => ShaderConstants.ToneAgX,
            };
            p.WireColor = WireDefaultColor;
            var selection = ColorSpaces.SrgbDecode(new Vector3(view.SelectionColor.X, view.SelectionColor.Y, view.SelectionColor.Z));
            p.WireSelectedColor = new Vector4(selection, 1f);
            BuildFrameData(t, camera, view, settings, shading, ref p.Data, out var frustum);
            var forward = new Vector3(p.Data.CameraForward.X, p.Data.CameraForward.Y, p.Data.CameraForward.Z);
            var eye = new Vector3(p.Data.CameraPosition.X, p.Data.CameraPosition.Y, p.Data.CameraPosition.Z);
            p.Draws.Build(_world, frustum, eye, forward, useMaterials: shading == ViewShading.Full);

            // Per-view constants and the light-list counter are reset by transfer commands in this command buffer.
            _barriers.Use(t.FrameUniform, BufferUse.TransferDestination);
            _barriers.Use(t.ClusterCounter, BufferUse.TransferDestination);
            _barriers.Flush();
            fixed (GpuFrameData* data = &p.Data)
                Context.Vk.CmdUpdateBuffer(cmd, t.FrameUniform.Handle, 0, (ulong)sizeof(GpuFrameData), data);
            Context.Vk.CmdFillBuffer(cmd, t.ClusterCounter.Handle, 0, Vk.WholeSize, 0);

            _passes.Execute(p);

            // Leave the display image readable by fragment shaders (Dear ImGui) of this or later submissions.
            _barriers.Use(t.Display, ImageUse.Display);
            _barriers.Flush();

            t.Last = new LastFrameInfo
            {
                Valid = true,
                ViewProjection = p.Data.ViewProjection,
                InverseViewProjection = p.Data.InverseViewProjection,
                CameraPosition = eye,
                CameraForward = forward,
                Orthographic = (p.Data.Flags & ShaderConstants.FrameOrthographic) != 0,
                Exposure = p.Data.Exposure,
                Shading = shading,
            };
        }
        finally
        {
            if (ownFrame)
                Context.SubmitFrame(frame);
        }

        int draws = _pass.Draws.Opaque.Length + _pass.Draws.Masked.Length + _pass.Draws.Blended.Length;
        LastStatistics = new RenderStatistics(Stopwatch.GetElapsedTime(start).TotalMilliseconds, _pass.Draws.VisibleInstances,
            _pass.Draws.CulledInstances, draws, _world.LightCount);
    }

    /// <inheritdoc/>
    public ImageData ReadPixels(IRenderTarget target)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        int w = t.Width, h = t.Height;
        ulong size = (ulong)w * (ulong)h * 4;
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
    /// The instance under pixel (<paramref name="x"/>, <paramref name="y"/>) (origin top-left) of the target's last
    /// frame: its pick id, the world position reconstructed from the depth buffer, the surface normal from the depth
    /// gradient of the neighbouring pixels on the same object (facing the camera), and the view depth in metres
    /// (distance along the view direction). Null for the background, non-pickable objects, pixels outside the
    /// target, or a target without a frame. Blocking.
    /// </summary>
    public PickResult? Pick(IRenderTarget target, int x, int y)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        if (!t.Last.Valid || x < 0 || y < 0 || x >= t.Width || y >= t.Height)
            return null;
        int x0 = Math.Max(x - 1, 0), y0 = Math.Max(y - 1, 0);
        int x1 = Math.Min(x + 1, t.Width - 1), y1 = Math.Min(y + 1, t.Height - 1);
        uint rw = (uint)(x1 - x0 + 1), rh = (uint)(y1 - y0 + 1);
        ulong depthBytes = rw * rh * 4ul;
        ulong pickOffset = VkUtil.AlignUp(depthBytes, 16);
        ulong size = pickOffset + depthBytes;
        var readback = Readback(size);

        var cmd = Context.Immediate.Begin();
        _barriers.CommandBuffer = cmd;
        _barriers.Use(t.Depth, ImageUse.TransferSource);
        _barriers.Use(t.PickIds, ImageUse.TransferSource);
        _barriers.Use(readback, BufferUse.TransferDestination);
        _barriers.Flush();
        CopyToBuffer(cmd, t.Depth, ImageAspectFlags.DepthBit, x0, y0, rw, rh, readback, 0);
        CopyToBuffer(cmd, t.PickIds, ImageAspectFlags.ColorBit, x0, y0, rw, rh, readback, pickOffset);
        _barriers.Use(readback, BufferUse.HostRead);
        _barriers.Flush();
        Context.Immediate.SubmitAndWait();
        Context.Allocator.Invalidate(readback.Allocation, 0, size);

        var depths = new ReadOnlySpan<float>(readback.MappedPointer, (int)(rw * rh)).ToArray();
        var ids = new ReadOnlySpan<uint>(readback.MappedPointer + pickOffset, (int)(rw * rh)).ToArray();
        int centre = (int)((y - y0) * rw + (x - x0));
        uint id = ids[centre];
        float depth = depths[centre];
        if (id == 0 || !(depth > 0f))
            return null;

        var last = t.Last;
        Vector3 Unproject(int px, int py, float d)
        {
            float nx = (px + 0.5f) / t.Width * 2f - 1f;
            float ny = (py + 0.5f) / t.Height * 2f - 1f;
            var h = Vector4.Transform(new Vector4(nx, ny, d, 1f), last.InverseViewProjection);
            return new Vector3(h.X, h.Y, h.Z) / h.W;
        }

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
                int i = (int)((py - y0) * rw + (px - x0));
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
            }
        }

        float viewDepth = Vector3.Dot(position - last.CameraPosition, last.CameraForward);
        return new PickResult(id, position, normal, viewDepth);
    }

    /// <summary>Not available in stage 1.</summary>
    /// <exception cref="NotSupportedException">Always: the path tracer arrives in stage 3.</exception>
    public IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings) => throw new NotSupportedException(StageThreeMessage);

    /// <summary>Waits for the GPU, destroys the world, the targets and the pipelines, and the context when owned.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        DisposeCore();
    }

    /// <summary>
    /// Test/diagnostic hook: the target's pre-tone-mapping HDR image divided by the exposure it was rendered with,
    /// i.e. scene luminance per Rec.2020 channel in cd/m². Blocking.
    /// </summary>
    internal HdrReadback ReadHdr(IRenderTarget target)
    {
        ThrowIfDisposed();
        var t = Validate(target);
        if (!t.Last.Valid)
            throw new InvalidOperationException("The target has not been rendered yet.");
        int w = t.Width, h = t.Height;
        ulong size = (ulong)w * (ulong)h * 8;
        var readback = Readback(size);
        var cmd = Context.Immediate.Begin();
        _barriers.CommandBuffer = cmd;
        _barriers.Use(t.Hdr, ImageUse.TransferSource);
        _barriers.Use(readback, BufferUse.TransferDestination);
        _barriers.Flush();
        CopyToBuffer(cmd, t.Hdr, ImageAspectFlags.ColorBit, 0, 0, (uint)w, (uint)h, readback, 0);
        _barriers.Use(readback, BufferUse.HostRead);
        _barriers.Flush();
        Context.Immediate.SubmitAndWait();
        Context.Allocator.Invalidate(readback.Allocation, 0, size);
        var halves = new ReadOnlySpan<Half>(readback.MappedPointer, w * h * 4);
        var result = new float[w * h * 4];
        float scale = t.Last.Exposure > 0f ? 1f / t.Last.Exposure : 1f;
        for (int i = 0; i < result.Length; i++)
            result[i] = (i & 3) == 3 ? (float)halves[i] : (float)halves[i] * scale;
        return new HdrReadback(w, h, result, t.Last.Exposure);
    }

    internal void OnTargetDisposed(VulkanRenderTarget target) => _targets.Remove(target);

    private VulkanRenderTarget Validate(IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target is not VulkanRenderTarget t || !ReferenceEquals(t.Owner, this))
            throw new ArgumentException("The target was not created by this renderer.", nameof(target));
        ObjectDisposedException.ThrowIf(t.IsDisposed, t);
        return t;
    }

    private void BuildFrameData(VulkanRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, ViewShading shading,
        ref GpuFrameData d, out Frustum frustum)
    {
        float aspect = target.Width / (float)target.Height;
        bool ortho = camera.Projection == ProjectionKind.Orthographic;
        float near = float.IsFinite(camera.Near) && camera.Near > 1e-4f ? camera.Near : 1e-4f;
        float far = float.IsFinite(camera.Far) && camera.Far > near * 1.001f ? camera.Far : near * 1000f;
        var viewMatrix = camera.View;
        if (!Matrix4x4.Invert(viewMatrix, out var inverseView))
        {
            viewMatrix = Matrix4x4.Identity;
            inverseView = Matrix4x4.Identity;
        }

        Matrix4x4 projection;
        if (ortho)
        {
            float height = float.IsFinite(camera.OrthoHeight) && camera.OrthoHeight > 1e-4f ? camera.OrthoHeight : 10f;
            projection = ReverseZ.Orthographic(height, aspect, near, far);
        }
        else
        {
            float fov = float.IsFinite(camera.FovY) ? Math.Clamp(camera.FovY, 0.01f, 3.1f) : 50f * MathF.PI / 180f;
            projection = ReverseZ.Perspective(fov, aspect, near);
        }

        var viewProjection = viewMatrix * projection;
        Matrix4x4.Invert(projection, out var inverseProjection);
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);
        frustum = Frustum.FromViewProjection(viewProjection, finiteFar: ortho);

        var eye = inverseView.Translation;
        var right = Vector3.Normalize(new Vector3(inverseView.M11, inverseView.M12, inverseView.M13));
        var up = Vector3.Normalize(new Vector3(inverseView.M21, inverseView.M22, inverseView.M23));
        var forward = -Vector3.Normalize(new Vector3(inverseView.M31, inverseView.M32, inverseView.M33));
        var headlight = Vector3.Normalize(-forward + 0.45f * up - 0.3f * right);

        bool full = shading == ViewShading.Full;
        float ev = (float.IsFinite(camera.Ev100) ? camera.Ev100 : 6f) + (float.IsFinite(settings.ExposureCompensation) ? settings.ExposureCompensation : 0f);
        float exposure = full ? Units.ExposureFromEv100(ev) : 1f;
        float background = float.IsFinite(view.BackgroundLuminance) ? MathF.Max(view.BackgroundLuminance, 0f) : 0f;
        float spacing = float.IsFinite(view.GridSpacing) && view.GridSpacing > 1e-4f ? view.GridSpacing : 1f;
        float logRange = MathF.Log(far / near);

        d.View = viewMatrix;
        d.Projection = projection;
        d.ViewProjection = viewProjection;
        d.InverseView = inverseView;
        d.InverseProjection = inverseProjection;
        d.InverseViewProjection = inverseViewProjection;
        d.CameraPosition = new Vector4(eye, ortho ? 1f : 0f);
        d.CameraForward = new Vector4(forward, 0f);
        d.RenderSize = new Vector4(target.Width, target.Height, 1f / target.Width, 1f / target.Height);
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
        d.LightCount = full ? (uint)_world.LightCount : 0u;
        d.ViewMode = shading switch
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
        d.ClusterIndexCapacity = target.ClusterIndexCapacity;
        d.Headlight = new Vector4(headlight, 0f);
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

    private Sampler CreateSampler(Filter filter, string name)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f,
        };
        Sampler sampler;
        VkUtil.Check(Context.Vk.CreateSampler(Context.Device, &info, null, &sampler), "vkCreateSampler");
        Context.SetDebugName(ObjectType.Sampler, sampler.Handle, name);
        return sampler;
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
        _passes.Dispose();
        Pipelines?.Dispose();
        _world?.Dispose();
        _readback?.Dispose();
        _readback = null;
        if (!Context.IsDisposed)
        {
            if (DisplaySampler.Handle != 0)
                Context.Vk.DestroySampler(Context.Device, DisplaySampler, null);
            if (HdrSampler.Handle != 0)
                Context.Vk.DestroySampler(Context.Device, HdrSampler, null);
            Context.DrainDeferred();
            Context.PipelineCacheStore.Save();
        }

        _compiler?.Dispose();
        if (_ownsContext)
            Context.Dispose();
    }
}
