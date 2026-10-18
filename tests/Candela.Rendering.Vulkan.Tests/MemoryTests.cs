using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>The device-memory sub-allocator: alignment, free-list reuse and coalescing, granularity, dedicated allocations.</summary>
public sealed unsafe class MemoryTests
{
    [Fact]
    public void BlockHonoursAlignmentAndCoalescesFreedRanges()
    {
        var block = new MemoryBlock(default, 1 << 20, 0, ResourceKind.Linear, null);
        Assert.True(block.TryAllocate(100, 1, out ulong a));
        Assert.True(block.TryAllocate(1000, 256, out ulong b));
        Assert.True(block.TryAllocate(64, 4096, out ulong c));
        Assert.Equal(0ul, a);
        Assert.Equal(0ul, b % 256);
        Assert.Equal(0ul, c % 4096);
        Assert.True(b >= a + 100 && c >= b + 1000);
        Assert.Equal(3, block.AllocationCount);
        Assert.Equal(1164ul, block.UsedBytes);

        // Free-list reuse: the first fitting range is taken again.
        block.Free(b, 1000);
        Assert.True(block.TryAllocate(900, 256, out ulong reused));
        Assert.Equal(b, reused);
        block.Free(reused, 900);

        // Freeing everything coalesces back to one range covering the block.
        block.Free(a, 100);
        block.Free(c, 64);
        Assert.True(block.IsEmpty);
        Assert.Equal(0ul, block.UsedBytes);
        var range = Assert.Single(block.FreeRanges);
        Assert.Equal((0ul, 1ul << 20), range);

        // Corrupt frees are detected.
        Assert.True(block.TryAllocate(128, 16, out ulong d));
        block.Free(d, 128);
        Assert.Throws<InvalidOperationException>(() => block.Free(d, 128));
        Assert.False(block.TryAllocate(2 << 20, 1, out _));
    }

    [SkippableFact]
    public void AllocatorSubAllocatesReusesAndDedicatesLargeRequests()
    {
        var context = TestVulkan.CreateContextOrSkip();
        try
        {
            var allocator = context.Allocator;
            var before = allocator.Statistics;
            uint allTypes = ~0u;

            var small = new List<MemoryAllocation>();
            for (int i = 0; i < 16; i++)
            {
                var requirements = new MemoryRequirements { Size = 1000 + (ulong)i * 37, Alignment = 256, MemoryTypeBits = allTypes };
                var allocation = allocator.Allocate(requirements, MemoryUsage.GpuOnly, ResourceKind.Linear);
                Assert.Equal(0ul, allocation.Offset % 256);
                Assert.False(allocation.IsDedicated);
                small.Add(allocation);
            }

            // No two live allocations overlap within a memory object.
            foreach (var x in small)
            {
                foreach (var y in small)
                {
                    if (x.Offset == y.Offset && x.Memory.Handle == y.Memory.Handle)
                        continue;
                    if (x.Memory.Handle == y.Memory.Handle)
                        Assert.True(x.Offset + x.Size <= y.Offset || y.Offset + y.Size <= x.Offset);
                }
            }

            // A freed range is handed out again.
            var freed = small[5];
            allocator.Free(freed);
            var again = allocator.Allocate(new MemoryRequirements { Size = freed.Size, Alignment = 256, MemoryTypeBits = allTypes },
                MemoryUsage.GpuOnly, ResourceKind.Linear);
            Assert.Equal((freed.Memory.Handle, freed.Offset), (again.Memory.Handle, again.Offset));
            small[5] = again;

            // Larger than half a block: a dedicated VkDeviceMemory.
            var large = allocator.Allocate(new MemoryRequirements { Size = allocator.DeviceLocalBlockSize, Alignment = 256, MemoryTypeBits = allTypes },
                MemoryUsage.GpuOnly, ResourceKind.Linear);
            Assert.True(large.IsDedicated);
            Assert.Equal(before.DedicatedCount + 1, allocator.Statistics.DedicatedCount);

            // Host-visible staging memory is mapped persistently.
            var upload = allocator.Allocate(new MemoryRequirements { Size = 4096, Alignment = 64, MemoryTypeBits = allTypes },
                MemoryUsage.Upload, ResourceKind.Linear);
            Assert.True(upload.MappedPointer != null);
            upload.MappedPointer[0] = 42;
            upload.MappedPointer[4095] = 7;

            // Images and buffers never share a block when bufferImageGranularity matters.
            var optimal = allocator.Allocate(new MemoryRequirements { Size = 4096, Alignment = 4096, MemoryTypeBits = allTypes },
                MemoryUsage.GpuOnly, ResourceKind.Optimal);
            if (context.Capabilities.BufferImageGranularity > 1)
                Assert.DoesNotContain(small, s => s.Memory.Handle == optimal.Memory.Handle);

            foreach (var s in small)
                allocator.Free(s);
            allocator.Free(large);
            allocator.Free(upload);
            allocator.Free(optimal);
            var after = allocator.Statistics;
            Assert.Equal(before.AllocationCount, after.AllocationCount);
            Assert.Equal(before.DedicatedCount, after.DedicatedCount);
            Assert.Equal(before.UsedBytes, after.UsedBytes);
        }
        finally
        {
            context.Dispose();
        }

        TestVulkan.AssertNoValidationErrors(context);
    }

    [SkippableFact]
    public void LargeImagesGetDedicatedMemory()
    {
        var context = TestVulkan.CreateContextOrSkip();
        try
        {
            var before = context.Allocator.Statistics;
            using (var big = GpuImage.Create2D(context, 4096, 2048, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit, "big"))
                Assert.Equal(before.DedicatedCount + 1, context.Allocator.Statistics.DedicatedCount); // 32 MiB ≥ the threshold
            Assert.Equal(before.DedicatedCount, context.Allocator.Statistics.DedicatedCount);
            using var small = GpuImage.Create2D(context, 64, 64, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit, "small");
            var withSmall = context.Allocator.Statistics;
            Assert.Equal(before.DedicatedCount, withSmall.DedicatedCount);
            Assert.Equal(before.AllocationCount + 1, withSmall.AllocationCount); // sub-allocated
        }
        finally
        {
            context.Dispose();
        }

        TestVulkan.AssertNoValidationErrors(context);
    }
}
