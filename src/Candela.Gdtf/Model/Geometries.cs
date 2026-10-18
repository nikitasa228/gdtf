using System.Numerics;

namespace Candela.Gdtf;

/// <summary>Element type of a node in the GDTF geometry tree.</summary>
public enum GeometryType
{
    /// <summary>Plain geometry (static part).</summary>
    Geometry,

    /// <summary>Rotating axis (pan, tilt, …), animated by the DMX layer.</summary>
    Axis,

    /// <summary>Beam filter (e.g. a frost or barrel that shapes the beam).</summary>
    FilterBeam,

    /// <summary>Colour filter (CMY flag, colour wheel position).</summary>
    FilterColor,

    /// <summary>Gobo position.</summary>
    FilterGobo,

    /// <summary>Framing shutter / shaper.</summary>
    FilterShaper,

    /// <summary>Light source with beam parameters.</summary>
    Beam,

    /// <summary>Media server layer.</summary>
    MediaServerLayer,

    /// <summary>Media server camera.</summary>
    MediaServerCamera,

    /// <summary>Media server master.</summary>
    MediaServerMaster,

    /// <summary>Display with a texture.</summary>
    Display,

    /// <summary>Instance of a top-level geometry (multi-pixel bars, multi-head fixtures).</summary>
    GeometryReference,

    /// <summary>Laser source.</summary>
    Laser,

    /// <summary>Connector / electrical port.</summary>
    WiringObject,

    /// <summary>Inventory item (spare parts, accessories).</summary>
    Inventory,

    /// <summary>Truss or tube structure.</summary>
    Structure,

    /// <summary>Rope or ground support.</summary>
    Support,

    /// <summary>Magnet (mounting point for other fixtures).</summary>
    Magnet,
}

/// <summary>
/// A node of the GDTF geometry tree. <see cref="Position"/> is the transform relative to the parent in metres, already
/// converted from the GDTF column-vector matrix to the System.Numerics row-vector convention
/// (<c>world = Position * parentWorld</c>). Fixtures are modelled hanging: the beam of a moving head points along −Z.
/// </summary>
public class GdtfGeometry
{
    /// <summary>Creates a plain <see cref="GeometryType.Geometry"/> node.</summary>
    public GdtfGeometry() : this(GeometryType.Geometry) { }

    /// <summary>Creates a node of a derived type.</summary>
    protected GdtfGeometry(GeometryType type) => Type = type;

    /// <summary>Element type.</summary>
    public GeometryType Type { get; }

    /// <summary>Name, unique within the fixture type (DMX channels and references use it).</summary>
    public string Name { get; set; } = "";

    /// <summary>Model name as written, or null.</summary>
    public string? ModelName { get; set; }

    /// <summary>Resolved model.</summary>
    public GdtfModel? Model { get; set; }

    /// <summary>Local transform relative to the parent (row-vector convention, metres).</summary>
    public Matrix4x4 Position { get; set; } = Matrix4x4.Identity;

    /// <summary>Parent node, null for top-level geometries.</summary>
    public GdtfGeometry? Parent { get; private set; }

    /// <summary>Child nodes in file order.</summary>
    public IReadOnlyList<GdtfGeometry> Children => _children;

    private readonly List<GdtfGeometry> _children = new();

    /// <summary>The top-level geometry this node belongs to.</summary>
    public GdtfGeometry TopLevel
    {
        get
        {
            var g = this;
            while (g.Parent is not null) g = g.Parent;
            return g;
        }
    }

    /// <summary>Appends a child and sets its parent.</summary>
    public void AddChild(GdtfGeometry child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent is not null) throw new InvalidOperationException($"Geometry '{child.Name}' already has a parent.");
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>This node and all descendants, depth first in file order.</summary>
    public IEnumerable<GdtfGeometry> DescendantsAndSelf()
    {
        var stack = new Stack<GdtfGeometry>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var g = stack.Pop();
            yield return g;
            for (int i = g._children.Count - 1; i >= 0; i--) stack.Push(g._children[i]);
        }
    }

    /// <summary>True when <paramref name="ancestor"/> is this node or one of its ancestors.</summary>
    public bool IsSelfOrDescendantOf(GdtfGeometry ancestor)
    {
        for (var g = this; g is not null; g = g.Parent)
            if (ReferenceEquals(g, ancestor)) return true;
        return false;
    }

    /// <summary>Transform from this node to its top-level geometry (product of positions up the tree).</summary>
    public Matrix4x4 TransformToTopLevel()
    {
        var m = Matrix4x4.Identity;
        for (var g = this; g is not null; g = g.Parent) m *= g.Position;
        return m;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Type} '{Name}'";
}

