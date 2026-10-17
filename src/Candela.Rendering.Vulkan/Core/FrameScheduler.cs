using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>Per-frame-in-flight resources: a command pool and buffer, a fence and a timestamp query pool.</summary>
internal sealed class FrameSlot
{
    public FrameSlot(int index) => Index = index;

    public int Index { get; }
    public CommandPool Pool;
    public CommandBuffer CommandBuffer;
    public Fence Fence;
    public QueryPool TimestampPool;
    public bool Submitted;
    public (uint Generation, ulong Head) UploadMark;

    /// <summary>Number of the frame recorded in this slot (1-based, see <see cref="VulkanContext.FrameNumber"/>).</summary>
    public ulong FrameNumber;

    /// <summary>Timestamps written by the last submission (read back when the slot is reused).</summary>
    public int TimestampsWritten;
}

/// <summary>
/// Frames in flight: <see cref="Begin"/> waits for the slot's previous submission (never for the whole device),
/// then releases staging space and destroys retired objects of completed frames; <see cref="Submit"/> ends the
/// command buffer and submits it with the slot's fence. Main thread only.
/// </summary>
internal sealed unsafe class FrameScheduler : IDisposable
{
    public const uint MaxTimestamps = 64;
    private readonly VulkanContext _context;
    private readonly FrameSlot[] _slots = new FrameSlot[VulkanContext.FramesInFlight];

    public FrameScheduler(VulkanContext context)
    {
        _context = context;
        var vk = context.Vk;
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = new FrameSlot(i);
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = context.QueueFamilyIndex,
            };
            VkUtil.Check(vk.CreateCommandPool(context.Device, &poolInfo, null, out slot.Pool), "vkCreateCommandPool");
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = slot.Pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            VkUtil.Check(vk.AllocateCommandBuffers(context.Device, &allocInfo, out slot.CommandBuffer), "vkAllocateCommandBuffers");
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VkUtil.Check(vk.CreateFence(context.Device, &fenceInfo, null, out slot.Fence), "vkCreateFence");
            if (context.Capabilities.Timestamps)
            {
                var queryInfo = new QueryPoolCreateInfo
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Timestamp,
                    QueryCount = MaxTimestamps,
                };
                VkUtil.Check(vk.CreateQueryPool(context.Device, &queryInfo, null, out slot.TimestampPool), "vkCreateQueryPool");
            }

            context.SetDebugName(ObjectType.CommandBuffer, (ulong)slot.CommandBuffer.Handle, $"frame slot {i}");
            _slots[i] = slot;
        }
    }

    /// <summary>The slot being recorded, or null between frames.</summary>
    public FrameSlot? Current { get; private set; }

    /// <summary>Begins the next frame: waits for its slot, recycles resources, resets and begins the command buffer.</summary>
    public FrameSlot Begin()
    {
        if (Current is not null)
            throw new InvalidOperationException("A frame is already being recorded.");
        var vk = _context.Vk;
        ulong frame = _context.AdvanceFrameNumber();
        var slot = _slots[(int)((frame - 1) % VulkanContext.FramesInFlight)];
        if (slot.Submitted)
        {
            var fence = slot.Fence;
            VkUtil.Check(vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            VkUtil.Check(vk.ResetFences(_context.Device, 1, &fence), "vkResetFences");
            slot.Submitted = false;
            _context.Upload.Release(slot.UploadMark);
        }

        _context.Deletion.Collect(frame);
        VkUtil.Check(vk.ResetCommandPool(_context.Device, slot.Pool, 0), "vkResetCommandPool");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VkUtil.Check(vk.BeginCommandBuffer(slot.CommandBuffer, &begin), "vkBeginCommandBuffer");
        slot.FrameNumber = frame;
        Current = slot;
        return slot;
    }

    /// <summary>Ends and submits the current frame's command buffer.</summary>
    public void Submit(FrameSlot slot)
    {
        if (!ReferenceEquals(slot, Current))
            throw new InvalidOperationException("Submitting a frame that is not being recorded.");
        var vk = _context.Vk;
        Current = null;
        VkUtil.Check(vk.EndCommandBuffer(slot.CommandBuffer), "vkEndCommandBuffer");
        slot.UploadMark = _context.Upload.Mark;
        var cmdInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = slot.CommandBuffer };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &cmdInfo,
        };
        VkUtil.Check(vk.QueueSubmit2(_context.Queue, 1, &submit, slot.Fence), "vkQueueSubmit2");
        slot.Submitted = true;
    }

    /// <summary>Waits until every submitted frame has completed (used before blocking readbacks and on shutdown).</summary>
    public void WaitAll()
    {
        var vk = _context.Vk;
        foreach (var slot in _slots)
        {
            if (!slot.Submitted)
                continue;
            var fence = slot.Fence;
            VkUtil.Check(vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        }
    }

    public void Dispose()
    {
        var vk = _context.Vk;
        foreach (var slot in _slots)
        {
            if (slot is null)
                continue;
            if (slot.TimestampPool.Handle != 0)
                vk.DestroyQueryPool(_context.Device, slot.TimestampPool, null);
            if (slot.Fence.Handle != 0)
                vk.DestroyFence(_context.Device, slot.Fence, null);
            if (slot.Pool.Handle != 0)
                vk.DestroyCommandPool(_context.Device, slot.Pool, null);
        }
    }
}

