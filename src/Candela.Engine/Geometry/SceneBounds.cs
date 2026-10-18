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

    private static void Include(ref Aabb bounds, Entity entity, Func<Guid, Aabb?>? modelBounds)
    {
        var world = entity.WorldMatrix;
        bool any = false;
        foreach (var component in entity.Components)
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
            if (local is not { } box || box.IsEmpty || !IsFinite(box)) continue;
            var transformed = box.Transform(world);
            if (!IsFinite(transformed)) continue;
            bounds.Include(transformed);
            any = true;
        }
        if (!any && entity.Components.Count > 0 && !entity.Has<GroupComponent>() && !entity.Has<CableComponent>()
            && !entity.Has<AtmosphereVolumeComponent>() && !entity.Has<CameraComponent>())
        {
            var p = world.Translation;
            if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)) bounds.Include(p);
        }
    }

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
