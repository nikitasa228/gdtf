using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine;

/// <summary>
/// The stage floor added under a scene that has none. MVR exports often contain only the rig (Depence's
/// <c>stages/Фокина.mvr</c>: 91 fixtures and nothing else), so the light lands on nothing: no pools, no lux, no bounce.
/// The CLI's <c>candela render --floor auto</c> adds this floor for the render and the editor adds it, as one undoable
/// step, when it opens a floorless MVR; both use <see cref="CreateIfMissing"/>.
/// <para>
/// The floor is a <see cref="WallKind.Floor"/> wall of <see cref="MaterialIds.WoodStage"/>, <see cref="Thickness"/> thick,
/// covering the content's footprint plus <see cref="Margin"/> on every side, with its top face at the lowest point the
/// content rests on (<see cref="SceneBounds.LowestPoint"/>: a floor-standing fixture's base, not the framing box around
/// it; at most Z = 0, the stage level of right-handed Z-up scenes). A scene has a floor when it holds a floor wall
/// (visible or not: a hidden floor is the user's choice), or when an imported model reaches down to that lowest point
/// and covers at least <see cref="ModelCoverage"/> of the content's footprint — a venue or set model brings its own
/// floor, and a second one at the same height would only z-fight with it.
/// </para>
/// </summary>
public static class StageFloor
{
    /// <summary>Distance the floor extends beyond the content's footprint on every side, m.</summary>
    public const float Margin = 3f;

    /// <summary>Thickness of the floor slab, m (its top face is the stage level).</summary>
    public const float Thickness = 0.1f;

    /// <summary>
    /// A model whose bottom lies within this distance of the lowest point the content rests on (m) can be the venue's floor
    /// (see <see cref="ModelCoverage"/>).
    /// </summary>
    public const float ModelFloorTolerance = 0.05f;

    /// <summary>Share of the content's footprint (0 … 1) a model must cover at the bottom to count as the venue's floor.</summary>
    public const float ModelCoverage = 0.5f;

    /// <summary>Name of the floor entity when the caller gives none.</summary>
    public const string DefaultName = "Stage floor";

    /// <summary>
    /// True when <paramref name="scene"/> already has a floor: a <see cref="WallKind.Floor"/> wall, or a visible model
    /// that is the venue's floor (see <see cref="StageFloor"/>). <paramref name="modelBounds"/> returns the local bounds
    /// of an imported model asset (null: unknown; such a model cannot count as a floor).
    /// </summary>
    public static bool HasFloor(Scene scene, Func<Guid, Aabb?>? modelBounds = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (HasFloorWall(scene)) return true;
        modelBounds = Cached(modelBounds);
        var content = SceneBounds.Compute(scene, modelBounds);
        return !content.IsEmpty && SceneBounds.LowestPoint(scene, modelBounds) is { } lowest && HasFloorModel(scene, modelBounds, content, lowest);
    }

    /// <summary>
    /// A new stage floor entity for <paramref name="scene"/> (not added to it: the caller adds it directly or through an
    /// undoable command), or null when the scene already has a floor (<see cref="HasFloor"/>) or has no content to put
    /// a floor under. <paramref name="name"/> is the entity's name (default <see cref="DefaultName"/>).
    /// </summary>
    public static Entity? CreateIfMissing(Scene scene, Func<Guid, Aabb?>? modelBounds = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (HasFloorWall(scene)) return null;
        modelBounds = Cached(modelBounds);
        var content = SceneBounds.Compute(scene, modelBounds);
        if (content.IsEmpty || !IsFinite(content.Min) || !IsFinite(content.Max) || SceneBounds.LowestPoint(scene, modelBounds) is not { } lowest ||
            HasFloorModel(scene, modelBounds, content, lowest))
            return null;

        var size = new Vector2(content.Size.X + 2f * Margin, content.Size.Y + 2f * Margin);
        var floor = new Entity(string.IsNullOrWhiteSpace(name) ? DefaultName : name)
        {
            Local = new Transform(new Vector3(content.Center.X, content.Center.Y, MathF.Min(0f, lowest)), Quaternion.Identity, Vector3.One),
        };
        floor.Add(new WallComponent
        {
            Kind = WallKind.Floor,
            Width = size.X,
            Height = size.Y,
            Thickness = Thickness,
            MaterialId = MaterialIds.WoodStage,
        });
        return floor;
    }

    /// <summary>
    /// Adds a stage floor to the engine's scene when it has none (directly, not undoable: the CLI's render) and returns
    /// it, or null when the scene has a floor or no content. Model bounds come from the engine's model store.
    /// </summary>
    public static Entity? AddIfMissing(StageEngine engine, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var floor = CreateIfMissing(engine.Scene, engine.Models.GetBounds, name);
        if (floor is not null) engine.Scene.Add(floor);
        return floor;
    }

    /// <summary>Width (X) and depth (Y) of a floor made by <see cref="CreateIfMissing"/>, m (zero for any other entity).</summary>
    public static Vector2 SizeOf(Entity floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        return floor.Get<WallComponent>() is { Kind: WallKind.Floor } wall ? new Vector2(wall.Width, wall.Height) : Vector2.Zero;
    }

    private static bool HasFloorWall(Scene scene)
    {
        foreach (var (_, wall) in scene.Query<WallComponent>())
        {
            if (wall.Kind == WallKind.Floor) return true;
        }

        return false;
    }

    /// <summary>A visible model reaches down to the lowest point the content rests on and covers most of its footprint.</summary>
    private static bool HasFloorModel(Scene scene, Func<Guid, Aabb?>? modelBounds, Aabb content, float lowest)
    {
        if (modelBounds is null) return false;
        float footprint = MathF.Max(content.Size.X, 1e-3f) * MathF.Max(content.Size.Y, 1e-3f);
        foreach (var (entity, _) in scene.Query<MeshComponent>())
        {
            if (!entity.IsVisibleInHierarchy) continue;
            var box = SceneBounds.Of(entity, modelBounds);
            if (box.IsEmpty || box.Min.Z > lowest + ModelFloorTolerance) continue;
            float overlapX = MathF.Min(box.Max.X, content.Max.X) - MathF.Max(box.Min.X, content.Min.X);
            float overlapY = MathF.Min(box.Max.Y, content.Max.Y) - MathF.Max(box.Min.Y, content.Min.Y);
            if (overlapX > 0f && overlapY > 0f && overlapX * overlapY >= ModelCoverage * footprint) return true;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="modelBounds"/> asked once per model asset: the bounds of a decoded model are computed from all its
    /// vertices, and one model (a truss piece, a chair) is often placed hundreds of times.
    /// </summary>
    private static Func<Guid, Aabb?>? Cached(Func<Guid, Aabb?>? modelBounds)
    {
        if (modelBounds is null) return null;
        var known = new Dictionary<Guid, Aabb?>();
        return id =>
        {
            if (!known.TryGetValue(id, out var bounds))
            {
                bounds = modelBounds(id);
                known[id] = bounds;
            }

            return bounds;
        };
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