/// <summary>
/// One-off command buffer submitted and waited for immediately: initial image transitions and blocking readbacks
/// (ReadPixels, Pick). Never used while a frame is recorded, so tracked resource states stay in submission order.
/// </summary>
internal sealed unsafe class ImmediateSubmitter : IDisposable
{
    private readonly VulkanContext _context;
    private readonly CommandPool _pool;
    private readonly CommandBuffer _cmd;
    private readonly Fence _fence;
    private bool _recording;

    public ImmediateSubmitter(VulkanContext context)
    {
        _context = context;
        var vk = context.Vk;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = context.QueueFamilyIndex,
        };
        VkUtil.Check(vk.CreateCommandPool(context.Device, &poolInfo, null, out _pool), "vkCreateCommandPool");
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        VkUtil.Check(vk.AllocateCommandBuffers(context.Device, &allocInfo, out _cmd), "vkAllocateCommandBuffers");
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        VkUtil.Check(vk.CreateFence(context.Device, &fenceInfo, null, out _fence), "vkCreateFence");
    }

    public CommandBuffer Begin()
    {
        if (_recording)
            throw new InvalidOperationException("Immediate command buffer already recording.");
        if (_context.Frames.Current is not null)
            throw new InvalidOperationException("Immediate submissions are not allowed while a frame is recorded.");
        var vk = _context.Vk;
        VkUtil.Check(vk.ResetCommandPool(_context.Device, _pool, 0), "vkResetCommandPool");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VkUtil.Check(vk.BeginCommandBuffer(_cmd, &begin), "vkBeginCommandBuffer");
        _recording = true;
        return _cmd;
    }

    public void SubmitAndWait()
    {
        if (!_recording)
            throw new InvalidOperationException("Immediate command buffer is not recording.");
        _recording = false;
        var vk = _context.Vk;
        VkUtil.Check(vk.EndCommandBuffer(_cmd), "vkEndCommandBuffer");
        var cmdInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = _cmd };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &cmdInfo,
        };
        var fence = _fence;
        VkUtil.Check(vk.QueueSubmit2(_context.Queue, 1, &submit, fence), "vkQueueSubmit2");
        VkUtil.Check(vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        VkUtil.Check(vk.ResetFences(_context.Device, 1, &fence), "vkResetFences");
    }

    public void Dispose()
    {
        var vk = _context.Vk;
        vk.DestroyFence(_context.Device, _fence, null);
        vk.DestroyCommandPool(_context.Device, _pool, null);
    }
}
