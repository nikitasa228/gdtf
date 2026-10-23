using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Position of a pass in the frame. Passes run in stage order (stable within a stage); later stages plug their
/// passes in at the documented slots without touching the existing ones. Values leave room for passes in between.
/// </summary>
internal enum RenderStage
{
    /// <summary>Diagnostics recorded before everything else (test hooks such as filling the temporal histories).</summary>
    Diagnostics = 50,

    /// <summary>Shadow maps (and reflective shadow maps) of the spot lights into the shadow atlas.</summary>
    ShadowAtlas = 100,

    /// <summary>Depth + pick-id pre-pass.</summary>
    DepthPrepass = 200,

    /// <summary>Clustered light assignment (compute).</summary>
    LightAssignment = 300,

    /// <summary>Indirect light: virtual point lights and gather (RSM) or ray-traced GI, at reduced resolution.</summary>
    Indirect = 350,

    /// <summary>Opaque and alpha-tested shading into the HDR image (or the Colorless/Wireframe views).</summary>
    Opaque = 400,

    /// <summary>Lux / noise analysis values of the visible surfaces.</summary>
    Analysis = 450,

    /// <summary>Froxel in-scattering of the spot lights in haze and smoke, flame emission, ray-marched narrow beams.</summary>
    Volumetrics = 500,

    /// <summary>Alpha-blended and transmissive surfaces, back to front.</summary>
    Transparent = 600,

    /// <summary>Diagnostics on the finished HDR image before post-processing (test hooks).</summary>
    DiagnosticsAfterScene = 650,

    /// <summary>Bloom and the auto-exposure measurement.</summary>
    HdrPost = 700,

    /// <summary>Exposure is already applied; tone mapping + sRGB encoding into the display image.</summary>
    DisplayTransform = 800,

    /// <summary>Scene depth upsampled to display resolution for the overlays (reduced resolution scale only).</summary>
    OverlayDepth = 850,

    /// <summary>Grid, lines, beam outlines and the selection outline over the display image.</summary>
    Overlays = 900,
}

/// <summary>
/// One pass of the frame. A pass first declares how it uses images and buffers
/// (<see cref="DeclareResources"/>, through <see cref="BarrierBatch.Use(GpuImage, ImageUse, bool)"/>); the
/// <see cref="PassSequence"/> turns the declarations into one batched synchronization2 barrier, then calls
/// <see cref="Execute"/> to record the commands. Passes that need several dependent steps may declare more uses and
/// flush the batch inside <see cref="Execute"/>. Passes hold their pipelines and per-pass state only; per-frame
/// inputs come through the <see cref="PassContext"/>. Recording must not allocate managed memory.
/// </summary>
internal abstract unsafe class RenderPass : IDisposable
{
    private byte* _label;

    protected RenderPass(string name, RenderStage stage)
    {
        Name = name;
        Stage = stage;
        int n = Encoding.UTF8.GetByteCount(name);
        _label = (byte*)NativeMemory.Alloc((nuint)n + 1);
        Encoding.UTF8.GetBytes(name, new Span<byte>(_label, n));
        _label[n] = 0;
    }

    /// <summary>Name shown in GPU timings and debugger labels.</summary>
    public string Name { get; }

    /// <summary>Where the pass runs in the frame.</summary>
    public RenderStage Stage { get; }

    /// <summary>NUL-terminated UTF-8 name for <c>vkCmdBeginDebugUtilsLabelEXT</c>.</summary>
    internal byte* Label => _label;

    /// <summary>Whether the pass runs for this view (view mode, settings, scene content).</summary>
    public abstract bool IsActive(PassContext context);

    /// <summary>Declares the pass's resource uses; barriers are derived from the tracked states.</summary>
    public abstract void DeclareResources(PassContext context, BarrierBatch barriers);

    /// <summary>Records the pass's commands into <see cref="PassContext.Cmd"/>.</summary>
    public abstract void Execute(PassContext context);

    /// <summary>Frees the native label (derived passes release their own objects first).</summary>
    public virtual void Dispose()
    {
        if (_label != null)
        {
            NativeMemory.Free(_label);
            _label = null;
        }
    }
}

/// <summary>
/// The ordered pass list of a renderer — the small render graph of stage 1. Passes are sorted by
/// <see cref="RenderStage"/>; <see cref="Execute"/> runs every active pass with automatic barriers, a debug label
/// (visible in RenderDoc/Nsight) and a pair of GPU timestamps (reported through
/// <see cref="VulkanContext.LastGpuTimings"/>). No managed allocation per frame.
/// </summary>
internal sealed unsafe class PassSequence : IDisposable
{
    private readonly List<RenderPass> _passes = new();

    /// <summary>The passes in execution order.</summary>
    public IReadOnlyList<RenderPass> Passes => _passes;

    /// <summary>Inserts a pass after every pass of the same or an earlier stage.</summary>
    public void Add(RenderPass pass)
    {
        int i = _passes.Count;
        while (i > 0 && _passes[i - 1].Stage > pass.Stage)
            i--;
        _passes.Insert(i, pass);
    }

    /// <summary>Records every active pass for the view described by <paramref name="context"/>.</summary>
    public void Execute(PassContext context)
    {
        var debug = context.Context.DebugUtils;
        var frames = context.Context.Frames;
        var barriers = context.Barriers;
        foreach (var pass in _passes)
        {
            if (!pass.IsActive(context))
                continue;
            if (debug is not null)
            {
                var label = new DebugUtilsLabelEXT { SType = StructureType.DebugUtilsLabelExt, PLabelName = pass.Label };
                debug.CmdBeginDebugUtilsLabel(context.Cmd, &label);
            }

            pass.DeclareResources(context, barriers);
            barriers.Flush();
            uint begin = frames.WriteTimestamp(context.Frame, PipelineStageFlags2.AllCommandsBit);
            pass.Execute(context);
            barriers.Flush();
            uint end = frames.WriteTimestamp(context.Frame, PipelineStageFlags2.AllCommandsBit);
            frames.AddScope(context.Frame, pass.Name, begin, end);
            if (debug is not null)
                debug.CmdEndDebugUtilsLabel(context.Cmd);
        }

    }

    /// <summary>Disposes every pass.</summary>
    public void Dispose()
    {
        foreach (var pass in _passes)
            pass.Dispose();
        _passes.Clear();
    }
}
