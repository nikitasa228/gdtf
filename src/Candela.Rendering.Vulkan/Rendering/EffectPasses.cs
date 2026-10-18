using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Froxel injection (froxel_inject.comp): source term and extinction of every froxel from the haze, smoke, flame and
/// the cluster lights (shadowed, cookie-masked), blended with the reprojected history when temporal accumulation is on.
/// </summary>
internal sealed class FroxelInjectPass : RenderPass
{
    public FroxelInjectPass()
        : base("froxel injection", RenderStage.Volumetrics)
    {
    }

    public override bool IsActive(PassContext context) => context.VolumetricsActive && context.Target.FroxelInjected is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        PassRecording.DeclareSceneReads(context, barriers);
        if (context.LightsActive)
            PassRecording.DeclareLightingReads(context, barriers, compute: true);
        PassRecording.DeclareMediaReads(context, barriers, compute: true);
        barriers.Use(t.FroxelInjected!, ImageUse.StorageCompute, discard: true);
        barriers.Use(t.FroxelHistory!, ImageUse.SampledCompute);
    }

    public override void Execute(PassContext context)
    {
        var v = context.Target.FroxelInjected!;
        PassRecording.BindCompute(context, context.Pipelines.FroxelInject, context.Target.InjectSet);
        context.Context.Vk.CmdDispatch(context.Cmd, (v.Width + 3) / 4, (v.Height + 3) / 4, (v.Depth + 3) / 4);
    }
}

/// <summary>
/// Front-to-back integration of the froxel volume (froxel_integrate.comp); the injected volume is then copied to the
/// history for the next frame's reprojection.
/// </summary>
internal sealed class FroxelIntegratePass : RenderPass
{
    public FroxelIntegratePass()
        : base("froxel integration", RenderStage.Volumetrics)
    {
    }

    public override bool IsActive(PassContext context) => context.VolumetricsActive && context.Target.FroxelInjected is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        barriers.Use(t.FrameUniform, BufferUse.UniformRead);
        barriers.Use(t.FroxelInjected!, ImageUse.SampledCompute);
        barriers.Use(t.FroxelIntegrated!, ImageUse.StorageCompute, discard: true);
    }

    public override unsafe void Execute(PassContext context)
    {
        var t = context.Target;
        var v = t.FroxelInjected!;
        var vk = context.Context.Vk;
        PassRecording.BindCompute(context, context.Pipelines.FroxelIntegrate, t.IntegrateSet);
        vk.CmdDispatch(context.Cmd, (v.Width + 7) / 8, (v.Height + 7) / 8, 1);

        if (!context.Settings.TemporalAccumulation)
            return;
        var barriers = context.Barriers;
        barriers.Use(v, ImageUse.TransferSource);
        barriers.Use(t.FroxelHistory!, ImageUse.TransferDestination, discard: true);
        barriers.Flush();
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(v.Width, v.Height, v.Depth),
        };
        vk.CmdCopyImage(context.Cmd, v.Handle, ImageLayout.TransferSrcOptimal, t.FroxelHistory!.Handle, ImageLayout.TransferDstOptimal, 1, &copy);
        t.VolumeHistoryValid = true;
    }
}

/// <summary>
/// Volumetric composite over the opaque scene (volumetric.frag): froxel in-scattering and transmittance at each
/// pixel's depth plus the full-resolution ray-marched narrow beams.
/// </summary>
internal sealed class VolumeCompositePass : RenderPass
{
    public VolumeCompositePass()
        : base("volumetric composite", RenderStage.Volumetrics)
    {
    }

    public override bool IsActive(PassContext context) => context.VolumetricsActive && context.Shading == ViewShading.Full ||
                                                         context.VolumetricsActive && context.Target.FroxelIntegrated is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        barriers.Use(t.Hdr, ImageUse.ColorAttachment);
        barriers.Use(t.Depth, ImageUse.DepthSampledFragment);
        PassRecording.DeclareSceneReads(context, barriers);
        if (context.LightsActive)
            PassRecording.DeclareLightingReads(context, barriers, compute: false);
        PassRecording.DeclareMediaReads(context, barriers, compute: false);
        if (t.FroxelIntegrated is { } volume)
            barriers.Use(volume, ImageUse.SampledFragment);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Hdr, null, null, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.VolumeComposite);
        PassRecording.DrawFullscreen(context);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>
/// Physically based bloom (bloom_down.comp / bloom_up.comp): a half-resolution pyramid of normalised 13-tap
/// downsamples of the HDR image, then tent-filtered upsampling that accumulates every level into level 0. The display
/// transform mixes (1 − k)·scene + k·bloom/levels with k = <see cref="PreviewSettings.BloomIntensity"/>.
/// </summary>
internal sealed class BloomPass : RenderPass
{
    public BloomPass()
        : base("bloom", RenderStage.HdrPost)
    {
    }

    public override bool IsActive(PassContext context) => context.BloomActive && context.Target.BloomLevels > 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
        barriers.Use(context.Target.Hdr, ImageUse.SampledCompute);
    }

    public override void Execute(PassContext context)
    {
        var t = context.Target;
        var vk = context.Context.Vk;
        var barriers = context.Barriers;
        int levels = t.BloomLevels;
        for (int i = 0; i < levels; i++)
        {
            var level = t.BloomLevel(i);
            if (i > 0)
                barriers.Use(t.BloomLevel(i - 1), ImageUse.SampledCompute);
            barriers.Use(level, ImageUse.StorageCompute, discard: true);
            barriers.Flush();
            PassRecording.BindCompute(context, i == 0 ? context.Pipelines.BloomDownsampleFirst : context.Pipelines.BloomDownsample, t.BloomDownSet(i));
            vk.CmdDispatch(context.Cmd, (level.Width + 7) / 8, (level.Height + 7) / 8, 1);
        }

        for (int i = levels - 2; i >= 0; i--)
        {
            var level = t.BloomLevel(i);
            barriers.Use(t.BloomLevel(i + 1), ImageUse.SampledCompute);
            barriers.Use(level, ImageUse.StorageCompute);
            barriers.Flush();
            PassRecording.BindCompute(context, context.Pipelines.BloomUpsample, t.BloomUpSet(i));
            vk.CmdDispatch(context.Cmd, (level.Width + 7) / 8, (level.Height + 7) / 8, 1);
        }
    }
}

