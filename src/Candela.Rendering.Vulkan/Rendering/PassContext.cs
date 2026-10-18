using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Everything a pass needs to record one view: the open frame, the world, the target, the pipelines, the per-view
/// constants and the draw lists. One instance per renderer, refilled by every <see cref="IRenderer.Render"/> call
/// (no allocation per frame).
/// </summary>
internal sealed class PassContext
{
    public PassContext(VulkanContext context, VulkanRenderWorld world, PipelineLibrary pipelines, BarrierBatch barriers)
    {
        Context = context;
        World = world;
        Pipelines = pipelines;
        Barriers = barriers;
    }

    public VulkanContext Context { get; }

    public VulkanRenderWorld World { get; }

    public PipelineLibrary Pipelines { get; }

    /// <summary>Barrier batch recording into <see cref="Cmd"/>.</summary>
    public BarrierBatch Barriers { get; }

    /// <summary>Per-view draw lists (rebuilt for every view).</summary>
    public DrawList Draws { get; } = new();

    public VulkanFrame Frame { get; set; } = null!;

    public CommandBuffer Cmd { get; set; }

    public VulkanRenderTarget Target { get; set; } = null!;

    public ViewOptions View { get; set; } = null!;

    public PreviewSettings Settings { get; set; } = null!;

    public ViewShading Shading { get; set; }

    /// <summary>The per-view constants uploaded for this view.</summary>
    public GpuFrameData Data;

    /// <summary>Scene descriptor set (set 0) of the frame.</summary>
    public DescriptorSet SceneSet { get; set; }

    /// <summary>Tone mapper for the display transform (TONE_*).</summary>
    public uint ToneMode { get; set; }

    /// <summary>Wireframe edges are drawn with the edge shader instead of polygon mode LINE.</summary>
    public bool EdgeWireframe { get; set; }

    /// <summary>Linear display colour of wireframe edges.</summary>
    public Vector4 WireColor { get; set; }

    /// <summary>Linear display colour of selected objects' wireframe edges.</summary>
    public Vector4 WireSelectedColor { get; set; }
}