/// <summary>Rotating axis (pan/tilt/…); its rotation comes from the DMX state of the attributes it carries.</summary>
public sealed class AxisGeometry : GdtfGeometry
{
    /// <summary>Creates an axis.</summary>
    public AxisGeometry() : base(GeometryType.Axis) { }
}

/// <summary>Beam filter geometry.</summary>
public sealed class FilterBeamGeometry : GdtfGeometry
{
    /// <summary>Creates a beam filter.</summary>
    public FilterBeamGeometry() : base(GeometryType.FilterBeam) { }
}

/// <summary>Colour filter geometry.</summary>
public sealed class FilterColorGeometry : GdtfGeometry
{
    /// <summary>Creates a colour filter.</summary>
    public FilterColorGeometry() : base(GeometryType.FilterColor) { }
}

/// <summary>Gobo geometry.</summary>
public sealed class FilterGoboGeometry : GdtfGeometry
{
    /// <summary>Creates a gobo geometry.</summary>
    public FilterGoboGeometry() : base(GeometryType.FilterGobo) { }
}

/// <summary>Shaper (framing shutter) geometry.</summary>
public sealed class FilterShaperGeometry : GdtfGeometry
{
    /// <summary>Creates a shaper geometry.</summary>
    public FilterShaperGeometry() : base(GeometryType.FilterShaper) { }
}

/// <summary>Media server layer geometry.</summary>
public sealed class MediaServerLayerGeometry : GdtfGeometry
{
    /// <summary>Creates a media server layer.</summary>
    public MediaServerLayerGeometry() : base(GeometryType.MediaServerLayer) { }
}

/// <summary>Media server camera geometry.</summary>
public sealed class MediaServerCameraGeometry : GdtfGeometry
{
    /// <summary>Creates a media server camera.</summary>
    public MediaServerCameraGeometry() : base(GeometryType.MediaServerCamera) { }
}

/// <summary>Media server master geometry.</summary>
public sealed class MediaServerMasterGeometry : GdtfGeometry
{
    /// <summary>Creates a media server master.</summary>
    public MediaServerMasterGeometry() : base(GeometryType.MediaServerMaster) { }
}

/// <summary>Magnet geometry (mounting point).</summary>
public sealed class MagnetGeometry : GdtfGeometry
{
    /// <summary>Creates a magnet.</summary>
    public MagnetGeometry() : base(GeometryType.Magnet) { }
}

/// <summary>Kind of lamp of a beam.</summary>
public enum LampType
{
    /// <summary>Discharge lamp (GDTF default).</summary>
    Discharge,

    /// <summary>Tungsten lamp.</summary>
    Tungsten,

    /// <summary>Halogen lamp.</summary>
    Halogen,

    /// <summary>LED.</summary>
    Led,
}

/// <summary>Optical type of a beam.</summary>
public enum BeamType
{
    /// <summary>Soft-edged wash beam (GDTF default).</summary>
    Wash,

    /// <summary>Hard-edged spot beam.</summary>
    Spot,

    /// <summary>No beam (e.g. a hazer outlet).</summary>
    None,

    /// <summary>Rectangular beam (projectors, panels).</summary>
    Rectangle,

    /// <summary>Plano-convex lens.</summary>
    PC,

    /// <summary>Fresnel lens.</summary>
    Fresnel,

    /// <summary>Glowing surface without a beam (pixels, LED tubes).</summary>
    Glow,
}

/// <summary>The Beam attributes that have a GDTF default, as flags (see <see cref="BeamGeometry.Specified"/>).</summary>
[Flags]
public enum BeamAttributes
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><see cref="BeamGeometry.LampType"/>.</summary>
    LampType = 1 << 0,

    /// <summary><see cref="BeamGeometry.PowerConsumption"/>.</summary>
    PowerConsumption = 1 << 1,

    /// <summary><see cref="BeamGeometry.LuminousFlux"/>.</summary>
    LuminousFlux = 1 << 2,

    /// <summary><see cref="BeamGeometry.ColorTemperature"/>.</summary>
    ColorTemperature = 1 << 3,

    /// <summary><see cref="BeamGeometry.BeamAngle"/>.</summary>
    BeamAngle = 1 << 4,

    /// <summary><see cref="BeamGeometry.FieldAngle"/>.</summary>
    FieldAngle = 1 << 5,

    /// <summary><see cref="BeamGeometry.ThrowRatio"/>.</summary>
    ThrowRatio = 1 << 6,

    /// <summary><see cref="BeamGeometry.RectangleRatio"/>.</summary>
    RectangleRatio = 1 << 7,

    /// <summary><see cref="BeamGeometry.BeamRadius"/>.</summary>
    BeamRadius = 1 << 8,

    /// <summary><see cref="BeamGeometry.BeamType"/>.</summary>
    BeamType = 1 << 9,

    /// <summary><see cref="BeamGeometry.ColorRenderingIndex"/>.</summary>
    ColorRenderingIndex = 1 << 10,

    /// <summary>Every attribute above.</summary>
    All = (1 << 11) - 1,
}

