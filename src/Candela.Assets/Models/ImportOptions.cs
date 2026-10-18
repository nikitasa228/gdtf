namespace Candela.Assets;

/// <summary>Which axis of a model file points up.</summary>
public enum SourceUpAxis
{
    /// <summary>Use the file's metadata or the format's convention (see docs/assets.md).</summary>
    Auto,

    /// <summary>+Y is up (glTF, OBJ, Maya/Blender FBX); rotated so that +Y becomes +Z and +Z (the front) becomes −Y.</summary>
    Y,

    /// <summary>+Z is up (3ds Max, SketchUp, CAD, Candela itself); no rotation.</summary>
    Z,
}

/// <summary>
/// User choices for importing a 3D model. The persistent part (<see cref="UpAxis"/>, <see cref="UnitScale"/>,
/// <see cref="GuessUnits"/>) is stored with the asset so that a project re-imports a model identically.
/// </summary>
public sealed record ImportOptions
{
    /// <summary>Default options: axis and units from the file, unit guessing on.</summary>
    public static ImportOptions Default { get; } = new();

    /// <summary>Up axis of the file; <see cref="SourceUpAxis.Auto"/> trusts metadata / format conventions.</summary>
    public SourceUpAxis UpAxis { get; init; } = SourceUpAxis.Auto;

    /// <summary>
    /// Metres per file unit (0.001 for millimetres, 0.0254 for inches). Null uses the file's unit metadata (FBX
    /// UnitScaleFactor, COLLADA &lt;unit meter&gt;) or 1 for formats without units.
    /// </summary>
    public float? UnitScale { get; init; }

    /// <summary>
    /// For formats that carry no units (OBJ, STL, PLY, 3DS): a model larger than <see cref="GuessMillimetresAbove"/>
    /// in its largest dimension is assumed to be modelled in millimetres (a warning says so). Ignored when
    /// <see cref="UnitScale"/> is set.
    /// </summary>
    public bool GuessUnits { get; init; } = true;

    /// <summary>Extent (in file units read as metres) above which <see cref="GuessUnits"/> switches to millimetres.</summary>
    public const float GuessMillimetresAbove = 200f;

    /// <summary>Edges sharper than this get split normals when normals are generated (degrees).</summary>
    public float SmoothingAngleDegrees { get; init; } = 66f;

    /// <summary>
    /// Prefix put in front of every texture path in the produced materials, e.g. <c>asset:{guid}/</c> (see
    /// <see cref="AssetKeys.TexturePrefix"/>). Empty leaves paths relative to the model.
    /// </summary>
    public string TextureKeyPrefix { get; init; } = "";

    /// <summary>
    /// Models with more mesh-carrying nodes than this are flattened into a single part (transforms baked, one
    /// sub-mesh per material) to keep draw calls bounded.
    /// </summary>
    public int MaxParts { get; init; } = 1024;
}
