using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>Recording helpers shared by the passes (no allocation).</summary>
internal static unsafe class PassRecording
{
    /// <summary>Begins dynamic rendering into one colour attachment and an optional depth attachment.</summary>
    /// <param name="context">Pass context.</param>
    /// <param name="color">Colour attachment.</param>
    /// <param name="colorClear">Clear value; null = load the previous contents.</param>
    /// <param name="depth">Depth attachment or null.</param>
    /// <param name="depthClear">Clear the depth (to 0 = far plane, reverse-Z) and write it; false = read-only (load, no store).</param>
    public static void Begin(PassContext context, GpuImage color, Vector4? colorClear, GpuImage? depth, bool depthClear)
    {
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = color.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = colorClear.HasValue ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        if (colorClear is { } c)
        {
            if (color.Format == Format.R32Uint)
                colorAttachment.ClearValue.Color = new ClearColorValue(uint32_0: (uint)c.X, uint32_1: (uint)c.Y, uint32_2: (uint)c.Z, uint32_3: (uint)c.W);
            else
                colorAttachment.ClearValue.Color = new ClearColorValue(c.X, c.Y, c.Z, c.W);
        }

        var depthAttachment = default(RenderingAttachmentInfo);
        if (depth is not null)
        {
            depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depth.View,
                ImageLayout = depthClear ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = depthClear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = depthClear ? AttachmentStoreOp.Store : AttachmentStoreOp.None,
            };
            depthAttachment.ClearValue.DepthStencil = new ClearDepthStencilValue(0f, 0);
        }

        var info = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(color.Width, color.Height)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = depth is null ? null : &depthAttachment,
        };
        var vk = context.Context.Vk;
        vk.CmdBeginRendering(context.Cmd, &info);
        var viewport = new Viewport(0f, 0f, color.Width, color.Height, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(color.Width, color.Height));
        vk.CmdSetViewport(context.Cmd, 0, 1, &viewport);
        vk.CmdSetScissor(context.Cmd, 0, 1, &scissor);
    }

    /// <summary>Binds the scene and view descriptor sets for graphics.</summary>
    public static void BindGraphics(PassContext context, Pipeline pipeline)
    {
        context.Context.Vk.CmdBindPipeline(context.Cmd, PipelineBindPoint.Graphics, pipeline);
        context.Pipelines.BindSets(context.Cmd, PipelineBindPoint.Graphics, context.SceneSet, context.Target.ViewSet);
    }

    /// <summary>
    /// Draws sub-meshes with the bound pipeline: binds each mesh's vertex/index buffer when it changes, sets the
    /// cull mode per material (double-sided → none) and pushes the instance/material indices.
    /// </summary>
    public static void DrawMeshes(PassContext context, ReadOnlySpan<DrawItem> items, bool forceCullNone = false)
    {
        var vk = context.Context.Vk;
        var cmd = context.Cmd;
        var world = context.World;
        ulong boundGeometry = 0;
        var cull = unchecked((CullModeFlags)uint.MaxValue); // unknown: the first draw sets it
        foreach (ref readonly var item in items)
        {
            var instance = world.InstanceAt(item.Instance);
            var mesh = instance.Mesh;
            var geometry = mesh.Geometry!.Handle;
            if (geometry.Handle != boundGeometry)
            {
                ulong offset = 0;
                vk.CmdBindVertexBuffers(cmd, 0, 1, &geometry, &offset);
                vk.CmdBindIndexBuffer(cmd, geometry, mesh.IndexOffset, IndexType.Uint32);
                boundGeometry = geometry.Handle;
            }

            var wanted = forceCullNone || item.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit;
            if (wanted != cull)
            {
                vk.CmdSetCullMode(cmd, wanted);
                cull = wanted;
            }

            var constants = new GpuDrawConstants
            {
                InstanceIndex = (uint)item.Instance,
                MaterialIndex = item.Material,
                Mode = (instance.Flags & InstanceFlags.Selected) != 0 ? ShaderConstants.DrawModeSelected : ShaderConstants.DrawModeDefault,
                Width = 1f,
                Color = (instance.Flags & InstanceFlags.Selected) != 0 ? context.WireSelectedColor : context.WireColor,
            };
            context.Pipelines.Push(cmd, constants);
            vk.CmdDrawIndexed(cmd, item.IndexCount, 1, item.IndexStart, 0, 0);
        }
    }

    /// <summary>Declares the per-view constants and the scene buffers as read by shaders.</summary>
    public static void DeclareSceneReads(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
        barriers.Use(context.World.MaterialBuffer, BufferUse.ShaderRead);
        barriers.Use(context.World.InstanceBuffer, BufferUse.ShaderRead);
        barriers.Use(context.World.MaterialSlotBuffer, BufferUse.ShaderRead);
    }
}

/// <summary>
/// Depth pre-pass: reverse-Z depth (cleared to 0, GREATER test) and the instance pick id (R32_UINT, 0 = nothing) of
/// every visible opaque and alpha-tested draw. Later passes test against this depth with EQUAL (no overdraw) or
/// read-only; <see cref="IRenderer.Pick"/> reads both images back. Runs in every view mode.
/// </summary>
internal sealed class DepthPrepassPass : RenderPass
{
    public DepthPrepassPass()
        : base("depth pre-pass", RenderStage.DepthPrepass)
    {
    }