/// <summary>A light source. Light leaves the geometry origin along its local −Z axis.</summary>
/// <remarks>
/// Every photometric attribute has a GDTF default (1000 W, 10 000 lm, 6000 K, 25°/25°, Wash …) that editors write back
/// unchanged, so a value alone cannot tell "the file says 10 000 lm" from "the author never set it". <see cref="Specified"/>
/// (and the <c>Is…Specified</c> properties) record which attributes the file actually gave with a valid value; the
/// properties themselves always hold a usable value (the file's, else the GDTF default). Assigning a property in code
/// marks it as specified.
/// </remarks>
public sealed class BeamGeometry : GdtfGeometry
{
    private LampType _lampType = LampType.Discharge;
    private float _powerConsumption = 1000f;
    private float _luminousFlux = 10000f;
    private float _colorTemperature = 6000f;
    private float _beamAngle = 25f;
    private float _fieldAngle = 25f;
    private float _throwRatio = 1f;
    private float _rectangleRatio = 1.7777f;
    private float _beamRadius = 0.05f;
    private BeamType _beamType = BeamType.Wash;
    private float _colorRenderingIndex = 100f;

    /// <summary>Creates a beam with GDTF defaults (nothing specified).</summary>
    public BeamGeometry() : base(GeometryType.Beam) { }

    /// <summary>
    /// The attributes the file gave with a valid value (attributes that are missing, empty or unparsable keep the GDTF
    /// default and are not in this set).
    /// </summary>
    public BeamAttributes Specified { get; set; }

    /// <summary>True when every attribute in <paramref name="attributes"/> was given by the file.</summary>
    public bool IsSpecified(BeamAttributes attributes) => attributes != BeamAttributes.None && (Specified & attributes) == attributes;

    /// <summary>True when <see cref="LampType"/> comes from the file (not the GDTF default Discharge).</summary>
    public bool IsLampTypeSpecified => IsSpecified(BeamAttributes.LampType);

    /// <summary>True when <see cref="PowerConsumption"/> comes from the file (not the GDTF default 1000 W).</summary>
    public bool IsPowerConsumptionSpecified => IsSpecified(BeamAttributes.PowerConsumption);

    /// <summary>True when <see cref="LuminousFlux"/> comes from the file (not the GDTF default 10 000 lm).</summary>
    public bool IsLuminousFluxSpecified => IsSpecified(BeamAttributes.LuminousFlux);

    /// <summary>True when <see cref="ColorTemperature"/> comes from the file (not the GDTF default 6000 K).</summary>
    public bool IsColorTemperatureSpecified => IsSpecified(BeamAttributes.ColorTemperature);

    /// <summary>True when <see cref="BeamAngle"/> comes from the file (not the GDTF default 25°).</summary>
    public bool IsBeamAngleSpecified => IsSpecified(BeamAttributes.BeamAngle);

    /// <summary>True when <see cref="FieldAngle"/> comes from the file (not the GDTF default 25°).</summary>
    public bool IsFieldAngleSpecified => IsSpecified(BeamAttributes.FieldAngle);

    /// <summary>True when <see cref="BeamType"/> comes from the file (not the GDTF default Wash).</summary>
    public bool IsBeamTypeSpecified => IsSpecified(BeamAttributes.BeamType);

    /// <summary>True when <see cref="ColorRenderingIndex"/> comes from the file (not the GDTF default 100).</summary>
    public bool IsColorRenderingIndexSpecified => IsSpecified(BeamAttributes.ColorRenderingIndex);

    /// <summary>Lamp kind (GDTF default Discharge).</summary>
    public LampType LampType { get => _lampType; set { _lampType = value; Specified |= BeamAttributes.LampType; } }

