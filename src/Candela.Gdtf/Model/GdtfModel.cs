using System.Numerics;

namespace Candela.Gdtf;

/// <summary>Built-in model shape used when a model has no file (GDTF <c>PrimitiveType</c>).</summary>
public enum GdtfPrimitiveType
{
    /// <summary>No built-in shape: the model comes from a file (or is invisible when there is none).</summary>
    Undefined,

    /// <summary>Box.</summary>
    Cube,

    /// <summary>Cylinder along Z.</summary>
    Cylinder,

    /// <summary>Sphere / ellipsoid.</summary>
    Sphere,

    /// <summary>Moving-head base.</summary>
    Base,

    /// <summary>Moving-head yoke.</summary>
    Yoke,

    /// <summary>Moving-head head.</summary>
    Head,

    /// <summary>Scanner body with mirror.</summary>
    Scanner,

    /// <summary>Conventional (PAR / profile) body.</summary>
    Conventional,

    /// <summary>Pigtail (cable stub).</summary>
    Pigtail,

    /// <summary>Base, GDTF 1.1 shape.</summary>
    Base1_1,

    /// <summary>Scanner, GDTF 1.1 shape.</summary>
    Scanner1_1,

    /// <summary>Conventional, GDTF 1.1 shape.</summary>
    Conventional1_1,
}

/// <summary>
/// A GDTF model definition: declared size in metres and either a model file (<c>models/gltf/{File}.glb</c>,
/// <c>models/3ds/{File}.3ds</c>, SVG views in <c>models/svg*/</c>) or a built-in primitive.
/// </summary>
public sealed class GdtfModel
{
    /// <summary>Unique name, referenced by geometries.</summary>
    public string Name { get; set; } = "";

    /// <summary>Extent along X in metres.</summary>
    public float Length { get; set; }

    /// <summary>Extent along Y in metres.</summary>
    public float Width { get; set; }

    /// <summary>Extent along Z in metres.</summary>
    public float Height { get; set; }

    /// <summary>Declared size (X = Length, Y = Width, Z = Height) in metres.</summary>
    public Vector3 Size => new(Length, Width, Height);

    /// <summary>Built-in shape used when there is no file.</summary>
    public GdtfPrimitiveType PrimitiveType { get; set; }

    /// <summary>Model file name without extension and folder, or null.</summary>
    public string? File { get; set; }

    /// <summary>Offset of the top-view SVG in metres (X).</summary>
    public float SvgOffsetX { get; set; }

    /// <summary>Offset of the top-view SVG in metres (Y).</summary>
    public float SvgOffsetY { get; set; }

    /// <summary>Offset of the side-view SVG in metres (X).</summary>
    public float SvgSideOffsetX { get; set; }

    /// <summary>Offset of the side-view SVG in metres (Y).</summary>
    public float SvgSideOffsetY { get; set; }

    /// <summary>Offset of the front-view SVG in metres (X).</summary>
    public float SvgFrontOffsetX { get; set; }

    /// <summary>Offset of the front-view SVG in metres (Y).</summary>
    public float SvgFrontOffsetY { get; set; }

    /// <inheritdoc />
    public override string ToString() => Name;
}