    public override bool IsActive(PassContext context) => true;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Depth, ImageUse.DepthAttachment, discard: true);
        barriers.Use(context.Target.PickIds, ImageUse.ColorAttachment, discard: true);
        PassRecording.DeclareSceneReads(context, barriers);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.PickIds, Vector4.Zero, context.Target.Depth, depthClear: true);
        PassRecording.BindGraphics(context, context.Pipelines.PrepassOpaque);
        PassRecording.DrawMeshes(context, context.Draws.Opaque);
        if (context.Draws.Masked.Length > 0)
        {
            context.Context.Vk.CmdBindPipeline(context.Cmd, PipelineBindPoint.Graphics, context.Pipelines.PrepassMask);
            PassRecording.DrawMeshes(context, context.Draws.Masked);
        }

        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>
/// Clustered light assignment (Full view): one work group per cluster of the 16 × 9 × 24 grid tests every light's
/// cone and range against the cluster's view-space bounds and writes the cluster's light list.
/// </summary>
internal sealed class ClusterAssignPass : RenderPass
{
    public ClusterAssignPass()
        : base("light assignment", RenderStage.LightAssignment)
    {
    }

    public override bool IsActive(PassContext context) => context.Shading == ViewShading.Full && context.World.LightCount > 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
        barriers.Use(context.World.LightBuffer, BufferUse.ComputeRead);
        barriers.Use(context.Target.ClusterCounter, BufferUse.ComputeWrite);
        barriers.Use(context.Target.Clusters, BufferUse.ComputeWrite);
        barriers.Use(context.Target.ClusterLights, BufferUse.ComputeWrite);
    }

    public override unsafe void Execute(PassContext context)
    {
        var vk = context.Context.Vk;
        vk.CmdBindPipeline(context.Cmd, PipelineBindPoint.Compute, context.Pipelines.ClusterAssign);
        context.Pipelines.BindSets(context.Cmd, PipelineBindPoint.Compute, context.SceneSet, context.Target.ViewSet);
        vk.CmdDispatch(context.Cmd, ShaderConstants.ClusterCount, 1, 1);
    }
}

/// <summary>
/// Full view, opaque surfaces: forward PBR shading of opaque and alpha-tested draws with the lights of their
/// cluster, EQUAL depth test against the pre-pass, into the HDR image cleared to the background luminance.
/// </summary>
internal sealed class ForwardOpaquePass : RenderPass
{
    public ForwardOpaquePass()
        : base("forward opaque", RenderStage.Opaque)
    {
    }

    public override bool IsActive(PassContext context) => context.Shading == ViewShading.Full;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Hdr, ImageUse.ColorAttachment, discard: true);
        barriers.Use(context.Target.Depth, ImageUse.DepthTestReadOnly);
        PassRecording.DeclareSceneReads(context, barriers);
        barriers.Use(context.World.LightBuffer, BufferUse.FragmentRead);
        barriers.Use(context.Target.Clusters, BufferUse.FragmentRead);
        barriers.Use(context.Target.ClusterLights, BufferUse.FragmentRead);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Hdr, context.Data.Background, context.Target.Depth, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.ForwardOpaque);
        PassRecording.DrawMeshes(context, context.Draws.Opaque);
        PassRecording.DrawMeshes(context, context.Draws.Masked);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>
/// Full view, blended and transmissive surfaces (and reference drawings) back to front with premultiplied alpha,
/// depth-tested against the opaque scene without writing depth.
/// </summary>
internal sealed class ForwardTransparentPass : RenderPass
{
    public ForwardTransparentPass()
        : base("forward transparent", RenderStage.Transparent)
    {
    }

    public override bool IsActive(PassContext context) => context.Shading == ViewShading.Full && context.Draws.Blended.Length > 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Hdr, ImageUse.ColorAttachment);
        barriers.Use(context.Target.Depth, ImageUse.DepthTestReadOnly);
        PassRecording.DeclareSceneReads(context, barriers);
        barriers.Use(context.World.LightBuffer, BufferUse.FragmentRead);
        barriers.Use(context.Target.Clusters, BufferUse.FragmentRead);
        barriers.Use(context.Target.ClusterLights, BufferUse.FragmentRead);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Hdr, null, context.Target.Depth, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.ForwardTransparent);
        PassRecording.DrawMeshes(context, context.Draws.Blended);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>Colorless view: neutral grey Lambert with a camera headlight and a soft ambient over a dark grey background.</summary>
internal sealed class ColorlessPass : RenderPass
{
    public ColorlessPass()
        : base("colorless", RenderStage.Opaque)
    {
    }

    public override bool IsActive(PassContext context) => context.Shading == ViewShading.Colorless;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Hdr, ImageUse.ColorAttachment, discard: true);
        barriers.Use(context.Target.Depth, ImageUse.DepthTestReadOnly);
        PassRecording.DeclareSceneReads(context, barriers);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Hdr, context.Data.Background, context.Target.Depth, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.Colorless);
        PassRecording.DrawMeshes(context, context.Draws.Opaque);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>