    /// <summary>Electrical power of the source in W (GDTF default 1000).</summary>
    public float PowerConsumption { get => _powerConsumption; set { _powerConsumption = value; Specified |= BeamAttributes.PowerConsumption; } }

    /// <summary>Luminous flux of the source in lm (GDTF default 10000).</summary>
    public float LuminousFlux { get => _luminousFlux; set { _luminousFlux = value; Specified |= BeamAttributes.LuminousFlux; } }

    /// <summary>Correlated colour temperature of the white source in K (GDTF default 6000).</summary>
    public float ColorTemperature { get => _colorTemperature; set { _colorTemperature = value; Specified |= BeamAttributes.ColorTemperature; } }

    /// <summary>Full beam angle in degrees, where intensity falls to 50 % (GDTF default 25).</summary>
    public float BeamAngle { get => _beamAngle; set { _beamAngle = value; Specified |= BeamAttributes.BeamAngle; } }

    /// <summary>Full field angle in degrees, where intensity falls to 10 % (GDTF default 25).</summary>
    public float FieldAngle { get => _fieldAngle; set { _fieldAngle = value; Specified |= BeamAttributes.FieldAngle; } }

    /// <summary>Throw ratio of a projector lens (GDTF default 1).</summary>
    public float ThrowRatio { get => _throwRatio; set { _throwRatio = value; Specified |= BeamAttributes.ThrowRatio; } }

    /// <summary>Width/height ratio of a rectangular beam (GDTF default 1.7777).</summary>
    public float RectangleRatio { get => _rectangleRatio; set { _rectangleRatio = value; Specified |= BeamAttributes.RectangleRatio; } }

    /// <summary>Radius of the light-emitting surface in metres (GDTF default 0.05).</summary>
    public float BeamRadius { get => _beamRadius; set { _beamRadius = value; Specified |= BeamAttributes.BeamRadius; } }

    /// <summary>Beam type (GDTF default Wash).</summary>
    public BeamType BeamType { get => _beamType; set { _beamType = value; Specified |= BeamAttributes.BeamType; } }

    /// <summary>Colour rendering index 0 … 100 (GDTF default 100).</summary>
    public float ColorRenderingIndex { get => _colorRenderingIndex; set { _colorRenderingIndex = value; Specified |= BeamAttributes.ColorRenderingIndex; } }

    /// <summary>Emitter that describes the white spectrum, as written (GDTF 1.2), or null.</summary>
    public string? EmitterSpectrumName { get; set; }

    /// <summary>Resolved spectrum emitter.</summary>
    public Emitter? EmitterSpectrum { get; set; }
}

/// <summary>A display (screen) geometry.</summary>
public sealed class DisplayGeometry : GdtfGeometry
{
    /// <summary>Creates a display.</summary>
    public DisplayGeometry() : base(GeometryType.Display) { }

    /// <summary>Texture resource name without extension, or null.</summary>
    public string? Texture { get; set; }
}

/// <summary>DMX offset that a <see cref="GeometryReference"/> applies to the channels of its referenced geometry.</summary>
/// <param name="DmxBreak">DMX break the offset applies to (1-based).</param>
/// <param name="DmxOffset">1-based DMX address offset of the instance in that break.</param>
public readonly record struct GeometryBreak(int DmxBreak, int DmxOffset);

/// <summary>
/// An instance of a top-level geometry (e.g. each pixel of an LED bar). The referenced tree is placed at this node's
/// position; its DMX channels are repeated per instance at the offsets given by <see cref="Breaks"/>.
/// </summary>
public sealed class GeometryReference : GdtfGeometry
{
    /// <summary>Creates a reference.</summary>
    public GeometryReference() : base(GeometryType.GeometryReference) { }

    /// <summary>Name of the referenced top-level geometry as written.</summary>
    public string? GeometryName { get; set; }

    /// <summary>Resolved top-level geometry.</summary>
    public GdtfGeometry? ReferencedGeometry { get; set; }

    /// <summary>DMX offsets per break, in file order (the last one is used for channels with DMXBreak "Overwrite").</summary>
    public List<GeometryBreak> Breaks { get; } = new();
}

/// <summary>Colour model of a laser.</summary>
public enum LaserColorType
{
    /// <summary>RGB laser (default).</summary>
    Rgb,

    /// <summary>Single-wavelength laser.</summary>
    SingleWaveLength,
}

/// <summary>A laser source.</summary>
public sealed class LaserGeometry : GdtfGeometry
{
    /// <summary>Creates a laser.</summary>
    public LaserGeometry() : base(GeometryType.Laser) { }

