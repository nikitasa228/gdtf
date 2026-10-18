using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Shadow atlas tiles of this view that are missing or stale (<see cref="ShadowAtlas.PendingRenders"/>): for each
/// tile the shadow casters inside its frustum (instances with <see cref="InstanceFlags.CastShadow"/>, not hidden, not
/// reference drawings; opaque and alpha-tested materials) are drawn from both sides with a slope-scaled depth bias
/// into the tile's viewport, plus the reflective shadow map (diffuse reflectance and normal) when indirect light uses
/// it. The atlas is cleared once when created; afterwards only re-rendered tiles are cleared, so cached tiles survive.
/// </summary>
internal sealed class ShadowPass : RenderPass
{
    /// <summary>Constant depth bias in units of the minimum resolvable depth difference (negative: away from the light in reverse-Z).</summary>
    public const float ConstantBias = -1.5f;

    /// <summary>Slope-scaled depth bias factor.</summary>
    public const float SlopeBias = -2.0f;

    private DrawItem[] _opaque = new DrawItem[256];
    private DrawItem[] _masked = new DrawItem[64];
    private int _opaqueCount;
    private int _maskedCount;

    public ShadowPass()
        : base("shadow atlas", RenderStage.ShadowAtlas)
    {
    }

    public override bool IsActive(PassContext context) => context.ShadowsActive && context.Atlas is { Depth: not null } a && a.PendingRenders.Length > 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var atlas = context.Atlas!;
        barriers.Use(atlas.Depth!, ImageUse.DepthAttachment, discard: atlas.Depth!.NeedsInitialization);
        if (context.RsmActive)
        {
            barriers.Use(atlas.RsmAlbedo!, ImageUse.ColorAttachment, discard: atlas.RsmAlbedo!.NeedsInitialization);
            barriers.Use(atlas.RsmNormal!, ImageUse.ColorAttachment, discard: atlas.RsmNormal!.NeedsInitialization);
        }

