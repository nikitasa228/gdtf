using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Synchronisation state of one image or buffer between commands: current layout, the last write (stages + access
/// still to be made visible), the stages that read since that write (for write-after-read) and the stages/accesses
/// that the last write has already been made visible to.
/// </summary>
internal struct ResourceState
{
    public ImageLayout Layout;
    public PipelineStageFlags2 WriteStages;
    public AccessFlags2 WriteAccess;
    public PipelineStageFlags2 ReadStages;
    public PipelineStageFlags2 VisibleStages;
    public AccessFlags2 VisibleAccess;
}

/// <summary>How a pass uses an image.</summary>
internal enum ImageUse
{
    /// <summary>Colour attachment (load/clear, blend, store).</summary>
    ColorAttachment,

    /// <summary>Depth attachment written by depth tests.</summary>
    DepthAttachment,

    /// <summary>Depth attachment, test only (no writes).</summary>
    DepthTestReadOnly,

    /// <summary>Depth sampled in a fragment shader (read-only depth layout).</summary>
    DepthSampledFragment,

    /// <summary>Sampled in a fragment shader.</summary>
    SampledFragment,

    /// <summary>Sampled in a compute shader.</summary>
    SampledCompute,

    /// <summary>Source of a copy or blit.</summary>
    TransferSource,

    /// <summary>Destination of a copy, blit or clear.</summary>
    TransferDestination,

    /// <summary>Final state of a target's display image: sampled by fragment shaders of later submissions (ImGui).</summary>
    Display,

    /// <summary>Swapchain image handed to the presentation engine.</summary>
    Present,
}

/// <summary>How a pass uses a buffer.</summary>
internal enum BufferUse
{
    VertexInput,
    IndexInput,
    UniformRead,
    ShaderRead,
    FragmentRead,
    ComputeRead,
    ComputeWrite,
    TransferSource,
    TransferDestination,
    HostRead,
}

/// <summary>
/// Collects the barriers a pass needs from the tracked state of its resources and records them as one
/// <c>vkCmdPipelineBarrier2</c>. Read-after-write, write-after-read, write-after-write and layout transitions are
/// derived from <see cref="ResourceState"/>; reads in stages the last write is already visible to need no barrier.
/// Preallocated: no managed allocation per frame.
/// </summary>
internal sealed unsafe class BarrierBatch
{
    private const int Capacity = 64;
    private readonly ImageMemoryBarrier2[] _images = new ImageMemoryBarrier2[Capacity];
    private readonly BufferMemoryBarrier2[] _buffers = new BufferMemoryBarrier2[Capacity];
    private int _imageCount;
    private int _bufferCount;

    public static void Describe(ImageUse use, out PipelineStageFlags2 stage, out AccessFlags2 access, out ImageLayout layout, out bool write)
    {
        switch (use)
        {
            case ImageUse.ColorAttachment:
                stage = PipelineStageFlags2.ColorAttachmentOutputBit;
                access = AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit;
                layout = ImageLayout.ColorAttachmentOptimal;
                write = true;
                return;
            case ImageUse.DepthAttachment:
                stage = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
                access = AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit;
                layout = ImageLayout.DepthStencilAttachmentOptimal;
                write = true;
                return;
            case ImageUse.DepthTestReadOnly:
                stage = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
                access = AccessFlags2.DepthStencilAttachmentReadBit;
                layout = ImageLayout.DepthStencilReadOnlyOptimal;
                write = false;
                return;
            case ImageUse.DepthSampledFragment:
                stage = PipelineStageFlags2.FragmentShaderBit;
                access = AccessFlags2.ShaderSampledReadBit;
                layout = ImageLayout.DepthStencilReadOnlyOptimal;
                write = false;
                return;
            case ImageUse.SampledFragment:
            case ImageUse.Display:
                stage = PipelineStageFlags2.FragmentShaderBit;
                access = AccessFlags2.ShaderSampledReadBit;
                layout = ImageLayout.ShaderReadOnlyOptimal;
                write = false;
                return;
            case ImageUse.SampledCompute:
                stage = PipelineStageFlags2.ComputeShaderBit;
                access = AccessFlags2.ShaderSampledReadBit;
                layout = ImageLayout.ShaderReadOnlyOptimal;
                write = false;
                return;
            case ImageUse.TransferSource:
                stage = PipelineStageFlags2.CopyBit | PipelineStageFlags2.BlitBit;
                access = AccessFlags2.TransferReadBit;
                layout = ImageLayout.TransferSrcOptimal;
                write = false;
                return;
            case ImageUse.TransferDestination:
                stage = PipelineStageFlags2.CopyBit | PipelineStageFlags2.BlitBit | PipelineStageFlags2.ClearBit;
                access = AccessFlags2.TransferWriteBit;
                layout = ImageLayout.TransferDstOptimal;
                write = true;
                return;
            case ImageUse.Present:
                stage = PipelineStageFlags2.None;
                access = AccessFlags2.None;
                layout = ImageLayout.PresentSrcKhr;
                write = false;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(use));
        }
    }

    public static void Describe(BufferUse use, out PipelineStageFlags2 stage, out AccessFlags2 access, out bool write)
    {
        const PipelineStageFlags2 allShaders = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
                                               PipelineStageFlags2.ComputeShaderBit;
        write = false;
        switch (use)
        {
            case BufferUse.VertexInput:
                stage = PipelineStageFlags2.VertexAttributeInputBit;
                access = AccessFlags2.VertexAttributeReadBit;
                return;
            case BufferUse.IndexInput:
                stage = PipelineStageFlags2.IndexInputBit;
                access = AccessFlags2.IndexReadBit;
                return;
            case BufferUse.UniformRead:
                stage = allShaders;
                access = AccessFlags2.UniformReadBit;
                return;
            case BufferUse.ShaderRead:
                stage = allShaders;
                access = AccessFlags2.ShaderStorageReadBit;
                return;
            case BufferUse.FragmentRead:
                stage = PipelineStageFlags2.FragmentShaderBit;
                access = AccessFlags2.ShaderStorageReadBit;
                return;
            case BufferUse.ComputeRead:
                stage = PipelineStageFlags2.ComputeShaderBit;
                access = AccessFlags2.ShaderStorageReadBit;
                return;
            case BufferUse.ComputeWrite:
                stage = PipelineStageFlags2.ComputeShaderBit;
                access = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit;
                write = true;
                return;
            case BufferUse.TransferSource:
                stage = PipelineStageFlags2.CopyBit;
                access = AccessFlags2.TransferReadBit;
                return;
            case BufferUse.TransferDestination:
                stage = PipelineStageFlags2.CopyBit | PipelineStageFlags2.ClearBit;
                access = AccessFlags2.TransferWriteBit;
                write = true;
                return;
            case BufferUse.HostRead:
                stage = PipelineStageFlags2.HostBit;
                access = AccessFlags2.HostReadBit;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(use));
        }
    }

    /// <summary>Declares that the next commands use <paramref name="image"/> as <paramref name="use"/>.</summary>
    /// <param name="image">The image.</param>
    /// <param name="use">The use.</param>
    /// <param name="discard">Previous contents are not needed (transition from UNDEFINED).</param>
    public void Use(GpuImage image, ImageUse use, bool discard = false) =>
        Use(image.Handle, ref image.State, image.FullRange, use, discard);

    /// <summary>Same as <see cref="Use(GpuImage, ImageUse, bool)"/> for an image tracked outside <see cref="GpuImage"/> (swapchain images).</summary>
    public void Use(Image image, ref ResourceState state, ImageSubresourceRange range, ImageUse use, bool discard = false)
    {
        Describe(use, out var stage, out var access, out var layout, out bool write);
        bool layoutChange = state.Layout != layout || (discard && state.Layout != ImageLayout.Undefined);
        if (write || layoutChange)
        {
            if (layoutChange || state.WriteStages != PipelineStageFlags2.None || state.ReadStages != PipelineStageFlags2.None)
            {
                AddImage(image, range, state.WriteStages | state.ReadStages, state.WriteAccess, stage, access,
                    discard ? ImageLayout.Undefined : state.Layout, layout);
            }

            state.Layout = layout;
            if (write)
            {
                state.WriteStages = stage;
                state.WriteAccess = access & WriteMask;
                state.ReadStages = PipelineStageFlags2.None;
                state.VisibleStages = stage;
                state.VisibleAccess = access;
            }
            else
            {
                // The layout transition is a write that is made available automatically; later readers chain to
                // the stage of this barrier and only need a visibility operation.
                state.WriteStages = stage == PipelineStageFlags2.None ? PipelineStageFlags2.AllCommandsBit : stage;
                state.WriteAccess = AccessFlags2.None;
                state.ReadStages = stage;
                state.VisibleStages = stage;
                state.VisibleAccess = access;
            }

            return;
        }

        if (state.WriteStages != PipelineStageFlags2.None &&
            ((state.VisibleStages & stage) != stage || (state.VisibleAccess & access) != access))
        {
            AddImage(image, range, state.WriteStages, state.WriteAccess, stage, access, layout, layout);
            state.VisibleStages |= stage;
            state.VisibleAccess |= access;
        }

        state.ReadStages |= stage;
    }

    /// <summary>Declares that the next commands use <paramref name="buffer"/> as <paramref name="use"/>.</summary>
    public void Use(GpuBuffer buffer, BufferUse use) => Use(buffer.Handle, ref buffer.State, use);

    public void Use(Buffer buffer, ref ResourceState state, BufferUse use)
    {
        Describe(use, out var stage, out var access, out bool write);
        if (write)
        {
            if (state.WriteStages != PipelineStageFlags2.None || state.ReadStages != PipelineStageFlags2.None)
                AddBuffer(buffer, state.WriteStages | state.ReadStages, state.WriteAccess, stage, access);
            state.WriteStages = stage;
            state.WriteAccess = access & WriteMask;
            state.ReadStages = PipelineStageFlags2.None;
            state.VisibleStages = stage;
            state.VisibleAccess = access;
            return;
        }

        if (state.WriteStages != PipelineStageFlags2.None &&
            ((state.VisibleStages & stage) != stage || (state.VisibleAccess & access) != access))
        {
            AddBuffer(buffer, state.WriteStages, state.WriteAccess, stage, access);
            state.VisibleStages |= stage;
            state.VisibleAccess |= access;
        }

        state.ReadStages |= stage;
    }

    /// <summary>Records the collected barriers (if any) into <paramref name="cmd"/>.</summary>
    public void Flush(Vk vk, CommandBuffer cmd)
    {
        if (_imageCount == 0 && _bufferCount == 0)
            return;
        fixed (ImageMemoryBarrier2* images = _images)
        fixed (BufferMemoryBarrier2* buffers = _buffers)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)_imageCount,
                PImageMemoryBarriers = images,
                BufferMemoryBarrierCount = (uint)_bufferCount,
                PBufferMemoryBarriers = buffers,
            };
            vk.CmdPipelineBarrier2(cmd, &dependency);
        }

        _imageCount = 0;
        _bufferCount = 0;
    }

    private const AccessFlags2 WriteMask = AccessFlags2.ShaderWriteBit | AccessFlags2.ShaderStorageWriteBit |
                                           AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit |
                                           AccessFlags2.TransferWriteBit | AccessFlags2.HostWriteBit | AccessFlags2.MemoryWriteBit;

    private void AddImage(Image image, ImageSubresourceRange range, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage, AccessFlags2 dstAccess, ImageLayout oldLayout, ImageLayout newLayout)
    {
        if (_imageCount == Capacity)
            throw new InvalidOperationException("Too many image barriers in one batch.");
        _images[_imageCount++] = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
    }

    private void AddBuffer(Buffer buffer, PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess)
    {
        if (_bufferCount == Capacity)
            throw new InvalidOperationException("Too many buffer barriers in one batch.");
        _buffers[_bufferCount++] = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = 0,
            Size = Vk.WholeSize,
        };
    }
}
