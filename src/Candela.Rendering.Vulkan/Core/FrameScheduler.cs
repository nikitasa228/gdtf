using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>GPU time of one named scope of a completed frame.</summary>
/// <param name="Name">Scope name (render pass name).</param>
/// <param name="Milliseconds">GPU time between the scope's begin and end timestamps.</param>
public readonly record struct GpuTiming(string Name, double Milliseconds);

/// <summary>
/// One frame in flight: a command buffer recorded by the renderer (and, in the editor, by the ImGui backend) and
/// submitted once. Obtained from <see cref="VulkanContext.BeginFrame"/>; valid until
/// <see cref="VulkanContext.SubmitFrame(VulkanFrame)"/>. The same object is reused for every
/// <see cref="VulkanContext.FramesInFlight"/>-th frame.
/// </summary>
public sealed class VulkanFrame
{
    internal const uint MaxTimestamps = 128;
    private const int MaxScopes = (int)MaxTimestamps / 2;

    internal VulkanFrame(int slotIndex)
    {
        SlotIndex = slotIndex;
    }

    /// <summary>Index of the frame-in-flight slot (0 … FramesInFlight − 1); selects per-slot resources.</summary>
    public int SlotIndex { get; }

    /// <summary>Number of the frame (1-based, increases by one per <see cref="VulkanContext.BeginFrame"/>).</summary>
    public ulong Number { get; internal set; }

    /// <summary>The primary command buffer being recorded.</summary>
    public CommandBuffer CommandBuffer { get; internal set; }

    internal CommandPool Pool;
    internal Fence Fence;
    internal QueryPool TimestampPool;
    internal bool Submitted;
    internal (uint Generation, ulong Head) UploadMark;
    internal uint TimestampCount;
    internal readonly ulong[] TimestampValues = new ulong[MaxTimestamps];
    internal readonly (string Name, uint Begin, uint End)[] Scopes = new (string, uint, uint)[MaxScopes];
    internal int ScopeCount;
    internal bool TimingsCollected = true;
}