        PassRecording.DeclareSceneReads(context, barriers);
        barriers.Use(atlas.Slots.Buffer, BufferUse.ShaderRead);
    }

    public override unsafe void Execute(PassContext context)
    {
        var atlas = context.Atlas!;
        var vk = context.Context.Vk;
        var cmd = context.Cmd;
        bool rsm = context.RsmActive;
        bool clearAll = atlas.Depth!.NeedsInitialization || (rsm && atlas.RsmAlbedo!.NeedsInitialization);
        var zero = Vector4.Zero;
        if (rsm)
        {
            PassRecording.Begin(context, new ColorAttachment(atlas.RsmAlbedo!, clearAll ? zero : null),
                new ColorAttachment(atlas.RsmNormal!, clearAll ? zero : null), default, 2, atlas.Depth, depthClear: clearAll, depthWrite: true);
        }
        else
        {
            PassRecording.Begin(context, default, default, default, 0, atlas.Depth, depthClear: clearAll, depthWrite: true);
        }

        atlas.Depth.NeedsInitialization = false;
        if (rsm)
            atlas.RsmAlbedo!.NeedsInitialization = atlas.RsmNormal!.NeedsInitialization = false;

        context.Pipelines.BindSets(cmd, PipelineBindPoint.Graphics, context.SceneSet, context.Target.ViewSet);
        vk.CmdSetDepthBias(cmd, ConstantBias, 0f, SlopeBias);
        var clears = stackalloc ClearAttachment[3];
        foreach (int index in atlas.PendingRenders)
        {
            ref var tile = ref atlas.TileAt(index);
            PassRecording.SetViewport(context, tile.X, tile.Y, (uint)tile.Size, (uint)tile.Size);
            if (!clearAll)
            {
                uint n = 0;
                clears[n++] = new ClearAttachment { AspectMask = ImageAspectFlags.DepthBit, ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)) };
                if (rsm)
                {
                    clears[n++] = new ClearAttachment { AspectMask = ImageAspectFlags.ColorBit, ColorAttachment = 0, ClearValue = default };
                    clears[n++] = new ClearAttachment { AspectMask = ImageAspectFlags.ColorBit, ColorAttachment = 1, ClearValue = default };
                }

                var rect = new ClearRect(new Rect2D(new Offset2D(tile.X, tile.Y), new Extent2D((uint)tile.Size, (uint)tile.Size)), 0, 1);
                vk.CmdClearAttachments(cmd, n, clears, 1, &rect);
            }

            CollectCasters(context.World, tile);
            if (_opaqueCount > 0)
            {
                vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, rsm ? context.Pipelines.ShadowRsm : context.Pipelines.ShadowDepth);
                PassRecording.DrawMeshes(context, _opaque.AsSpan(0, _opaqueCount), forceCullNone: true, mode: (uint)index);
            }

            if (_maskedCount > 0)
            {
                vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, rsm ? context.Pipelines.ShadowRsmMask : context.Pipelines.ShadowDepthMask);
                PassRecording.DrawMeshes(context, _masked.AsSpan(0, _maskedCount), forceCullNone: true, mode: (uint)index);
            }

            atlas.MarkRendered(index, rsm);
        }

        vk.CmdEndRendering(cmd);
    }

    /// <summary>Shadow casters whose bounding sphere intersects the tile's frustum.</summary>
    private void CollectCasters(VulkanRenderWorld world, in ShadowAtlas.Tile tile)
    {
        _opaqueCount = _maskedCount = 0;
        foreach (int index in world.LiveInstances)
        {
            var instance = world.InstanceAt(index);
            var mesh = instance.Mesh;
            if ((instance.Flags & InstanceFlags.CastShadow) == 0 || (instance.Flags & (InstanceFlags.Hidden | InstanceFlags.Reference)) != 0 ||
                mesh.Destroyed || mesh.Geometry is null || mesh.IndexCount == 0 || !tile.Intersects(instance.WorldSphere))
            {
                continue;
            }

            foreach (var sub in mesh.SubMeshes)
            {
                if (sub.IndexCount <= 0)
                    continue;
                var material = sub.MaterialSlot < instance.Materials.Length ? instance.Materials[sub.MaterialSlot] : null;
                var kind = material?.Kind ?? DrawKind.Opaque;
                if (kind == DrawKind.Blend)
                    continue;
                var item = new DrawItem
                {
                    Instance = index,
                    IndexStart = (uint)sub.IndexStart,
                    IndexCount = (uint)sub.IndexCount,
                    Material = material is null ? 0u : (uint)material.Index,
                    DoubleSided = true,
                };
                if (kind == DrawKind.Mask)
                    Add(ref _masked, ref _maskedCount, item);
                else
                    Add(ref _opaque, ref _opaqueCount, item);
            }
        }
    }

    private static void Add(ref DrawItem[] array, ref int count, in DrawItem item)
    {
        if (count == array.Length)
            Array.Resize(ref array, array.Length * 2);
        array[count++] = item;
    }
}

/// <summary>
/// Reflective shadow maps, step 1 (compute, one work group per light with a reflective shadow map in this view):
/// importance-sampled virtual point lights from the lights' current intensity, colour and cookie and the cached
/// reflectance/normal/depth of their shadow tiles (vpl.comp).
/// </summary>
internal sealed class VplPass : RenderPass
{
    public VplPass()
        : base("virtual point lights", RenderStage.Indirect)
    {
    }

    public override bool IsActive(PassContext context) =>
        context.Indirect == IndirectLighting.ReflectiveShadowMaps && context.IndirectLightCount > 0 && context.Target.Vpls is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var atlas = context.Atlas!;
        PassRecording.DeclareSceneReads(context, barriers);
        PassRecording.DeclareLightingReads(context, barriers, compute: true);
        barriers.Use(atlas.RsmAlbedo!, ImageUse.SampledCompute);
        barriers.Use(atlas.RsmNormal!, ImageUse.SampledCompute);
        barriers.Use(context.Target.Vpls!, BufferUse.ComputeWrite);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.BindCompute(context, context.Pipelines.VplGenerate);
        context.Context.Vk.CmdDispatch(context.Cmd, (uint)context.IndirectLightCount, 1, 1);
    }
}

/// <summary>Reflective shadow maps, step 2: indirect irradiance at reduced resolution from the virtual point lights (gather.comp).</summary>
internal sealed class IndirectGatherPass : RenderPass
{
    public IndirectGatherPass()
        : base("indirect gather", RenderStage.Indirect)
    {
    }