/// Wireframe view: every triangle edge of the visible instances (hidden edges included) over a dark grey
/// background — rasterised with polygon mode LINE when the device has <c>fillModeNonSolid</c>, otherwise drawn by the
/// edge shader as anti-aliased screen-space quads from each mesh's unique edge list.
/// </summary>
internal sealed class WireframePass : RenderPass
{
    public WireframePass()
        : base("wireframe", RenderStage.Opaque)
    {
    }

    public override bool IsActive(PassContext context) => context.Shading == ViewShading.Wireframe;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Hdr, ImageUse.ColorAttachment, discard: true);
        PassRecording.DeclareSceneReads(context, barriers);
    }

    public override unsafe void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Hdr, context.Data.Background, null, depthClear: false);
        if (!context.EdgeWireframe)
        {
            PassRecording.BindGraphics(context, context.Pipelines.WireframePolygon);
            PassRecording.DrawMeshes(context, context.Draws.Opaque, forceCullNone: true);
        }
        else
        {
            PassRecording.BindGraphics(context, context.Pipelines.WireframeEdges);
            var vk = context.Context.Vk;
            int last = -1;
            foreach (ref readonly var item in context.Draws.Opaque)
            {
                if (item.Instance == last)
                    continue; // one edge list per mesh, sub-meshes of an instance are consecutive
                last = item.Instance;
                var instance = context.World.InstanceAt(item.Instance);
                var edges = instance.Mesh.Edges;
                if (edges is null || instance.Mesh.EdgeCount == 0)
                    continue;
                var buffer = edges.Handle;
                ulong offset = 0;
                vk.CmdBindVertexBuffers(context.Cmd, 0, 1, &buffer, &offset);
                var constants = new GpuDrawConstants
                {
                    InstanceIndex = (uint)item.Instance,
                    Mode = ShaderConstants.DrawModeDefault,
                    Width = 1f,
                    Color = (instance.Flags & InstanceFlags.Selected) != 0 ? context.WireSelectedColor : context.WireColor,
                };
                context.Pipelines.Push(context.Cmd, constants);
                vk.CmdDraw(context.Cmd, 6, instance.Mesh.EdgeCount, 0, 0);
            }
        }

        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>
/// Display transform: the HDR image through the tone mapper (Full: AgX/ACES/Neutral; Colorless/Wireframe:
/// pass-through) and sRGB encoding into the display image.
/// </summary>
internal sealed class TonemapPass : RenderPass
{
    public TonemapPass()
        : base("tone mapping", RenderStage.DisplayTransform)
    {
    }

    public override bool IsActive(PassContext context) => true;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Hdr, ImageUse.SampledFragment);
        barriers.Use(context.Target.Display, ImageUse.ColorAttachment, discard: true);
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Display, null, null, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.Tonemap);
        context.Pipelines.Push(context.Cmd, new GpuDrawConstants { Mode = context.ToneMode });
        var vk = context.Context.Vk;
        vk.CmdDraw(context.Cmd, 3, 1, 0, 0);
        vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>
/// Infinite ground grid on Z = 0 over the display image, depth-tested against the scene (not in Wireframe, where
/// every edge is visible too).
/// </summary>
internal sealed class GridPass : RenderPass
{
    public GridPass()
        : base("grid", RenderStage.Overlays)
    {
    }

    public override bool IsActive(PassContext context) => context.View.ShowGrid;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Display, ImageUse.ColorAttachment);
        barriers.Use(context.Target.Depth, ImageUse.DepthTestReadOnly);
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Display, null, context.Target.Depth, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.Grid);
        var vk = context.Context.Vk;
        vk.CmdSetDepthTestEnable(context.Cmd, context.Shading != ViewShading.Wireframe);
        vk.CmdDraw(context.Cmd, 3, 1, 0, 0);
        vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>Line segments of <see cref="IRenderWorld.SetLines"/>: anti-aliased screen-space quads, depth-tested, over the display image.</summary>
internal sealed class LinesPass : RenderPass
{
    public LinesPass()
        : base("lines", RenderStage.Overlays)
    {
    }

    public override bool IsActive(PassContext context) => context.World.LineSegmentCount > 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Display, ImageUse.ColorAttachment);
        barriers.Use(context.Target.Depth, ImageUse.DepthTestReadOnly);
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
        barriers.Use(context.World.LineBuffer, BufferUse.VertexInput);
    }

    public override unsafe void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Display, null, context.Target.Depth, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.Lines);
        var vk = context.Context.Vk;
        var buffer = context.World.LineBuffer.Handle;
        ulong offset = 0;
        vk.CmdBindVertexBuffers(context.Cmd, 0, 1, &buffer, &offset);
        vk.CmdDraw(context.Cmd, 6, (uint)context.World.LineSegmentCount, 0, 0);
        vk.CmdEndRendering(context.Cmd);
    }
}
