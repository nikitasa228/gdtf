using System.Numerics;
using System.Xml.Linq;
using Candela.Core.SceneGraph.Components;

namespace Candela.Mvr;

/// <summary>Element type of an MVR scene object.</summary>
public enum MvrObjectKind
{
    /// <summary><c>SceneObject</c>: generic geometry (set pieces, walls, furniture; GDTF-driven since MVR 1.6).</summary>
    SceneObject,

    /// <summary><c>GroupObject</c>: groups child objects under one transform.</summary>
    GroupObject,

    /// <summary><c>FocusPoint</c>: a point fixtures are focused on.</summary>
    FocusPoint,

    /// <summary><c>Fixture</c>: a GDTF device (light, hazer, …) with its patch.</summary>
    Fixture,

    /// <summary><c>Truss</c>: truss or pipe that carries fixtures.</summary>
    Truss,

    /// <summary><c>Support</c>: hoist, rope or ground support that carries a structure.</summary>
    Support,

    /// <summary><c>VideoScreen</c>: an LED wall or display with video sources.</summary>
    VideoScreen,

    /// <summary><c>Projector</c>: a video projector with projections.</summary>
    Projector,
}

/// <summary>
/// Base of every object of a layer's <c>ChildList</c>: identity, name, class and transform. The transform is relative
/// to the parent object (or the layer) and converted to metres: see <see cref="MvrMatrix"/>.
/// </summary>
public abstract class MvrObject
{
    /// <summary>Element type.</summary>
    public abstract MvrObjectKind Kind { get; }

    /// <summary>Identity (<c>uuid</c>); kept as the entity id on import.</summary>
    public Guid Uuid { get; set; }

    /// <summary>Display name (<c>name</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Class the object belongs to (<c>Classing</c>, uuid of an <see cref="MvrClass"/>), or null.</summary>
    public Guid? Classing { get; set; }

    /// <summary>Transform relative to the parent in metres (row-vector convention); identity when absent.</summary>
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;

    /// <summary>True when the file wrote a <c>Matrix</c> for the object.</summary>
    public bool HasMatrix { get; set; }

    /// <summary>Child elements this reader does not know (vendor extensions, newer versions), kept verbatim.</summary>
    public List<XElement> UnknownElements { get; } = new();

    /// <summary>Child objects (<c>ChildList</c>); empty for object kinds that cannot have children.</summary>
    public virtual IReadOnlyList<MvrObject> ChildObjects => Array.Empty<MvrObject>();

    /// <summary>This object and all descendants, depth first in file order.</summary>
    public IEnumerable<MvrObject> SelfAndDescendants()
    {
        var stack = new Stack<MvrObject>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            yield return o;
            var children = o.ChildObjects;
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} '{Name}' ({Uuid})";
}

/// <summary><c>GroupObject</c>: a transform that groups child objects (nested groups allowed).</summary>
public sealed class MvrGroupObject : MvrObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.GroupObject;

    /// <summary>Grouped objects (<c>ChildList</c>), relative to the group.</summary>
    public List<MvrObject> Children { get; } = new();

    /// <inheritdoc />
    public override IReadOnlyList<MvrObject> ChildObjects => Children;
}

/// <summary><c>FocusPoint</c>: a target fixtures refer to through <see cref="MvrFixture.Focus"/>.</summary>
public sealed class MvrFocusPoint : MvrObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.FocusPoint;

    /// <summary>Optional geometry that visualises the point.</summary>
    public List<MvrGeometry> Geometries { get; } = new();
}

/// <summary>
/// Base of the object kinds that can carry geometry, a GDTF and a patch: <c>SceneObject</c>, <c>Fixture</c>,
/// <c>Truss</c>, <c>Support</c>, <c>VideoScreen</c> and <c>Projector</c> (since MVR 1.6 all of them may reference a GDTF).
/// </summary>
public abstract class MvrDeviceObject : MvrObject
{
    /// <summary>Uuid of the main object of a multipatch group (<c>multipatch</c>, MVR 1.6), or null.</summary>
    public Guid? Multipatch { get; set; }

    /// <summary>Geometry of the object (<c>Geometries</c>); fixtures take theirs from the GDTF instead.</summary>
    public List<MvrGeometry> Geometries { get; } = new();

    /// <summary>GDTF file reference (<c>GDTFSpec</c>), usually <c>"Manufacturer@Model.gdtf"</c>, or null.</summary>
    public string? GdtfSpec { get; set; }

    /// <summary>DMX mode name of the GDTF (<c>GDTFMode</c>), or null.</summary>
    public string? GdtfMode { get; set; }

    /// <summary>Whether the object casts shadows (<c>CastShadow</c>), or null when not written (default true).</summary>
    public bool? CastShadow { get; set; }

    /// <summary>Hanging position (<c>Position</c>, uuid of an <see cref="MvrPositionDefinition"/>), or null.</summary>
    public Guid? Position { get; set; }

