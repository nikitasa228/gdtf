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

/// <summary>
/// Test hook: writes half-float +infinity into a block of the HDR image after the scene passes and before the bloom and
/// the display transform read it — what a GPU that does not clamp stores when a blend overflows the half-float range
/// (lavapipe clamps, so the overflow itself cannot be produced there). The readers must keep it from turning into NaN
/// (hardware filtering of an infinity) and spreading. Active only for the frame after
/// <see cref="VulkanRenderTarget.HdrOverflowForTests"/> was set (a pixel rectangle of the internal resolution).
/// </summary>
internal sealed class HdrOverflowPass : RenderPass
{
    /// <summary>Half-float +infinity (exponent all ones, mantissa 0).</summary>
    private const ushort HalfInfinity = 0x7C00;

    /// <summary>Half-float 1.0 (alpha: scene light).</summary>
    private const ushort HalfOne = 0x3C00;

    public HdrOverflowPass()
        : base("HDR overflow (diagnostics)", RenderStage.DiagnosticsAfterScene)
    {
    }

    public override bool IsActive(PassContext context) => context.Target.HdrOverflowForTests is not null;

    public override void DeclareResources(PassContext context, BarrierBatch barriers) =>
        barriers.Use(context.Target.Hdr, ImageUse.TransferDestination);

    public override unsafe void Execute(PassContext context)
    {
        var t = context.Target;
        var rect = t.HdrOverflowForTests!.Value;
        t.HdrOverflowForTests = null;
        int x0 = Math.Clamp(rect.X, 0, (int)t.Hdr.Width - 1), y0 = Math.Clamp(rect.Y, 0, (int)t.Hdr.Height - 1);
        int w = Math.Clamp(rect.Width, 1, (int)t.Hdr.Width - x0), h = Math.Clamp(rect.Height, 1, (int)t.Hdr.Height - y0);
        var region = context.Context.Upload.Allocate((ulong)(w * h * 8), 16);
        var texels = new Span<ushort>(region.Pointer, w * h * 4);
        for (int i = 0; i < w * h; i++)
        {
            texels[4 * i] = HalfInfinity;
            texels[4 * i + 1] = HalfInfinity;
            texels[4 * i + 2] = HalfInfinity;
            texels[4 * i + 3] = HalfOne;
        }

        var copy = new BufferImageCopy
        {
            BufferOffset = region.Offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(x0, y0, 0),
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        context.Context.Vk.CmdCopyBufferToImage(context.Cmd, region.Buffer, t.Hdr.Handle, ImageLayout.TransferDstOptimal, 1, &copy);
    }
}
