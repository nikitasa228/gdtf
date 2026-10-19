using Candela.Electrical;

namespace Candela.App.Ui;

/// <summary>Icon glyphs of the cabling devices (library tiles, cable link cards, properties headers).</summary>
internal static class DeviceIcons
{
    /// <summary>The glyph of a library section.</summary>
    public static string Glyph(DeviceGroup group) => group switch
    {
        DeviceGroup.Console => Icons.Sliders,
        DeviceGroup.DataDistribution => StageIcons.Router,
        DeviceGroup.Mains => StageIcons.PlugZap,
        DeviceGroup.Distro => Icons.Zap,
        _ => Icons.SunMedium,
    };

    /// <summary>The glyph of a device preset's icon key ("console", "network-console", "node", "splitter", "mains", "distro", "dimmer", "psu").</summary>
    public static string Glyph(string? iconKey) => iconKey switch
    {
        "console" => Icons.Sliders,
        "network-console" => Icons.Network,
        "node" => StageIcons.Router,
        "splitter" => Icons.Waypoints,
        "mains" => StageIcons.PlugZap,
        "distro" => Icons.Zap,
        "dimmer" => Icons.SunMedium,
        "psu" => Icons.Bolt,
        _ => Icons.Box,
    };
}