    /// <summary>Colour model.</summary>
    public LaserColorType ColorType { get; set; }

    /// <summary>Wavelength in nm for single-wavelength lasers.</summary>
    public float? ColorWaveLength { get; set; }

    /// <summary>Optical output in W.</summary>
    public float OutputStrength { get; set; }

    /// <summary>Emitter name as written, or null.</summary>
    public string? EmitterName { get; set; }

    /// <summary>Resolved emitter.</summary>
    public Emitter? Emitter { get; set; }

    /// <summary>Beam diameter at the aperture in metres.</summary>
    public float BeamDiameter { get; set; }

    /// <summary>Minimum beam divergence in mrad.</summary>
    public float BeamDivergenceMin { get; set; }

    /// <summary>Maximum beam divergence in mrad.</summary>
    public float BeamDivergenceMax { get; set; }

    /// <summary>Scan angle along pan in degrees.</summary>
    public float ScanAnglePan { get; set; }

    /// <summary>Scan angle along tilt in degrees.</summary>
    public float ScanAngleTilt { get; set; }

    /// <summary>Scan speed in Hz.</summary>
    public float ScanSpeed { get; set; }

    /// <summary>Supported laser protocols (e.g. ILDA).</summary>
    public List<string> Protocols { get; } = new();
}

/// <summary>Role of a wiring object.</summary>
public enum WiringComponentType
{
    /// <summary>Signal or power input.</summary>
    Input,

    /// <summary>Signal or power output.</summary>
    Output,

    /// <summary>Power source (e.g. a distro output).</summary>
    PowerSource,

    /// <summary>Power consumer (mains input of a fixture).</summary>
    Consumer,

    /// <summary>Fuse.</summary>
    Fuse,

    /// <summary>Network provider.</summary>
    NetworkProvider,

    /// <summary>Network input.</summary>
    NetworkInput,

    /// <summary>Network output.</summary>
    NetworkOutput,

    /// <summary>Bidirectional network port.</summary>
    NetworkInOut,
}

/// <summary>Fuse trip characteristic.</summary>
public enum FuseRating
{
    /// <summary>B characteristic.</summary>
    B,

    /// <summary>C characteristic.</summary>
    C,

    /// <summary>D characteristic.</summary>
    D,

    /// <summary>K characteristic.</summary>
    K,

    /// <summary>Z characteristic.</summary>
    Z,
}

/// <summary>Where the connector faces on the device.</summary>
public enum WiringOrientation
{
    /// <summary>Left side.</summary>
    Left,

    /// <summary>Right side.</summary>
    Right,

    /// <summary>Top side.</summary>
    Top,

    /// <summary>Bottom side.</summary>
    Bottom,
}

/// <summary>An electrical/data port of the device (GDTF 1.2), the source of cabling ports.</summary>
public sealed class WiringObjectGeometry : GdtfGeometry
{
    /// <summary>Creates a wiring object.</summary>
    public WiringObjectGeometry() : base(GeometryType.WiringObject) { }

    /// <summary>Connector type name (GDTF connector list: XLR5, RJ45, PowerconTRUE1 …).</summary>
    public string? ConnectorType { get; set; }

    /// <summary>Role of the port.</summary>
    public WiringComponentType ComponentType { get; set; }

    /// <summary>Signal carried, as written (DMX512, Power, Ethernet …).</summary>
    public string? SignalType { get; set; }

    /// <summary>Number of pins.</summary>
    public int PinCount { get; set; }

    /// <summary>Power the port draws as a consumer in W.</summary>
    public float ElectricalPayLoad { get; set; }

    /// <summary>Lowest accepted voltage in V.</summary>
    public float VoltageRangeMin { get; set; }

    /// <summary>Highest accepted voltage in V.</summary>
    public float VoltageRangeMax { get; set; }

    /// <summary>Lowest accepted frequency in Hz.</summary>
    public float FrequencyRangeMin { get; set; }

    /// <summary>Highest accepted frequency in Hz.</summary>
    public float FrequencyRangeMax { get; set; }

    /// <summary>Maximum power the port can deliver as a source in W.</summary>
    public float MaxPayLoad { get; set; }

    /// <summary>Nominal voltage of a power source in V.</summary>
    public float Voltage { get; set; }

    /// <summary>Signal layer (patching level) of the port.</summary>
    public int SignalLayer { get; set; }

    /// <summary>Power factor of a consumer (cos φ).</summary>
    public float CosPhi { get; set; }

    /// <summary>Fuse current in A.</summary>
    public float FuseCurrent { get; set; }

