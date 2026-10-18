using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>A physical device as seen by <see cref="DeviceSelector"/> (kept free of handles so the ranking is unit-testable).</summary>
internal sealed record DeviceCandidate(
    int Index,
    string Name,
    PhysicalDeviceType Type,
    uint ApiVersion,
    ulong DeviceLocalMemoryBytes,
    IReadOnlyList<string> MissingRequirements);

/// <summary>
/// Picks the physical device: only devices that meet every requirement (Vulkan 1.3, dynamic rendering,
/// synchronization2, descriptor indexing, timeline semaphores, scalar block layout, a graphics+compute queue and
/// presentation when a surface was given) are eligible. Among them a device whose name contains the preferred name
/// wins; otherwise discrete &gt; integrated &gt; virtual &gt; CPU, then the larger device-local memory.
/// </summary>
internal static class DeviceSelector
{
    public static int TypeRank(PhysicalDeviceType type) => type switch
    {
        PhysicalDeviceType.DiscreteGpu => 4,
        PhysicalDeviceType.IntegratedGpu => 3,
        PhysicalDeviceType.VirtualGpu => 2,
        PhysicalDeviceType.Cpu => 1,
        _ => 0,
    };

    /// <summary>Index of the chosen candidate, or null when none is eligible.</summary>
    public static DeviceCandidate? Select(IReadOnlyList<DeviceCandidate> candidates, string preferredName)
    {
        DeviceCandidate? best = null;
        foreach (var c in candidates)
        {
            if (c.MissingRequirements.Count > 0)
                continue;
            if (best is null || Compare(c, best, preferredName) > 0)
                best = c;
        }

        return best;
    }

    private static int Compare(DeviceCandidate a, DeviceCandidate b, string preferredName)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            bool pa = a.Name.Contains(preferredName.Trim(), StringComparison.OrdinalIgnoreCase);
            bool pb = b.Name.Contains(preferredName.Trim(), StringComparison.OrdinalIgnoreCase);
            if (pa != pb)
                return pa ? 1 : -1;
        }

        int ra = TypeRank(a.Type), rb = TypeRank(b.Type);
        if (ra != rb)
            return ra.CompareTo(rb);
        if (a.DeviceLocalMemoryBytes != b.DeviceLocalMemoryBytes)
            return a.DeviceLocalMemoryBytes.CompareTo(b.DeviceLocalMemoryBytes);
        return b.Index.CompareTo(a.Index); // stable: the first enumerated device wins ties
    }
}
