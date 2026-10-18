using System.Numerics;

namespace Candela.Rendering.Vulkan;

/// <summary>One indexed draw: a sub-mesh of an instance with its material.</summary>
internal struct DrawItem
{
    /// <summary>Instance slot (index into the instance buffer).</summary>
    public int Instance;

    public uint IndexStart;
    public uint IndexCount;

    /// <summary>Index into the material buffer (0 = default material).</summary>
    public uint Material;

    /// <summary>Back faces are drawn (no culling).</summary>
    public bool DoubleSided;

    /// <summary>Distance of the instance's bounding-sphere centre along the view direction, m.</summary>
    public float ViewDepth;
}

/// <summary>
/// View frustum planes for culling bounding spheres (planes point inwards, normalised). Extracted from the
/// reverse-Z view-projection matrix (System.Numerics row-vector convention: clip = v · M): left/right
/// (x = ±w), top/bottom (y = ±w), near (depth ≤ 1 ⇔ z ≤ w) and, for finite projections, far (depth ≥ 0 ⇔ z ≥ 0).
/// </summary>
internal struct Frustum
{
    private Vector4 _p0, _p1, _p2, _p3, _p4, _p5;
    private int _count;

    public static Frustum FromViewProjection(in Matrix4x4 m, bool finiteFar)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        var f = new Frustum
        {
            _p0 = Normalize(c4 + c1),
            _p1 = Normalize(c4 - c1),
            _p2 = Normalize(c4 + c2),
            _p3 = Normalize(c4 - c2),
            _p4 = Normalize(c4 - c3),
            _p5 = Normalize(c3),
            _count = finiteFar ? 6 : 5,
        };
        return f;
    }

    /// <summary>True when the sphere (xyz centre, w radius) is at least partly inside.</summary>
    public readonly bool Intersects(Vector4 sphere)
    {
        var c = new Vector3(sphere.X, sphere.Y, sphere.Z);
        float r = sphere.W;
        if (!float.IsFinite(r) || !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z))
            return true;
        return Inside(_p0, c, r) && Inside(_p1, c, r) && Inside(_p2, c, r) && Inside(_p3, c, r) && Inside(_p4, c, r) &&
               (_count < 6 || Inside(_p5, c, r));
    }

    private static bool Inside(Vector4 plane, Vector3 c, float r) => plane.X * c.X + plane.Y * c.Y + plane.Z * c.Z + plane.W >= -r;

    private static Vector4 Normalize(Vector4 p)
    {
        float len = new Vector3(p.X, p.Y, p.Z).Length();
        return len > 1e-20f ? p / len : new Vector4(0f, 0f, 0f, 1f); // degenerate plane: accept everything
    }
}

/// <summary>
/// The draws of one view, built from the world's live instances: hidden instances and instances outside the view
/// frustum are skipped; each sub-mesh is sorted into the opaque, alpha-tested or blended list by its material
/// (reference drawings are blended); blended draws are sorted back to front. Arrays grow on demand and are reused,
/// so building allocates nothing in steady state.
/// </summary>
internal sealed class DrawList
{
    private static readonly Comparison<DrawItem> BackToFront = static (a, b) => b.ViewDepth.CompareTo(a.ViewDepth);
    private DrawItem[] _opaque = new DrawItem[256];
    private DrawItem[] _masked = new DrawItem[64];
    private DrawItem[] _blended = new DrawItem[64];
    private int _opaqueCount;
    private int _maskedCount;
    private int _blendedCount;

    public ReadOnlySpan<DrawItem> Opaque => _opaque.AsSpan(0, _opaqueCount);

    public ReadOnlySpan<DrawItem> Masked => _masked.AsSpan(0, _maskedCount);

    public ReadOnlySpan<DrawItem> Blended => _blended.AsSpan(0, _blendedCount);

    /// <summary>Instances drawn in the last build.</summary>
    public int VisibleInstances { get; private set; }

    /// <summary>Instances rejected by frustum culling in the last build.</summary>
    public int CulledInstances { get; private set; }

    /// <summary>Visible instances with <see cref="InstanceFlags.Selected"/> in the last build.</summary>
    public int SelectedInstances { get; private set; }

    /// <summary>Rebuilds the lists.</summary>
    /// <param name="world">The scene.</param>
    /// <param name="frustum">View frustum.</param>
    /// <param name="cameraPosition">Camera position, world space.</param>
    /// <param name="forward">Unit view direction.</param>
    /// <param name="useMaterials">Full view: materials decide the list. Otherwise every draw is opaque.</param>
    public void Build(VulkanRenderWorld world, in Frustum frustum, Vector3 cameraPosition, Vector3 forward, bool useMaterials)
    {
        _opaqueCount = _maskedCount = _blendedCount = 0;
        int visible = 0, culled = 0, selected = 0;
        foreach (int index in world.LiveInstances)
        {
            var instance = world.InstanceAt(index);
            var mesh = instance.Mesh;
            if ((instance.Flags & InstanceFlags.Hidden) != 0 || mesh.Destroyed || mesh.Geometry is null || mesh.IndexCount == 0)
                continue;
            if (!frustum.Intersects(instance.WorldSphere))
            {
                culled++;
                continue;
            }

            visible++;
            if ((instance.Flags & InstanceFlags.Selected) != 0)
                selected++;
            var centre = new Vector3(instance.WorldSphere.X, instance.WorldSphere.Y, instance.WorldSphere.Z);
            float depth = Vector3.Dot(centre - cameraPosition, forward);
            bool reference = (instance.Flags & InstanceFlags.Reference) != 0;
            foreach (var sub in mesh.SubMeshes)
            {
                if (sub.IndexCount <= 0)
                    continue;
                var material = sub.MaterialSlot < instance.Materials.Length ? instance.Materials[sub.MaterialSlot] : null;
                var item = new DrawItem
                {
                    Instance = index,
                    IndexStart = (uint)sub.IndexStart,
                    IndexCount = (uint)sub.IndexCount,
                    Material = material is null ? 0u : (uint)material.Index,
                    DoubleSided = material?.DoubleSided ?? false,
                    ViewDepth = depth,
                };
                var kind = !useMaterials ? DrawKind.Opaque
                    : reference ? DrawKind.Blend
                    : material?.Kind ?? DrawKind.Opaque;
                switch (kind)
                {
                    case DrawKind.Mask:
                        Add(ref _masked, ref _maskedCount, item);
                        break;
                    case DrawKind.Blend:
                        Add(ref _blended, ref _blendedCount, item);
                        break;
                    default:
                        Add(ref _opaque, ref _opaqueCount, item);
                        break;
                }
            }
        }

        VisibleInstances = visible;
        CulledInstances = culled;
        SelectedInstances = selected;
        if (_blendedCount > 1)
            _blended.AsSpan(0, _blendedCount).Sort(BackToFront);
    }

    private static void Add(ref DrawItem[] array, ref int count, in DrawItem item)
    {
        if (count == array.Length)
            Array.Resize(ref array, array.Length * 2);
        array[count++] = item;
    }
}
