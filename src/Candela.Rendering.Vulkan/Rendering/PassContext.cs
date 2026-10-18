using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Everything a pass needs to record one view: the open frame, the world, the target, the pipelines, the per-view
/// constants, the draw lists and the feature decisions of this view. One instance per renderer, refilled by every
/// <see cref="IRenderer.Render"/> call (no allocation per frame).
/// </summary>
internal sealed class PassContext
{
    public PassContext(VulkanContext context, VulkanRenderWorld world, PipelineLibrary pipelines, BarrierBatch barriers, RendererResources shared)
    {
        Context = context;
        World = world;
        Pipelines = pipelines;
        Barriers = barriers;
        Shared = shared;
    }

    public VulkanContext Context { get; }

    public VulkanRenderWorld World { get; }

    public PipelineLibrary Pipelines { get; }

    /// <summary>Barrier batch recording into <see cref="Cmd"/>.</summary>
    public BarrierBatch Barriers { get; }

    /// <summary>Samplers and placeholder resources.</summary>
    public RendererResources Shared { get; }

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

    /// <summary>The shadow atlas (null until shadows were first used).</summary>
    public ShadowAtlas? Atlas { get; set; }

    /// <summary>Ray-tracing scene (TLAS) when ray-traced GI runs this frame.</summary>
    public RayTracingScene? RayTracing { get; set; }

    /// <summary>Clustered light lists are built (Full view or lux overlay with lights).</summary>
    public bool LightsActive { get; set; }

    /// <summary>Shadow maps are rendered and sampled.</summary>
    public bool ShadowsActive { get; set; }

    /// <summary>Reflective shadow maps are rendered with the shadow tiles.</summary>
    public bool RsmActive { get; set; }

    /// <summary>The froxel volume runs (media present, or the noise overlay's fog).</summary>
    public bool VolumetricsActive { get; set; }

    /// <summary>Volumetric beams are integrated per pixel in this view (beam culling, scattering, temporal passes).</summary>
    public bool BeamsActive { get; set; }

    /// <summary>Indirect light of this view.</summary>
    public IndirectLighting Indirect { get; set; }

    /// <summary>Weight of the ray-traced GI history (0 = none).</summary>
    public float IndirectHistoryWeight { get; set; }

    public bool BloomActive { get; set; }

    public bool AutoExposureActive { get; set; }

    /// <summary>The scene is rendered below the display resolution.</summary>
    public bool Scaled { get; set; }

    /// <summary>The overlay of this view.</summary>
    public OverlayMode Overlay { get; set; }

    /// <summary>The selection outline is drawn (some visible instance is selected).</summary>
    public bool SelectionActive { get; set; }

    /// <summary>Lights in the indirect-light list (after the ray-marched beams).</summary>
    public int IndirectLightCount { get; set; }

    /// <summary>Depth image the display-resolution overlays test against.</summary>
    public GpuImage OverlayDepth => Target.DisplayDepth ?? Target.Depth;
}
