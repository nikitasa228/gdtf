using System.Numerics;

namespace Candela.Core.SceneGraph.Components;

/// <summary>An imported 3D model (asset stored in the project) with optional per-slot material overrides.</summary>
public sealed class MeshComponent : Component
{
    /// <summary>The imported model asset of the project.</summary>
    [AssetReference]
    public Guid ModelAssetId { get; set; }

    /// <summary>Material slot index of the model → material id (see <see cref="MaterialIds"/>).</summary>
    public Dictionary<int, string> MaterialOverrides { get; set; } = new();

    /// <summary>True when the model casts shadows.</summary>
    public bool CastShadow { get; set; } = true;

    /// <inheritdoc/>
    protected internal override void RemapReferences(EntityReferenceRemapper remapper) => ModelAssetId = remapper.RemapAsset(ModelAssetId);
}

/// <summary>Shape and orientation of a <see cref="WallComponent"/>.</summary>
public enum WallKind
{
    /// <summary>Vertical: width along local X, height along local Z, thickness along local Y.</summary>
    Wall,

    /// <summary>Horizontal, top face at the entity origin: width along X, length along Y, thickness downwards.</summary>
    Floor,

    /// <summary>Horizontal, bottom face at the entity origin: width along X, length along Y, thickness upwards.</summary>
    Ceiling,
}

/// <summary>A wall, floor or ceiling drawn in the editor as a resizable rectangle (a thin box).</summary>
public sealed class WallComponent : Component
{
    /// <summary>Wall, floor or ceiling (orientation of the box).</summary>
    public WallKind Kind { get; set; } = WallKind.Wall;

    /// <summary>Metres along local X.</summary>
    public float Width { get; set; } = 4f;

    /// <summary>Metres along local Z (walls) or local Y (floors, ceilings).</summary>
    public float Height { get; set; } = 3f;

    /// <summary>Metres (local Y for walls, downwards for floors, upwards for ceilings).</summary>
    public float Thickness { get; set; } = 0.1f;

    /// <summary>Material id (<see cref="MaterialIds"/> or a project asset key).</summary>
    public string MaterialId { get; set; } = MaterialIds.PaintedWallWhite;

    /// <summary>Texture repeats per metre (1 = a texture tile covers 1 m).</summary>
    public float UvScale { get; set; } = 1f;
}

/// <summary>How an <see cref="ImageComponent"/> is drawn.</summary>
public enum ImageMode
{
    /// <summary>Unlit, semi-transparent drawing used for tracing (plans, sections).</summary>
    Reference,

    /// <summary>A lit printed surface (banner, poster) that reacts to the stage light.</summary>
    Poster,
}

/// <summary>An image placed in the scene on a rectangle in the local XZ plane (facing −Y).</summary>
public sealed class ImageComponent : Component
{
    /// <summary>The image asset of the project.</summary>
    [AssetReference]
    public Guid ImageAssetId { get; set; }

    /// <summary>Reference drawing (unlit, translucent) or lit poster.</summary>
    public ImageMode Mode { get; set; } = ImageMode.Reference;

    /// <summary>Metres along local X.</summary>
    public float Width { get; set; } = 2f;

    /// <summary>Metres along local Z.</summary>
    public float Height { get; set; } = 1f;

    /// <summary>0 (invisible) … 1 (opaque).</summary>
    public float Opacity { get; set; } = 0.6f;

    /// <summary>True when the back face shows the image too.</summary>
    public bool DoubleSided { get; set; } = true;

    /// <inheritdoc/>
    protected internal override void RemapReferences(EntityReferenceRemapper remapper) => ImageAssetId = remapper.RemapAsset(ImageAssetId);
}

/// <summary>Kind of load-carrying structure of a <see cref="TrussComponent"/>.</summary>
public enum TrussKind
{
    /// <summary>A lattice truss (box or triangle profile).</summary>
    Truss,

    /// <summary>A single pipe (bar).</summary>
    Pipe,

    /// <summary>A ladder truss.</summary>
    Ladder,

