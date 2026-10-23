using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Test hook: fills every temporal history of the target (froxel volume, the beam history the next temporal pass reads,
/// ray-traced indirect light) with one value — NaN or ±infinity in the tests — before the frame's passes run, the way
/// stale or uninitialised GPU memory may look on another driver. The histories stay marked valid, so the frame really
/// reads them; the readers must reject or sanitise what they find. Active only for the frame after
/// <see cref="VulkanRenderTarget.HistoryFillForTests"/> was set.
/// </summary>
internal sealed class HistoryFillPass : RenderPass
{
    public HistoryFillPass()
        : base("history fill (diagnostics)", RenderStage.Diagnostics)
    {
    }

    public override bool IsActive(PassContext context) => context.Target.HistoryFillForTests is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers)
    {
        foreach (var image in Histories(context.Target))
            barriers.Use(image, ImageUse.TransferDestination, discard: true);
    }

    public override unsafe void Execute(PassContext context)
    {
        var t = context.Target;
        float v = t.HistoryFillForTests ?? 0f;
        t.HistoryFillForTests = null;
        var value = new ClearColorValue(v, v, v, v);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        foreach (var image in Histories(t))
            context.Context.Vk.CmdClearColorImage(context.Cmd, image.Handle, ImageLayout.TransferDstOptimal, &value, 1, &range);
    }

    private static IEnumerable<GpuImage> Histories(VulkanRenderTarget t)
    {
        if (t.FroxelHistory is { } froxels)
            yield return froxels;
        if (t.BeamHistory(t.BeamHistoryIndex) is { } beams)
            yield return beams;
        if (t.IndirectHistory is { } indirect)
            yield return indirect;
    }
}
