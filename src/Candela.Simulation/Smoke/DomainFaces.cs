namespace Candela.Simulation;

/// <summary>
/// The six faces of the axis-aligned simulation box. Used to say which faces are <b>open</b> (air and smoke flow in and
/// out freely; pressure is held at ambient) — every other face is a closed, free-slip wall.
/// </summary>
[Flags]
public enum DomainFaces
{
    /// <summary>No face: a closed box.</summary>
    None = 0,

    /// <summary>The face at minimum X.</summary>
    XMin = 1 << 0,

    /// <summary>The face at maximum X.</summary>
    XMax = 1 << 1,

    /// <summary>The face at minimum Y.</summary>
    YMin = 1 << 2,

    /// <summary>The face at maximum Y.</summary>
    YMax = 1 << 3,

    /// <summary>The floor face (minimum Z).</summary>
    ZMin = 1 << 4,

    /// <summary>The ceiling face (maximum Z).</summary>
    ZMax = 1 << 5,

    /// <summary>The four vertical side faces.</summary>
    Sides = XMin | XMax | YMin | YMax,

    /// <summary>Every face except the floor: the default for a stage, where the floor is solid and smoke can leave the box elsewhere.</summary>
    AllButFloor = Sides | ZMax,

    /// <summary>All six faces.</summary>
    All = Sides | ZMin | ZMax,
}