    public override bool IsActive(PassContext context) => context.Indirect == IndirectLighting.ReflectiveShadowMaps && context.Target.Indirect is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        barriers.Use(t.FrameUniform, BufferUse.UniformRead);
        barriers.Use(t.Indirect!, ImageUse.StorageCompute, discard: true);
        barriers.Use(t.Depth, ImageUse.DepthSampledCompute);
        barriers.Use(t.Normals, ImageUse.SampledCompute);
        if (t.Vpls is not null)
            barriers.Use(t.Vpls, BufferUse.ComputeRead);
    }

    public override void Execute(PassContext context)
    {
        var indirect = context.Target.Indirect!;
        PassRecording.BindCompute(context, context.Pipelines.IndirectGather, context.Target.GatherSet);
        context.Context.Vk.CmdDispatch(context.Cmd, (indirect.Width + 7) / 8, (indirect.Height + 7) / 8, 1);
    }
}

/// <summary>
/// Ray-traced one-bounce diffuse GI (ray query against the scene TLAS) at reduced resolution with temporal
/// accumulation; the result is copied to the history for the next frame (rt_gi.comp).
/// </summary>
internal sealed class RayTracedGiPass : RenderPass
{
    public RayTracedGiPass()
        : base("indirect (ray traced)", RenderStage.Indirect)
    {
    }

    public override bool IsActive(PassContext context) =>
        context.Indirect == IndirectLighting.RayTraced && context.RayTracing is { HasTlas: true } && context.Target.RayTracingSet.Handle != 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        PassRecording.DeclareSceneReads(context, barriers);
        PassRecording.DeclareLightingReads(context, barriers, compute: true);
        barriers.Use(t.Indirect!, ImageUse.StorageCompute, discard: true);
        barriers.Use(t.IndirectHistory!, ImageUse.SampledCompute);
        barriers.Use(t.Depth, ImageUse.DepthSampledCompute);
        barriers.Use(t.Normals, ImageUse.SampledCompute);
        context.RayTracing!.DeclareReads(barriers);
    }

    public override unsafe void Execute(PassContext context)
    {
        var t = context.Target;
        var indirect = t.Indirect!;
        var vk = context.Context.Vk;
        PassRecording.BindCompute(context, context.Pipelines.RayTracedGi, t.RayTracingSet);
        context.Pipelines.Push(context.Cmd, new GpuDrawConstants { Mode = (uint)context.IndirectLightCount, Width = context.IndirectHistoryWeight });
        vk.CmdDispatch(context.Cmd, (indirect.Width + 7) / 8, (indirect.Height + 7) / 8, 1);

        // Keep this frame's result as the next frame's history.
        var barriers = context.Barriers;
        barriers.Use(indirect, ImageUse.TransferSource);
        barriers.Use(t.IndirectHistory!, ImageUse.TransferDestination, discard: true);
        barriers.Flush();
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(indirect.Width, indirect.Height, 1),
        };
        vk.CmdCopyImage(context.Cmd, indirect.Handle, ImageLayout.TransferSrcOptimal, t.IndirectHistory!.Handle, ImageLayout.TransferDstOptimal, 1, &copy);
    }
}

/// <summary>
/// Lux and noise overlays: per-pixel illuminance (lx) or sound level (dB) of every visible opaque surface into the
/// analysis image (EQUAL depth test against the pre-pass; analysis.frag). Runs in every view mode.
/// </summary>
internal sealed class AnalysisPass : RenderPass
{
    public AnalysisPass()
        : base("analysis overlay", RenderStage.Analysis)
    {
    }

    public override bool IsActive(PassContext context) =>
        context.Overlay is OverlayMode.Lux or OverlayMode.Noise && context.Target.Analysis is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Analysis!, ImageUse.ColorAttachment, discard: true);
        barriers.Use(context.Target.Depth, ImageUse.DepthTestReadOnly);
        PassRecording.DeclareSceneReads(context, barriers);
        PassRecording.DeclareLightingReads(context, barriers, compute: false);
        PassRecording.DeclareMediaReads(context, barriers, compute: false);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Analysis!, Vector4.Zero, context.Target.Depth, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.Analysis);
        PassRecording.DrawMeshes(context, context.Draws.Opaque);
        PassRecording.DrawMeshes(context, context.Draws.Masked);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}