    /// <summary>A vertical ground-support tower.</summary>
    Tower,
}

/// <summary>
/// A load-carrying structure fixtures hang on. Length runs along local X from the entity origin.
/// Capacities are those of the structure itself; rigging points add their own limits.
/// </summary>
public sealed class TrussComponent : Component
{
    /// <summary>Kind of structure.</summary>
    public TrussKind Kind { get; set; } = TrussKind.Truss;

    /// <summary>Metres along local X.</summary>
    public float Length { get; set; } = 3f;

    /// <summary>Cross-section label (e.g. "F34", "F44", "Ø48 pipe").</summary>
    public string Profile { get; set; } = "F34";

    /// <summary>Own weight in kg (0 = take it from the model / profile table).</summary>
    public float WeightKg { get; set; }

    /// <summary>Maximum uniformly distributed load in kg (0 = unknown).</summary>
    public float MaxUniformLoadKg { get; set; }

    /// <summary>Maximum single point load at mid-span in kg (0 = unknown).</summary>
    public float MaxPointLoadKg { get; set; }

    /// <summary>Optional model asset; when empty the renderer generates the profile procedurally.</summary>
    [AssetReference]
    public Guid? ModelAssetId { get; set; }

    /// <inheritdoc/>
    protected internal override void RemapReferences(EntityReferenceRemapper remapper) => ModelAssetId = remapper.RemapAsset(ModelAssetId);
}

/// <summary>A hoist / hanging point that carries a structure (MVR Support).</summary>
public sealed class RiggingPointComponent : Component
{
    /// <summary>Working load limit in kg.</summary>
    public float CapacityKg { get; set; } = 500f;

    /// <summary>Entities (usually trusses) suspended from this point.</summary>
    [EntityReference]
    public List<Guid> Carries { get; set; } = new();

    /// <inheritdoc/>
    protected internal override void RemapReferences(EntityReferenceRemapper remapper) => remapper.RemapAll(Carries);
}

/// <summary>Marks an entity that only groups children (MVR GroupObject, editor folders).</summary>
public sealed class GroupComponent : Component
{
}

/// <summary>Built-in material ids (the material library lives in Candela.Assets).</summary>
public static class MaterialIds
{
    public const string Default = "builtin:default";
    public const string PaintedWallWhite = "builtin:painted-wall-white";
    public const string PaintedWallBlack = "builtin:painted-wall-black";
    public const string Concrete = "builtin:concrete";
    public const string StageFloorBlack = "builtin:stage-floor-black";
    public const string DanceFloorWhite = "builtin:dance-floor-white";
    public const string WoodStage = "builtin:wood-stage";
    public const string MoltonBlack = "builtin:molton-black";
    public const string AluminiumTruss = "builtin:aluminium-truss";
    public const string SteelBlack = "builtin:steel-black";
    public const string Mirror = "builtin:mirror";
    public const string Glass = "builtin:glass";
    public const string LedScreenOff = "builtin:led-screen-off";
    public const string Cyclorama = "builtin:cyclorama";
    public const string Carpet = "builtin:carpet";
}

/// <summary>Saved camera / view.</summary>
public sealed class CameraComponent : Component
{
    /// <summary>True for an orthographic projection, false for perspective.</summary>
    public bool Orthographic { get; set; }

    /// <summary>Vertical field of view in degrees (perspective).</summary>
    public float FovYDegrees { get; set; } = 50f;

    /// <summary>Visible height in metres (orthographic).</summary>
    public float OrthoHeight { get; set; } = 10f;

    /// <summary>Exposure value at ISO 100 (physical camera).</summary>
    public float Ev100 { get; set; } = 6f;

    /// <summary>
    /// The point the view looks at, in world space (metres, Z up), or null to look along the entity's orientation. The
    /// camera position is the entity's world position. Copies keep the point unchanged (it does not follow the entity);
    /// a merge that moves the imported scene moves it with the placement.
    /// </summary>
    public Vector3? Target { get; set; }
}
