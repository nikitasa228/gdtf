using System.Numerics;
using Candela.Assets;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine;

/// <summary>
/// World-space bounds of a scene's content, for framing views ("auto" camera), sizing the automatic smoke box and
/// thumbnails. Uses the components' own extents (walls, floors, images, trusses, model bounds when supplied) and a
/// small box around every other entity, ignoring non-finite values. Groups and empty entities do not count.
/// </summary>
public static class SceneBounds
{
    /// <summary>Half size of the box used for entities without a known extent (a fixture, console, hoist), m.</summary>
    public const float DefaultHalfExtent = 0.3f;

    /// <summary>
    /// Bounds of every visible entity's content. <paramref name="modelBounds"/> returns the local bounds of an imported
    /// model asset (null: unknown, the entity's position is used). <paramref name="includeHidden"/> counts hidden
    /// entities too.
    /// </summary>
    public static Aabb Compute(Scene scene, Func<Guid, Aabb?>? modelBounds = null, bool includeHidden = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var bounds = Aabb.Empty;
        foreach (var entity in scene.All())
        {
            if (!includeHidden && !entity.IsVisibleInHierarchy) continue;
            Include(ref bounds, entity, modelBounds);
        }
        return bounds;
    }

    /// <summary>Bounds of one entity's own content (not its children), or an empty box.</summary>
    public static Aabb Of(Entity entity, Func<Guid, Aabb?>? modelBounds = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var b = Aabb.Empty;
        Include(ref b, entity, modelBounds);
        return b;
    }

    /// <summary>
    /// Appends the world-space corners of every visible entity's content box (8 per box; a single point for an entity
    /// without a known extent) to <paramref name="corners"/>: a tighter hull of the content than <see cref="Compute"/>'s
    /// box, for framing views (<see cref="ViewFraming.Frame(ReadOnlySpan{Vector3}, Vector3, float, float, float)"/>).
    /// </summary>
    public static void CollectContentCorners(Scene scene, List<Vector3> corners, Func<Guid, Aabb?>? modelBounds = null, bool includeHidden = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(corners);
        Span<Vector3> local = stackalloc Vector3[8];
        foreach (var entity in scene.All())
        {
            if (!includeHidden && !entity.IsVisibleInHierarchy) continue;
            var world = entity.WorldMatrix;
            bool any = false;
            foreach (var component in entity.Components)
            {
                if (!TryLocalBox(component, modelBounds, out var box)) continue;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                    local[i] = Vector3.Transform(c, world);
                }
                bool finite = true;
                foreach (var c in local) finite &= IsFinite(c);
                if (!finite) continue;
                foreach (var c in local) corners.Add(c);
                any = true;
            }
            if (!any && CountsAsPoint(entity) && IsFinite(world.Translation)) corners.Add(world.Translation);
        }
    }

    /// <summary>
    /// Appends the corners of a small box (± <paramref name="halfExtent"/> m) around every geometry part of every posed
    /// fixture: the fixture bodies as they hang and move (yoke and head below the mounting point), which the default
    /// box of a fixture entity does not follow. Use with <see cref="CollectContentCorners"/> for framing.
    /// </summary>
    public static void CollectFixtureParts(FixturePoses poses, List<Vector3> corners, float halfExtent = 0.15f)
    {
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(corners);
        float h = float.IsFinite(halfExtent) && halfExtent > 0f ? halfExtent : 0.15f;
        foreach (var pose in poses.All)
        {
            if (!pose.Entity.IsVisibleInHierarchy) continue;
            foreach (ref readonly var m in pose.PartWorld)
            {
                var p = m.Translation;
                if (!IsFinite(p)) continue;
                for (int i = 0; i < 8; i++)
                    corners.Add(p + new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h));
            }
        }
    }

    private static void Include(ref Aabb bounds, Entity entity, Func<Guid, Aabb?>? modelBounds)
    {
        var world = entity.WorldMatrix;
        bool any = false;
        foreach (var component in entity.Components)
        {
            if (!TryLocalBox(component, modelBounds, out var box)) continue;
            var transformed = box.Transform(world);
            if (!IsFinite(transformed)) continue;
            bounds.Include(transformed);
            any = true;
        }
        if (!any && CountsAsPoint(entity))
        {
            var p = world.Translation;
            if (IsFinite(p)) bounds.Include(p);
        }
    }

    /// <summary>The local box of a component's content; false for components without extent (groups, cameras …).</summary>
    private static bool TryLocalBox(Component component, Func<Guid, Aabb?>? modelBounds, out Aabb box)
    {
        Aabb? local = component switch
        {
            WallComponent wall => StageSimulation.TryWallBox(wall, out var min, out var max) ? new Aabb(min, max) : null,
            ImageComponent image => ImageBox(image),
            TrussComponent truss => truss.ModelAssetId is { } id && modelBounds?.Invoke(id) is { } tb ? tb : TrussBox(truss),
            MeshComponent mesh => modelBounds?.Invoke(mesh.ModelAssetId),
            GroupComponent => Aabb.Empty,
            AtmosphereVolumeComponent => Aabb.Empty,
            CameraComponent => Aabb.Empty,
            CableComponent => Aabb.Empty,
            _ => new Aabb(new Vector3(-DefaultHalfExtent), new Vector3(DefaultHalfExtent)),
        };
        box = local ?? Aabb.Empty;
        return local is { } b && !b.IsEmpty && IsFinite(b);
    }

    /// <summary>An entity with components but no known extent counts as its position (a model that cannot be decoded).</summary>
    private static bool CountsAsPoint(Entity entity) =>
        entity.Components.Count > 0 && !entity.Has<GroupComponent>() && !entity.Has<CableComponent>()
        && !entity.Has<AtmosphereVolumeComponent>() && !entity.Has<CameraComponent>();

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Aabb ImageBox(ImageComponent image)
    {
        float w = Positive(image.Width) * 0.5f, h = Positive(image.Height) * 0.5f;
        return new Aabb(new Vector3(-w, -0.005f, -h), new Vector3(w, 0.005f, h));
    }

    private static Aabb TrussBox(TrussComponent truss)
    {
        var profile = TrussProfile.Parse(truss.Profile, truss.Kind);
        float half = 0.5f * MathF.Max(profile.Size, 0.05f);
        return new Aabb(new Vector3(0f, -half, -half), new Vector3(Positive(truss.Length), half, half));
    }

    private static float Positive(float v) => float.IsFinite(v) && v > 0f ? v : 1f;

    private static bool IsFinite(Aabb b) =>
        float.IsFinite(b.Min.X) && float.IsFinite(b.Min.Y) && float.IsFinite(b.Min.Z) &&
        float.IsFinite(b.Max.X) && float.IsFinite(b.Max.Y) && float.IsFinite(b.Max.Z);
}
