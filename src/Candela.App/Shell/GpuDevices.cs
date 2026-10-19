using System.Text;
using Candela.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace Candela.App.Shell;

/// <summary>
/// The graphics devices of the running Vulkan instance, for the "preferred GPU" choice of the settings window (the
/// setting is a device name; the context picks the first suitable device whose name contains it).
/// </summary>
internal static unsafe class GpuDevices
{
    /// <summary>Names of the physical devices the instance sees (empty when they cannot be listed).</summary>
    public static IReadOnlyList<string> List(VulkanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            uint count = 0;
            if (context.Vk.EnumeratePhysicalDevices(context.Instance, &count, null) != Result.Success || count == 0)
                return Array.Empty<string>();
            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* p = devices)
            {
                if (context.Vk.EnumeratePhysicalDevices(context.Instance, &count, p) is not (Result.Success or Result.Incomplete))
                    return Array.Empty<string>();
            }

            var names = new List<string>((int)count);
            for (int i = 0; i < count; i++)
            {
                var properties = context.Vk.GetPhysicalDeviceProperties(devices[i]);
                string name = DeviceName(properties.DeviceName, 256);
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }

            return names;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The listed device a preference names: an exact (case-insensitive) match first, else the first name containing it
    /// (the rule the context applies at start-up); null when none matches.
    /// </summary>
    public static string? Find(IReadOnlyList<string> devices, string preferred)
    {
        ArgumentNullException.ThrowIfNull(devices);
        string wanted = (preferred ?? "").Trim();
        if (wanted.Length == 0)
            return null;
        foreach (var d in devices)
            if (string.Equals(d, wanted, StringComparison.OrdinalIgnoreCase))
                return d;
        foreach (var d in devices)
            if (d.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    private static string DeviceName(byte* chars, int maxLength)
    {
        int length = 0;
        while (length < maxLength && chars[length] != 0)
            length++;
        return Encoding.UTF8.GetString(chars, length).Trim();
    }
}
