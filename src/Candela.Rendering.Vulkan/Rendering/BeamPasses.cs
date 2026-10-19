using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Beam tile culling (beam_cull.comp): clears the statistics words, then one work group per tile of
/// <see cref="ShaderConstants.BeamTileSize"/>² half-resolution pixels tests every volumetric beam's truncated cone
/// against the tile's frustum (side planes and the farthest scene depth) and writes the tile's beam mask; the
/// statistics words are copied to the frame slot's read-back buffer (read when the slot comes round again).
/// </summary>
internal sealed class BeamCullPass : RenderPass
{
    public BeamCullPass()
        : base("beam tile culling", RenderStage.Volumetrics)
    {
    }

    public override bool IsActive(PassContext context) => context.BeamsActive && context.Target.BeamCullSet.Handle != 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        barriers.Use(t.FrameUniform, BufferUse.UniformRead);
        barriers.Use(context.World.LightBuffer, BufferUse.ShaderRead);
        barriers.Use(t.Lists.Buffer, BufferUse.ShaderRead);
        barriers.Use(t.Depth, ImageUse.DepthSampledCompute);
        barriers.Use(t.BeamMasks!, BufferUse.TransferDestination);
    }

    public override unsafe void Execute(PassContext context)
    {
        var t = context.Target;
        var vk = context.Context.Vk;
        var cmd = context.Cmd;
        var barriers = context.Barriers;
        var masks = t.BeamMasks!;
        vk.CmdFillBuffer(cmd, masks.Handle, 0, ShaderConstants.BeamMaskHeader * 4ul, 0);
        barriers.Use(masks, BufferUse.ComputeWrite);
        barriers.Flush();
        PassRecording.BindCompute(context, context.Pipelines.BeamCull, t.BeamCullSet);
        vk.CmdDispatch(cmd, context.Data.BeamTilesX, context.Data.BeamTilesY, 1);

        int slot = context.Frame.SlotIndex;
        if (t.BeamStatsReadback[slot] is not { } readback)
            return;
        barriers.Use(masks, BufferUse.TransferSource);
        barriers.Use(readback, BufferUse.TransferDestination);
        barriers.Flush();
        var copy = new BufferCopy(0, 0, ShaderConstants.BeamMaskHeader * 4ul);
        vk.CmdCopyBuffer(cmd, masks.Handle, readback.Handle, 1, &copy);
        barriers.Use(readback, BufferUse.HostRead);
        t.BeamStatsFrame[slot] = context.Frame.Number;
        t.BeamStatsTiles[slot] = context.Data.BeamTilesX * context.Data.BeamTilesY;
    }
}

/// <summary>
/// Per-pixel single scattering of the volumetric beams at half the internal resolution (beam_scatter.comp): the
/// estimate with gobos, blades, shadows and smoke, and the noise-free envelope without them.
/// </summary>
internal sealed class BeamScatterPass : RenderPass
{
    public BeamScatterPass()
        : base("beam single scattering", RenderStage.Volumetrics)
    {
    }

    public override bool IsActive(PassContext context) => context.BeamsActive && context.Target.BeamScatterSet.Handle != 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        PassRecording.DeclareSceneReads(context, barriers);
        PassRecording.DeclareLightingReads(context, barriers, compute: true);
        PassRecording.DeclareMediaReads(context, barriers, compute: true);
        barriers.Use(t.Depth, ImageUse.DepthSampledCompute);
        if (t.FroxelIntegrated is { } volume)
            barriers.Use(volume, ImageUse.SampledCompute);
        barriers.Use(t.BeamMasks!, BufferUse.ComputeRead);
        barriers.Use(context.Shared.BeamTable!, ImageUse.SampledCompute);
        barriers.Use(t.BeamScatter!, ImageUse.StorageCompute, discard: true);
        barriers.Use(t.BeamBase!, ImageUse.StorageCompute, discard: true);
    }

    public override void Execute(PassContext context)
    {
        var image = context.Target.BeamScatter!;
        PassRecording.BindCompute(context, context.Pipelines.BeamScatter, context.Target.BeamScatterSet);
        context.Context.Vk.CmdDispatch(context.Cmd, (image.Width + 7) / 8, (image.Height + 7) / 8, 1);
    }
}

/// <summary>
/// Temporal accumulation of the beam modulation ratio (beam_temporal.comp): reprojected, neighbourhood-clamped
/// history of L / B times the current envelope B, into the resolved beam image and the next history.
/// </summary>
internal sealed class BeamTemporalPass : RenderPass
{
    public BeamTemporalPass()
        : base("beam temporal accumulation", RenderStage.Volumetrics)
    {
    }

    public override bool IsActive(PassContext context) =>
        context.BeamsActive && context.Target.BeamResolved is not null && context.Target.BeamTemporalSet(context.Target.BeamHistoryIndex).Handle != 0;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        var t = context.Target;
        int read = t.BeamHistoryIndex;
        barriers.Use(t.FrameUniform, BufferUse.UniformRead);
        barriers.Use(t.BeamScatter!, ImageUse.SampledCompute);
        barriers.Use(t.BeamBase!, ImageUse.SampledCompute);
        barriers.Use(t.BeamHistory(read)!, ImageUse.SampledCompute);
        barriers.Use(t.BeamHistory(1 - read)!, ImageUse.StorageCompute, discard: true);
        barriers.Use(t.BeamResolved!, ImageUse.StorageCompute, discard: true);
    }

    public override void Execute(PassContext context)
    {
        var t = context.Target;
        var image = t.BeamResolved!;
        PassRecording.BindCompute(context, context.Pipelines.BeamTemporal, t.BeamTemporalSet(t.BeamHistoryIndex));
        context.Context.Vk.CmdDispatch(context.Cmd, (image.Width + 7) / 8, (image.Height + 7) / 8, 1);
        t.BeamHistoryIndex = 1 - t.BeamHistoryIndex;
        t.BeamHistoryValid = true;
        t.BeamHistoryFrames = Math.Min(t.BeamHistoryFrames + 1, 1 << 20);
    }
}
