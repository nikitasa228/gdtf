namespace Candela.Core.SceneGraph.Components;

/// <summary>
/// A GDTF device placed in the scene (lights, hazers, fog machines, fans, flame generators, …). The entity transform
/// is the fixture's base/hanging position; pan/tilt and other moving parts come from the fixture's DMX state.
/// </summary>
public sealed class FixtureComponent : Component
{
    /// <summary>GDTF file name as found in a library folder or the project's fixtures/ folder.</summary>
    public string GdtfSpec { get; set; } = "";

    /// <summary>DMX mode name from the GDTF; null = first mode.</summary>
    public string? GdtfMode { get; set; }

    /// <summary>Patch: one address per DMX break of the mode (most fixtures have one break).</summary>
    public List<DmxAddress> Addresses { get; set; } = new();

    /// <summary>Console fixture id (MVR FixtureID).</summary>
    public int FixtureId { get; set; }

    public string? UnitNumber { get; set; }
    public int CustomId { get; set; }

    /// <summary>Gel / identification colour from MVR (CIE 1931 xyY), if any.</summary>
    public CieColor? Color { get; set; }

    public bool CastShadow { get; set; } = true;

    /// <summary>Entity that carries the fixture's weight (truss, pipe, rigging point, wall bracket); null = free-standing.</summary>
    [EntityReference]
    public Guid? MountedOn { get; set; }

    /// <summary>
    /// Values set by hand in the properties panel while the fixture is not driven by a console: GDTF attribute name →
    /// value in the attribute's physical unit (Pan/Tilt in degrees, Dimmer 0..1, Zoom in degrees, …).
    /// </summary>
    public Dictionary<string, float> ManualAttributes { get; set; } = new();

    /// <summary>Optional point the fixture is focused on (world space, metres), as in MVR focus points.</summary>
    public System.Numerics.Vector3? FocusPoint { get; set; }

    /// <inheritdoc/>
    protected internal override void RemapEntityReferences(EntityReferenceRemapper remapper) => MountedOn = remapper.Remap(MountedOn);
}

/// <summary>
/// DMX address. <see cref="Universe"/> is 1-based as shown on consoles (Art-Net port-address 0 maps to universe 1 by
/// default, see the app settings); <see cref="Address"/> is 1..512; <see cref="Break"/> is the GDTF DMX break (1-based).
/// </summary>
public readonly record struct DmxAddress(int Universe, int Address, int Break = 1)
{
    /// <summary>Absolute address as used by MVR and many consoles: (universe-1)*512 + address.</summary>
    public int Absolute => (Universe - 1) * 512 + Address;

    public static DmxAddress FromAbsolute(int absolute, int dmxBreak = 1) =>
        new((absolute - 1) / 512 + 1, (absolute - 1) % 512 + 1, dmxBreak);

    public override string ToString() => $"{Universe}.{Address:D3}";
}

/// <summary>CIE 1931 chromaticity x, y and luminance-like Y (MVR uses Y in 0..100).</summary>
public readonly record struct CieColor(float X, float Y, float BigY);
