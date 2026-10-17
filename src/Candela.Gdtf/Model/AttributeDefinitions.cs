using Candela.Core.SceneGraph.Components;

namespace Candela.Gdtf;

/// <summary>GDTF <c>AttributeDefinitions</c>: activation groups, feature groups/features and the attributes themselves.</summary>
public sealed class AttributeDefinitions
{
    /// <summary>Groups of attributes that are only meaningful together (e.g. ColorRGB, PanTilt, Gobo1).</summary>
    public List<ActivationGroup> ActivationGroups { get; } = new();

    /// <summary>Feature groups (Dimmer, Position, Gobo, Color, Beam, Focus, Control, Shapers, Video).</summary>
    public List<FeatureGroup> FeatureGroups { get; } = new();

    /// <summary>All attributes defined by the fixture type.</summary>
    public List<GdtfAttribute> Attributes { get; } = new();
}

/// <summary>An activation group: attributes in it are applied together (e.g. Pan and Tilt).</summary>
public sealed class ActivationGroup
{
    /// <summary>Unique name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Attributes that belong to this group (back links filled by the reader).</summary>
    public List<GdtfAttribute> Attributes { get; } = new();
}

/// <summary>A feature group (e.g. "Position") with its features (e.g. "PanTilt").</summary>
public sealed class FeatureGroup
{
    /// <summary>Unique name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Display name.</summary>
    public string Pretty { get; set; } = "";

    /// <summary>Features of the group.</summary>
    public List<Feature> Features { get; } = new();
}

/// <summary>A feature inside a <see cref="FeatureGroup"/>; attributes refer to it as <c>Group.Feature</c>.</summary>
public sealed class Feature
{
    /// <summary>Name, unique inside the group.</summary>
    public string Name { get; set; } = "";

    /// <summary>Owning group (set by the reader).</summary>
    public FeatureGroup? Group { get; set; }

    /// <summary>Node name used by attributes: <c>Group.Feature</c>.</summary>
    public string FullName => Group is null ? Name : $"{Group.Name}.{Name}";
}

/// <summary>Physical unit of an attribute or sub-physical unit (GDTF enum; values keep GDTF units, e.g. degrees).</summary>
public enum PhysicalUnit
{
    /// <summary>No unit (dimensionless or abstract value).</summary>
    None,

    /// <summary>Percent.</summary>
    Percent,

    /// <summary>Length in metres.</summary>
    Length,

    /// <summary>Mass in kilograms.</summary>
    Mass,

    /// <summary>Time in seconds.</summary>
    Time,

    /// <summary>Temperature in kelvin.</summary>
    Temperature,

    /// <summary>Luminous intensity in candela.</summary>
    LuminousIntensity,

    /// <summary>Angle in degrees.</summary>
    Angle,

    /// <summary>Force in newton.</summary>
    Force,

    /// <summary>Frequency in hertz.</summary>
    Frequency,

    /// <summary>Current in amperes.</summary>
    Current,

    /// <summary>Voltage in volts.</summary>
    Voltage,

    /// <summary>Power in watts.</summary>
    Power,

    /// <summary>Energy in joules.</summary>
    Energy,

    /// <summary>Area in square metres.</summary>
    Area,

    /// <summary>Volume in cubic metres.</summary>
    Volume,

    /// <summary>Speed in metres per second.</summary>
    Speed,

    /// <summary>Acceleration in metres per second squared.</summary>
    Acceleration,

    /// <summary>Angular speed in degrees per second.</summary>
    AngularSpeed,

    /// <summary>Angular acceleration in degrees per second squared (GDTF spells it "AngularAccc").</summary>
    AngularAcceleration,

    /// <summary>Wavelength in nanometres.</summary>
    WaveLength,

    /// <summary>Colour component (0 … 1 of the emitter's full output).</summary>
    ColorComponent,
}

/// <summary>Kind of a <see cref="SubPhysicalUnit"/> (GDTF 1.2), used by effect attributes.</summary>
public enum SubPhysicalUnitType
{
    /// <summary>Placement offset of the effect.</summary>
    PlacementOffset,

    /// <summary>Amplitude.</summary>
    Amplitude,

    /// <summary>Minimum of the amplitude.</summary>
    AmplitudeMin,

    /// <summary>Maximum of the amplitude.</summary>
    AmplitudeMax,

    /// <summary>Duration of one cycle.</summary>
    Duration,

    /// <summary>Duty cycle.</summary>
    DutyCycle,

    /// <summary>Time offset.</summary>
    TimeOffset,

    /// <summary>Minimum opening (shutters, iris).</summary>
    MinimumOpening,

    /// <summary>Plain value.</summary>
    Value,

    /// <summary>Horizontal ratio.</summary>
    RatioHorizontal,

    /// <summary>Vertical ratio.</summary>
    RatioVertical,
}

/// <summary>A GDTF attribute (Pan, Dimmer, Gobo1, ColorAdd_R …): what a channel function controls.</summary>
public sealed class GdtfAttribute
{
    /// <summary>Unique attribute name (GDTF attribute naming, e.g. <c>Shutter1Strobe</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Short display name.</summary>
    public string Pretty { get; set; } = "";

    /// <summary>Activation group name as written, or null.</summary>
    public string? ActivationGroupName { get; set; }

    /// <summary>Resolved activation group.</summary>
    public ActivationGroup? ActivationGroup { get; set; }

    /// <summary>Feature node as written (<c>Group.Feature</c>).</summary>
    public string? FeatureName { get; set; }

    /// <summary>Resolved feature.</summary>
    public Feature? Feature { get; set; }

    /// <summary>Main attribute name for sub-attributes (e.g. Shutter1 for Shutter1Strobe), or null.</summary>
    public string? MainAttributeName { get; set; }

    /// <summary>Resolved main attribute.</summary>
    public GdtfAttribute? MainAttribute { get; set; }

    /// <summary>Unit of the physical values of channel functions that control this attribute.</summary>
    public PhysicalUnit PhysicalUnit { get; set; }

    /// <summary>Optional colour of the attribute (e.g. the emitter colour of ColorAdd_R), CIE 1931 xyY.</summary>
    public CieColor? Color { get; set; }

    /// <summary>Sub-physical units of effect attributes (GDTF 1.2).</summary>
    public List<SubPhysicalUnit> SubPhysicalUnits { get; } = new();

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A sub-physical unit of an attribute (GDTF 1.2), e.g. the duration of an effect.</summary>
public sealed class SubPhysicalUnit
{
    /// <summary>Kind of the value.</summary>
    public SubPhysicalUnitType Type { get; set; }

    /// <summary>Unit of <see cref="PhysicalFrom"/> / <see cref="PhysicalTo"/>.</summary>
    public PhysicalUnit PhysicalUnit { get; set; }

    /// <summary>Default physical start value.</summary>
    public float PhysicalFrom { get; set; }

    /// <summary>Default physical end value.</summary>
    public float PhysicalTo { get; set; } = 1f;

    /// <summary>Owning attribute (set by the reader).</summary>
    public GdtfAttribute? Attribute { get; set; }
}
