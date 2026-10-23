using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Candela.App.Ui;
using Microsoft.Win32;

namespace Candela.App.Shell;

/// <summary>Maker of a graphics adapter, for the driver download page.</summary>
internal enum GpuVendor
{
    /// <summary>Not known (no adapter found, a virtual or remote display adapter).</summary>
    Unknown,

    /// <summary>NVIDIA GeForce / RTX / Quadro.</summary>
    Nvidia,

    /// <summary>AMD Radeon.</summary>
    Amd,

    /// <summary>Intel graphics.</summary>
    Intel,
}

/// <summary>A display adapter installed in the computer (Windows: from the display class of the device registry).</summary>
/// <param name="Name">The adapter's name as its driver describes it ("NVIDIA GeForce RTX 3060").</param>
/// <param name="Vendor">Who makes it.</param>
internal readonly record struct DisplayAdapter(string Name, GpuVendor Vendor);

/// <summary>
/// What to tell a user whose computer cannot run the Vulkan renderer: whether the Vulkan runtime (<c>vulkan-1.dll</c>,
/// installed by the graphics driver) is there at all, which graphics card is installed, and where its maker's driver
/// downloads are. Used by the start-up failure message (a native message box on Windows). Never throws.
/// </summary>
internal static class GraphicsDriverHelp
{
    /// <summary>NVIDIA's driver download page.</summary>
    public const string NvidiaDrivers = "https://www.nvidia.com/Download/index.aspx";

    /// <summary>AMD's driver download page.</summary>
    public const string AmdDrivers = "https://www.amd.com/en/support/download/drivers.html";

    /// <summary>Intel's driver and support assistant (finds the right graphics driver).</summary>
    public const string IntelDrivers = "https://www.intel.com/content/www/us/en/support/detect.html";

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>True when the Vulkan loader of the graphics driver can be loaded (<c>vulkan-1.dll</c>, <c>libvulkan.so.1</c>).</summary>
    public static bool VulkanLoaderPresent()
    {
        string name = OperatingSystem.IsWindows() ? "vulkan-1.dll" : OperatingSystem.IsMacOS() ? "libvulkan.1.dylib" : "libvulkan.so.1";
        if (!NativeLibrary.TryLoad(name, out nint handle))
            return false;
        NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>The vendor of an adapter from its driver's description or provider name.</summary>
    public static GpuVendor VendorOf(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return GpuVendor.Unknown;
        if (description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || description.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Nvidia;
        if (description.Contains("AMD", StringComparison.Ordinal) || description.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || description.StartsWith("ATI ", StringComparison.Ordinal))
            return GpuVendor.Amd;
        if (description.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    /// <summary>The driver download page of <paramref name="vendor"/>, or null when it is not known.</summary>
    public static string? DriverPage(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => NvidiaDrivers,
        GpuVendor.Amd => AmdDrivers,
        GpuVendor.Intel => IntelDrivers,
        _ => null,
    };

    /// <summary>
    /// The display adapters of this computer, the ones that need a vendor driver first (NVIDIA, AMD, Intel, then the
    /// rest). Windows only; empty elsewhere or when the registry cannot be read.
    /// </summary>
    public static IReadOnlyList<DisplayAdapter> DisplayAdapters()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<DisplayAdapter>();
        try
        {
            return ReadDisplayAdapters();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<DisplayAdapter>();
        }
    }

    /// <summary>
    /// The adapter a driver update would help: the first by <see cref="DisplayAdapters"/>' order, ignoring Microsoft's
    /// basic and remote display adapters when another one exists.
    /// </summary>
    public static DisplayAdapter? MainAdapter(IReadOnlyList<DisplayAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        foreach (var adapter in adapters)
            if (adapter.Vendor != GpuVendor.Unknown)
                return adapter;
        return adapters.Count > 0 ? adapters[0] : null;
    }

    /// <summary>
    /// The full message for a start-up without a usable Vulkan device, in the UI language: the requirement, the cause
    /// (no Vulkan runtime, or no Vulkan 1.3 device with <paramref name="reason"/>), the graphics card, the driver pages,
    /// the log file and — when <paramref name="askToOpen"/> — the question whether to open the card maker's page.
    /// </summary>
    public static string Message(string reason, bool loaderPresent, DisplayAdapter? adapter, string logFile, bool askToOpen)
    {
        var text = new StringBuilder(1024);
        text.AppendLine(InstallLoc.Get(InstallStr.NoVulkanIntro)).AppendLine();
        text.AppendLine(loaderPresent ? InstallLoc.Format(InstallStr.NoVulkanDevice, reason) : InstallLoc.Get(InstallStr.NoVulkanLoaderMissing));
        if (adapter is { } a)
        {
            text.AppendLine(InstallLoc.Format(a.Vendor switch
            {
                GpuVendor.Nvidia => InstallStr.NoVulkanVendorNvidia,
                GpuVendor.Amd => InstallStr.NoVulkanVendorAmd,
                GpuVendor.Intel => InstallStr.NoVulkanVendorIntel,
                _ => InstallStr.NoVulkanVendorUnknown,
            }, a.Name));
        }

        text.AppendLine().AppendLine(InstallLoc.Get(InstallStr.NoVulkanDrivers)).AppendLine();
        text.Append(InstallLoc.Format(InstallStr.NoVulkanLog, logFile));
        if (askToOpen)
            text.AppendLine().AppendLine().Append(InstallLoc.Get(InstallStr.NoVulkanOpenPage));
        return text.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static List<DisplayAdapter> ReadDisplayAdapters()
    {
        var adapters = new List<DisplayAdapter>();
        using var display = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (display is null)
            return adapters;
        foreach (var name in display.GetSubKeyNames())
        {
            if (name.Length != 4 || !name.All(char.IsAsciiDigit))
                continue;
            try
            {
                using var key = display.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is not string description || description.Length == 0)
                    continue;
                var vendor = VendorOf(description);
                if (vendor == GpuVendor.Unknown)
                    vendor = VendorOf(key.GetValue("ProviderName") as string);
                if (!adapters.Any(x => x.Name == description))
                    adapters.Add(new DisplayAdapter(description, vendor));
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Some adapter keys are protected; skip them.
            }
        }

        adapters.Sort((x, y) => Rank(x.Vendor).CompareTo(Rank(y.Vendor)));
        return adapters;
    }

    private static int Rank(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => 0,
        GpuVendor.Amd => 1,
        GpuVendor.Intel => 2,
        _ => 3,
    };
}