/// <summary>
/// Frames in flight: <see cref="Begin"/> waits for the slot's previous submission (never for the whole device),
/// collects its GPU timings, releases staging space and destroys retired objects of completed frames;
/// <see cref="Submit"/> ends the command buffer and submits it with the slot's fence (plus optional semaphores for
/// swapchain acquire/present). Main thread only.
/// </summary>
internal sealed unsafe class FrameScheduler : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanFrame[] _slots = new VulkanFrame[VulkanContext.FramesInFlight];
    private readonly GpuTiming[] _lastTimings = new GpuTiming[VulkanFrame.MaxTimestamps / 2];
    private int _lastTimingCount;
    private ulong _lastTimingFrame;

    public FrameScheduler(VulkanContext context)
    {
        _context = context;
        var vk = context.Vk;
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = new VulkanFrame(i);
            _slots[i] = slot;
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
            CommandBuffer cmd;
            VkUtil.Check(vk.AllocateCommandBuffers(context.Device, &allocInfo, &cmd), "vkAllocateCommandBuffers");
            slot.CommandBuffer = cmd;
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VkUtil.Check(vk.CreateFence(context.Device, &fenceInfo, null, out slot.Fence), "vkCreateFence");
            if (context.Capabilities.Timestamps)
            {
                var queryInfo = new QueryPoolCreateInfo
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Timestamp,
                    QueryCount = VulkanFrame.MaxTimestamps,
                };
                VkUtil.Check(vk.CreateQueryPool(context.Device, &queryInfo, null, out slot.TimestampPool), "vkCreateQueryPool");
            }

            context.SetDebugName(ObjectType.CommandBuffer, (ulong)cmd.Handle, $"frame slot {i}");
        }
    }

    /// <summary>The frame being recorded, or null between frames.</summary>
    public VulkanFrame? Current { get; private set; }

    /// <summary>GPU scope timings of the most recently completed frame that recorded any.</summary>
    public ReadOnlySpan<GpuTiming> LastTimings => _lastTimings.AsSpan(0, _lastTimingCount);

    /// <summary>Number of the frame <see cref="LastTimings"/> belong to (0 = none yet).</summary>
    public ulong LastTimingFrame => _lastTimingFrame;

    /// <summary>Begins the next frame: waits for its slot, recycles resources, resets and begins the command buffer.</summary>
    public VulkanFrame Begin()
    {
        if (Current is not null)
            throw new InvalidOperationException("A frame is already being recorded; submit it first.");
        var vk = _context.Vk;
        ulong number = _context.AdvanceFrameNumber();
        var slot = _slots[(int)((number - 1) % VulkanContext.FramesInFlight)];
        if (slot.Submitted)
        {
            var fence = slot.Fence;
            VkUtil.Check(vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            VkUtil.Check(vk.ResetFences(_context.Device, 1, &fence), "vkResetFences");
            slot.Submitted = false;
            CollectTimings(slot);
            _context.Upload.Release(slot.UploadMark);
        }

        _context.Deletion.Collect(number);
        VkUtil.Check(vk.ResetCommandPool(_context.Device, slot.Pool, 0), "vkResetCommandPool");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VkUtil.Check(vk.BeginCommandBuffer(slot.CommandBuffer, &begin), "vkBeginCommandBuffer");
        if (slot.TimestampPool.Handle != 0)
            vk.CmdResetQueryPool(slot.CommandBuffer, slot.TimestampPool, 0, VulkanFrame.MaxTimestamps);
        slot.TimestampCount = 0;
        slot.ScopeCount = 0;
        slot.TimingsCollected = false;
        slot.Number = number;
        Current = slot;
        return slot;
    }

    /// <summary>
    /// Writes a timestamp after all previous commands reach <paramref name="stage"/>. Returns its index, or
    /// <see cref="uint.MaxValue"/> when timestamps are unsupported or the frame's query pool is full.
    /// </summary>
    public uint WriteTimestamp(VulkanFrame frame, PipelineStageFlags2 stage)
    {
        if (frame.TimestampPool.Handle == 0 || frame.TimestampCount >= VulkanFrame.MaxTimestamps)
            return uint.MaxValue;
        uint index = frame.TimestampCount++;
        _context.Vk.CmdWriteTimestamp2(frame.CommandBuffer, stage, frame.TimestampPool, index);
        return index;
    }

    /// <summary>Records a named scope between two timestamps (reported in <see cref="LastTimings"/> once complete).</summary>
    public void AddScope(VulkanFrame frame, string name, uint begin, uint end)
    {
        if (begin == uint.MaxValue || end == uint.MaxValue || frame.ScopeCount >= frame.Scopes.Length)
            return;
        frame.Scopes[frame.ScopeCount++] = (name, begin, end);
    }

    /// <summary>Ends and submits the current frame's command buffer, optionally waiting on/signalling semaphores.</summary>
    public void Submit(VulkanFrame frame, ReadOnlySpan<SemaphoreSubmitInfo> waits, ReadOnlySpan<SemaphoreSubmitInfo> signals)
    {
        if (!ReferenceEquals(frame, Current))
            throw new InvalidOperationException("Submitting a frame that is not being recorded.");
        var vk = _context.Vk;
        Current = null;
        VkUtil.Check(vk.EndCommandBuffer(frame.CommandBuffer), "vkEndCommandBuffer");
        frame.UploadMark = _context.Upload.Mark;
        var cmdInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = frame.CommandBuffer };
        fixed (SemaphoreSubmitInfo* pWaits = waits)
        fixed (SemaphoreSubmitInfo* pSignals = signals)
        {
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &cmdInfo,
                WaitSemaphoreInfoCount = (uint)waits.Length,
                PWaitSemaphoreInfos = pWaits,
                SignalSemaphoreInfoCount = (uint)signals.Length,
                PSignalSemaphoreInfos = pSignals,
            };
            VkUtil.Check(vk.QueueSubmit2(_context.Queue, 1, &submit, frame.Fence), "vkQueueSubmit2");
        }

        frame.Submitted = true;
    }

    /// <summary>Waits until every submitted frame has completed (blocking readbacks, shutdown) and collects timings.</summary>
    public void WaitAll()
    {
        var vk = _context.Vk;
        VulkanFrame? latest = null;
        foreach (var slot in _slots)
        {
            if (!slot.Submitted)
                continue;
            var fence = slot.Fence;
            VkUtil.Check(vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            if (latest is null || slot.Number > latest.Number)
                latest = slot;
        }

        // Older slot first so the newest frame's timings win.
        foreach (var slot in _slots)
        {
            if (slot.Submitted && !ReferenceEquals(slot, latest))
                CollectTimings(slot);
        }

        if (latest is not null)
            CollectTimings(latest);
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

    private void CollectTimings(VulkanFrame slot)
    {
        if (slot.TimingsCollected)
            return;
        slot.TimingsCollected = true;
        if (slot.TimestampPool.Handle == 0 || slot.TimestampCount == 0 || slot.ScopeCount == 0)
            return;
        fixed (ulong* values = slot.TimestampValues)
        {
            // The fence has signalled, so every written query is available; no WAIT flag needed.
            var result = _context.Vk.GetQueryPoolResults(_context.Device, slot.TimestampPool, 0, slot.TimestampCount,
                (nuint)(slot.TimestampCount * sizeof(ulong)), values, sizeof(ulong), QueryResultFlags.Result64Bit);
            if (result != Result.Success)
                return;
        }

        double period = _context.Capabilities.TimestampPeriod * 1e-6; // ns per tick → ms per tick
        int n = 0;
        for (int i = 0; i < slot.ScopeCount; i++)
        {
            var (name, begin, end) = slot.Scopes[i];
            ulong ticks = slot.TimestampValues[end] - slot.TimestampValues[begin];
            _lastTimings[n++] = new GpuTiming(name, ticks * period);
        }

        _lastTimingCount = n;
        _lastTimingFrame = slot.Number;
    }
}

/// <summary>
/// One-off command buffer submitted and waited for immediately: blocking readbacks (ReadPixels, Pick) and other
/// rare operations. Never used while a frame is recorded, so tracked resource states stay in submission order.
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
            throw new InvalidOperationException("Blocking GPU operations are not allowed while a frame is recorded; submit the frame first.");
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