    /// <summary>Purpose of the object (<c>Function</c>, free text), or null.</summary>
    public string? Function { get; set; }

    /// <summary>Fixture id as shown on consoles (<c>FixtureID</c>; text since MVR 1.5), or null.</summary>
    public string? FixtureId { get; set; }

    /// <summary>Numeric fixture id (<c>FixtureIDNumeric</c>, MVR 1.5+), or null.</summary>
    public int? FixtureIdNumeric { get; set; }

    /// <summary>Unit number (<c>UnitNumber</c>), or null.</summary>
    public int? UnitNumber { get; set; }

    /// <summary>Node path of the parent GDTF geometry this object is attached to (<c>ChildPosition</c>), or null.</summary>
    public string? ChildPosition { get; set; }

    /// <summary>DMX patch (<c>Addresses/Address</c>), one per DMX break.</summary>
    public List<MvrAddress> Addresses { get; } = new();

    /// <summary>Network settings (<c>Addresses/Network</c>, MVR 1.6).</summary>
    public List<MvrNetwork> Networks { get; } = new();

    /// <summary>Control protocols (<c>Protocols/Protocol</c>, MVR 1.6).</summary>
    public List<MvrProtocol> Protocols { get; } = new();

    /// <summary>Beam alignments (<c>Alignments/Alignment</c>).</summary>
    public List<MvrAlignment> Alignments { get; } = new();

    /// <summary>Custom start-up commands (<c>CustomCommands/CustomCommand</c>), e.g. <c>"Body_Pan,f 50"</c>.</summary>
    public List<string> CustomCommands { get; } = new();

    /// <summary>Wheel/emitter/filter overwrites (<c>Overwrites/Overwrite</c>).</summary>
    public List<MvrOverwrite> Overwrites { get; } = new();

    /// <summary>Cable connections to other objects (<c>Connections/Connection</c>, MVR 1.6).</summary>
    public List<MvrConnection> Connections { get; } = new();

    /// <summary>Type of <see cref="CustomId"/> (<c>CustomIdType</c>), or null.</summary>
    public int? CustomIdType { get; set; }

    /// <summary>Custom id (<c>CustomId</c>), or null.</summary>
    public int? CustomId { get; set; }

    /// <summary>Attached objects (<c>ChildList</c>), relative to this object.</summary>
    public List<MvrObject> Children { get; } = new();

    /// <inheritdoc />
    public override IReadOnlyList<MvrObject> ChildObjects => Children;
}

/// <summary><c>SceneObject</c>: generic geometry; with a <see cref="MvrDeviceObject.GdtfSpec"/> a GDTF-driven device.</summary>
public sealed class MvrSceneObject : MvrDeviceObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.SceneObject;
}

/// <summary><c>Fixture</c>: a GDTF device placed in the scene, with patch, focus, colour and mappings.</summary>
public sealed class MvrFixture : MvrDeviceObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.Fixture;

    /// <summary>Focus point the fixture is aimed at (<c>Focus</c>, uuid of an <see cref="MvrFocusPoint"/>), or null.</summary>
    public Guid? Focus { get; set; }

    /// <summary>Pan DMX is inverted (<c>DMXInvertPan</c>, MVR 1.6), or null.</summary>
    public bool? DmxInvertPan { get; set; }

    /// <summary>Tilt DMX is inverted (<c>DMXInvertTilt</c>, MVR 1.6), or null.</summary>
    public bool? DmxInvertTilt { get; set; }

    /// <summary>Gel / identification colour as CIE 1931 xyY with Y in 0…100 (<c>Color</c>), or null.</summary>
    public CieColor? Color { get; set; }

    /// <summary>Video mappings (<c>Mappings/Mapping</c>).</summary>
    public List<MvrMapping> Mappings { get; } = new();

    /// <summary>Custom gobo (<c>Gobo</c>), or null.</summary>
    public MvrGobo? Gobo { get; set; }
}

/// <summary><c>Truss</c>: a structure fixtures hang on.</summary>
public sealed class MvrTruss : MvrDeviceObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.Truss;
}

/// <summary><c>Support</c>: a hoist, rope or ground support.</summary>
public sealed class MvrSupport : MvrDeviceObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.Support;

    /// <summary>Chain length in metres (<c>ChainLength</c>, written in millimetres), or null.</summary>
    public float? ChainLength { get; set; }
}

/// <summary><c>VideoScreen</c>: an LED wall or display.</summary>
public sealed class MvrVideoScreen : MvrDeviceObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.VideoScreen;

    /// <summary>Video inputs (<c>Sources/Source</c>).</summary>
    public List<MvrSource> Sources { get; } = new();
}

/// <summary><c>Projector</c>: a video projector.</summary>
public sealed class MvrProjector : MvrDeviceObject
{
    /// <inheritdoc />
    public override MvrObjectKind Kind => MvrObjectKind.Projector;

    /// <summary>What the projector shows (<c>Projections/Projection</c>).</summary>
    public List<MvrProjection> Projections { get; } = new();
}