    /// <summary>Fuse characteristic.</summary>
    public FuseRating FuseRating { get; set; }

    /// <summary>Side the connector faces.</summary>
    public WiringOrientation Orientation { get; set; }

    /// <summary>Group of wiring objects that share a cable.</summary>
    public string? WireGroup { get; set; }

    /// <summary>Internal pin connections to other wiring objects.</summary>
    public List<PinPatch> PinPatches { get; } = new();

    /// <summary>True for ports that carry mains or DC power (by component type, signal type or name).</summary>
    public bool IsPower =>
        ComponentType is WiringComponentType.Consumer or WiringComponentType.PowerSource or WiringComponentType.Fuse ||
        (SignalType?.Contains("power", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>An internal pin connection from a wiring object to another one.</summary>
public sealed class PinPatch
{
    /// <summary>Target wiring object name as written.</summary>
    public string? ToWiringObjectName { get; set; }

    /// <summary>Resolved target.</summary>
    public WiringObjectGeometry? ToWiringObject { get; set; }

    /// <summary>Pin on this wiring object.</summary>
    public int FromPin { get; set; }

    /// <summary>Pin on the target.</summary>
    public int ToPin { get; set; }
}

/// <summary>An inventory item (accessory or spare part) carried by the device.</summary>
public sealed class InventoryGeometry : GdtfGeometry
{
    /// <summary>Creates an inventory item.</summary>
    public InventoryGeometry() : base(GeometryType.Inventory) { }

    /// <summary>Number of items.</summary>
    public int Count { get; set; }
}

/// <summary>How a structure is described.</summary>
public enum StructureType
{
    /// <summary>Described by its centre line and cross section.</summary>
    CenterLineBased,

    /// <summary>Detailed model.</summary>
    Detail,
}

/// <summary>Cross section of a structure.</summary>
public enum CrossSectionType
{
    /// <summary>Truss framework.</summary>
    TrussFramework,

    /// <summary>Tube.</summary>
    Tube,
}

/// <summary>A truss or tube structure (GDTF 1.2).</summary>
public sealed class StructureGeometry : GdtfGeometry
{
    /// <summary>Creates a structure.</summary>
    public StructureGeometry() : base(GeometryType.Structure) { }

    /// <summary>Linked geometry name as written.</summary>
    public string? LinkedGeometryName { get; set; }

    /// <summary>Resolved linked geometry.</summary>
    public GdtfGeometry? LinkedGeometry { get; set; }

    /// <summary>Description kind.</summary>
    public StructureType StructureType { get; set; }

    /// <summary>Cross section kind.</summary>
    public CrossSectionType CrossSectionType { get; set; }

    /// <summary>Height of a tube cross section in metres.</summary>
    public float CrossSectionHeight { get; set; }

    /// <summary>Wall thickness of a tube cross section in metres.</summary>
    public float CrossSectionWallThickness { get; set; }

    /// <summary>Name of the truss cross section.</summary>
    public string? TrussCrossSection { get; set; }
}

/// <summary>Kind of support.</summary>
public enum SupportType
{
    /// <summary>Rope (hanging point).</summary>
    Rope,

    /// <summary>Ground support.</summary>
    GroundSupport,
}

/// <summary>A support (rope or ground support) with load capacities and stiffness (GDTF 1.2).</summary>
public sealed class SupportGeometry : GdtfGeometry
{
    /// <summary>Creates a support.</summary>
    public SupportGeometry() : base(GeometryType.Support) { }

    /// <summary>Kind of support.</summary>
    public SupportType SupportType { get; set; }

    /// <summary>Rope cross section name.</summary>
    public string? RopeCrossSection { get; set; }

    /// <summary>Rope offset in metres.</summary>
    public Vector3 RopeOffset { get; set; }

    /// <summary>Allowed force along X/Y/Z in N (GDTF CapacityX/Y/Z).</summary>
    public Vector3 Capacity { get; set; }

    /// <summary>Allowed moment about X/Y/Z in N·m (GDTF CapacityXX/YY/ZZ).</summary>
    public Vector3 CapacityRotational { get; set; }

    /// <summary>Stiffness along X/Y/Z in N/m (GDTF ResistanceX/Y/Z).</summary>
    public Vector3 Resistance { get; set; }

    /// <summary>Rotational stiffness about X/Y/Z in N·m/deg (GDTF ResistanceXX/YY/ZZ).</summary>
    public Vector3 ResistanceRotational { get; set; }
}