/// <summary>
/// Auto exposure measurement (histogram.comp, exposure.comp): log-luminance histogram of the frame and its trimmed
/// mean, copied to a host-visible buffer of the frame slot; the renderer reads it when the slot comes round again and
/// adapts the exposure over time.
/// </summary>
internal sealed class ExposurePass : RenderPass
{
    public ExposurePass()
        : base("auto exposure", RenderStage.HdrPost)
    {
    }

    public override bool IsActive(PassContext context) => context.AutoExposureActive && context.Target.Exposure is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
        barriers.Use(context.Target.Hdr, ImageUse.SampledCompute);
        barriers.Use(context.Target.Exposure!, BufferUse.TransferDestination);
    }

    public override unsafe void Execute(PassContext context)
    {
        var t = context.Target;
        var vk = context.Context.Vk;
        var cmd = context.Cmd;
        var barriers = context.Barriers;
        var exposure = t.Exposure!;
        vk.CmdFillBuffer(cmd, exposure.Handle, 0, Vk.WholeSize, 0);
        barriers.Use(exposure, BufferUse.ComputeWrite);
        barriers.Flush();
        PassRecording.BindCompute(context, context.Pipelines.Histogram);
        vk.CmdDispatch(cmd, (uint)(t.RenderWidth + 15) / 16, (uint)(t.RenderHeight + 15) / 16, 1);
        barriers.Use(exposure, BufferUse.ComputeWrite);
        barriers.Flush();
        PassRecording.BindCompute(context, context.Pipelines.ExposureReduce);
        vk.CmdDispatch(cmd, 1, 1, 1);

        int slot = context.Frame.SlotIndex;
        var readback = t.ExposureReadback[slot]!;
        barriers.Use(exposure, BufferUse.TransferSource);
        barriers.Use(readback, BufferUse.TransferDestination);
        barriers.Flush();
        var copy = new BufferCopy(ShaderConstants.HistogramBins * 4ul, 0, (ulong)sizeof(GpuExposureResult));
        vk.CmdCopyBuffer(cmd, exposure.Handle, readback.Handle, 1, &copy);
        barriers.Use(readback, BufferUse.HostRead);
        t.ExposureReadbackFrame[slot] = context.Frame.Number;
    }
}

/// <summary>Overlay depth at display resolution (depth_upsample.frag) when the scene is rendered at a reduced resolution.</summary>
internal sealed class OverlayDepthPass : RenderPass
{
    public OverlayDepthPass()
        : base("overlay depth", RenderStage.OverlayDepth)
    {
    }

    public override bool IsActive(PassContext context) => context.Target.DisplayDepth is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
        barriers.Use(context.Target.DisplayDepth!, ImageUse.DepthAttachment, discard: true);
        barriers.Use(context.Target.Depth, ImageUse.DepthSampledFragment);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, default, default, default, 0, context.Target.DisplayDepth, depthClear: false, depthWrite: true, depthLoad: false);
        PassRecording.BindGraphics(context, context.Pipelines.DepthUpsample);
        PassRecording.DrawFullscreen(context);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>
/// Selection outline, step 1 (selection.frag): every selected instance into the display-resolution mask with its
/// coverage and visibility against the scene depth.
/// </summary>
internal sealed class SelectionMaskPass : RenderPass
{
    public SelectionMaskPass()
        : base("selection mask", RenderStage.Overlays)
    {
    }

    public override bool IsActive(PassContext context) => context.SelectionActive && context.Target.SelectionMask is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.SelectionMask!, ImageUse.ColorAttachment, discard: true);
        barriers.Use(context.Target.Depth, ImageUse.DepthSampledFragment);
        PassRecording.DeclareSceneReads(context, barriers);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.SelectionMask!, Vector4.Zero, null, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.SelectionMask);
        PassRecording.DrawMeshes(context, context.Draws.Opaque, forceCullNone: true, selectedOnly: true);
        PassRecording.DrawMeshes(context, context.Draws.Masked, forceCullNone: true, selectedOnly: true);
        PassRecording.DrawMeshes(context, context.Draws.Blended, forceCullNone: true, selectedOnly: true);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}

/// <summary>Selection outline, step 2 (outline.frag): the ~2 px outline in the selection colour over the display image.</summary>
internal sealed class OutlinePass : RenderPass
{
    public OutlinePass()
        : base("selection outline", RenderStage.Overlays)
    {
    }

    public override bool IsActive(PassContext context) => context.SelectionActive && context.Target.SelectionMask is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        barriers.Use(context.Target.Display, ImageUse.ColorAttachment);
        barriers.Use(context.Target.SelectionMask!, ImageUse.SampledFragment);
        barriers.Use(context.Target.FrameUniform, BufferUse.UniformRead);
    }

    public override void Execute(PassContext context)
    {
        PassRecording.Begin(context, context.Target.Display, null, null, depthClear: false);
        PassRecording.BindGraphics(context, context.Pipelines.Outline);
        PassRecording.DrawFullscreen(context);
        context.Context.Vk.CmdEndRendering(context.Cmd);
    }
}
