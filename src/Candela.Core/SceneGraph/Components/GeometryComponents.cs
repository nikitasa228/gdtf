using System.Numerics;

namespace Candela.Core.SceneGraph.Components;

/// <summary>An imported 3D model (asset stored in the project) with optional per-slot material overrides.</summary>
public sealed class MeshComponent : Component
{
    public Guid ModelAssetId { get; set; }

    /// <summary>Material slot index of the model → material id (see <see cref="MaterialIds"/>).</summary>
    public Dictionary<int, string> MaterialOverrides { get; set; } = new();

    public bool CastShadow { get; set; } = true;
}

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
    public WallKind Kind { get; set; } = WallKind.Wall;

    /// <summary>Metres along local X.</summary>
    public float Width { get; set; } = 4f;

    /// <summary>Metres along local Z (walls) or local Y (floors, ceilings).</summary>
    public float Height { get; set; } = 3f;

    public float Thickness { get; set; } = 0.1f;

    public string MaterialId { get; set; } = MaterialIds.PaintedWallWhite;

    /// <summary>Texture repeats per metre (1 = a texture tile covers 1 m).</summary>
    public float UvScale { get; set; } = 1f;
}

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
    public Guid ImageAssetId { get; set; }
    public ImageMode Mode { get; set; } = ImageMode.Reference;
    public float Width { get; set; } = 2f;
    public float Height { get; set; } = 1f;
    public float Opacity { get; set; } = 0.6f;
    public bool DoubleSided { get; set; } = true;
}

public enum TrussKind
{
    Truss,
    Pipe,
    Ladder,
    Tower,
}

/// <summary>
/// A load-carrying structure fixtures hang on. Length runs along local X from the entity origin.
/// Capacities are those of the structure itself; rigging points add their own limits.
/// </summary>
public sealed class TrussComponent : Component
{
    public TrussKind Kind { get; set; } = TrussKind.Truss;
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
    public Guid? ModelAssetId { get; set; }
}

/// <summary>A hoist / hanging point that carries a structure (MVR Support).</summary>
public sealed class RiggingPointComponent : Component
{
    public float CapacityKg { get; set; } = 500f;

    /// <summary>Entities (usually trusses) suspended from this point.</summary>
    public List<Guid> Carries { get; set; } = new();
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
    public bool Orthographic { get; set; }

    /// <summary>Vertical field of view in degrees (perspective).</summary>
    public float FovYDegrees { get; set; } = 50f;

    /// <summary>Visible height in metres (orthographic).</summary>
    public float OrthoHeight { get; set; } = 10f;

    /// <summary>Exposure value at ISO 100 (physical camera).</summary>
    public float Ev100 { get; set; } = 6f;

    public Vector3? Target { get; set; }
}
