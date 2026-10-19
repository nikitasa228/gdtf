using System.Numerics;
using System.Runtime.InteropServices;

namespace Candela.Rendering.Vulkan;

/// <summary>Layout of <c>VkAabbPositionsKHR</c> (24 bytes): a procedural primitive's box; a NaN minimum x makes it inactive.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AabbPositions
{
    public float MinX;
    public float MinY;
    public float MinZ;
    public float MaxX;
    public float MaxY;
    public float MaxZ;

    /// <summary>An inactive box (never reported to ray queries).</summary>
    public static AabbPositions Inactive => new() { MinX = float.NaN, MinY = float.NaN, MinZ = float.NaN, MaxX = float.NaN, MaxY = float.NaN, MaxZ = float.NaN };

    public Vector3 Min => new(MinX, MinY, MinZ);

    public Vector3 Max => new(MaxX, MaxY, MaxZ);

    public bool IsActive => !float.IsNaN(MinX);
}

/// <summary>
/// CPU geometry of the path tracer's light snapshot: the box that bounds each light's cone (ray queries find the
/// lights that can reach a point or a ray segment through these boxes) and the lens polygon of each light (what rays
/// see and what next-event estimation samples), plus the bounds the haze fills.
/// </summary>
internal static class PathTracerScene
{
    /// <summary>How far the haze extends around the scene, m (as far as the real-time froxels follow a beam).</summary>
    public const float BeamReach = 30f;

    /// <summary>Largest extent of the media box from the origin, m (keeps float precision sensible).</summary>
    public const float MaxExtent = 1e4f;

    /// <summary>The lens polygon sits this far in front of the light's centre, m (in front of the fixture's lens mesh).</summary>
    public const float LensOffset = 0.005f;

    /// <summary>Half-angle above which a light's box is the whole media box (cones wider than this are not bounded tighter).</summary>
    public const float WideConeAngle = 1.4f;

    /// <summary>Vertices of one lens polygon (centre + rim).</summary>
    public const int LensVertices = (int)ShaderConstants.PtDiscSegments + 1;

    /// <summary>Indices of one lens polygon (a fan of triangles).</summary>
    public const int LensIndices = 3 * (int)ShaderConstants.PtDiscSegments;

    /// <summary>
    /// Circumradius / radius of the regular lens polygon with the area of the lens disc: √(2π / (n·sin(2π/n))), so the
    /// polygon's area is exactly πR².
    /// </summary>
    public static float PolygonRadiusFactor
    {
        get
        {
            double n = ShaderConstants.PtDiscSegments;
            return (float)Math.Sqrt(2.0 * Math.PI / (n * Math.Sin(2.0 * Math.PI / n)));
        }
    }

    /// <summary>A light emits (finite, positive intensity in some channel).</summary>
    public static bool Emits(in GpuLight light) =>
        float.IsFinite(light.MaxIntensity) && light.MaxIntensity > 0f && float.IsFinite(light.Position.X) && float.IsFinite(light.Position.Y) &&
        float.IsFinite(light.Position.Z);

    /// <summary>
    /// The box of the light's influence inside <paramref name="boxMin"/>…<paramref name="boxMax"/>: the spherical cone
    /// sector of its bounding angle (cut-off, iris, prism spread) reaching the farthest corner of the box, grown by the
    /// lens; the whole box for cones wider than <see cref="WideConeAngle"/>. Inactive when the light does not emit.
    /// </summary>
    public static AabbPositions LightVolume(in GpuLight light, Vector3 boxMin, Vector3 boxMax)
    {
        if (!Emits(light))
            return AabbPositions.Inactive;
        var p = light.Position;
        float pad = MathF.Max(light.SourceRadius, 0f) * PolygonRadiusFactor + LensOffset + 0.01f;
        var min = p;
        var max = p;
        float theta = float.IsFinite(light.BoundingAngle) ? light.BoundingAngle : MathF.PI;
        var a = light.Direction;
        if (theta >= WideConeAngle || !(a.LengthSquared() > 0.5f))
        {
            min = Vector3.Min(min, boxMin);
            max = Vector3.Max(max, boxMax);
        }
        else
        {
            float reach = 1e-3f;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) != 0 ? boxMax.X : boxMin.X, (i & 2) != 0 ? boxMax.Y : boxMin.Y, (i & 4) != 0 ? boxMax.Z : boxMin.Z);
                reach = MathF.Max(reach, Vector3.Distance(corner, p));
            }

            float cos = MathF.Cos(theta), sin = MathF.Sin(theta);
            // the cap's rim: a circle of radius R sin θ around p + a R cos θ in the plane normal to a
            var centre = p + a * (reach * cos);
            float r = reach * sin;
            var extent = new Vector3(r * MathF.Sqrt(MathF.Max(1f - a.X * a.X, 0f)), r * MathF.Sqrt(MathF.Max(1f - a.Y * a.Y, 0f)),
                r * MathF.Sqrt(MathF.Max(1f - a.Z * a.Z, 0f)));
            min = Vector3.Min(min, centre - extent);
            max = Vector3.Max(max, centre + extent);
            // the sphere's extreme points that lie inside the cone
            for (int axis = 0; axis < 3; axis++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    var e = Vector3.Zero;
                    if (axis == 0)
                        e.X = sign;
                    else if (axis == 1)
                        e.Y = sign;
                    else
                        e.Z = sign;
                    if (Vector3.Dot(e, a) >= cos)
                    {
                        min = Vector3.Min(min, p + e * reach);
                        max = Vector3.Max(max, p + e * reach);
                    }
                }
            }

            min = Vector3.Max(min, boxMin);
            max = Vector3.Min(max, boxMax);
        }

        min -= new Vector3(pad);
        max += new Vector3(pad);
        if (!(min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z))
            return AabbPositions.Inactive;
        return new AabbPositions { MinX = min.X, MinY = min.Y, MinZ = min.Z, MaxX = max.X, MaxY = max.Y, MaxZ = max.Z };
    }

    /// <summary>
    /// Writes the lens polygon of <paramref name="light"/> (light index <paramref name="index"/>): the centre
    /// <see cref="LensOffset"/> in front of the light and <see cref="ShaderConstants.PtDiscSegments"/> rim vertices
    /// (radius × <see cref="PolygonRadiusFactor"/>, starting along the light's right vector, turning towards up) and the
    /// fan of triangles. Lights without a lens or without light get NaN vertices (inactive triangles), so primitive
    /// p always belongs to light p / segments.
    /// </summary>
    public static void WriteLens(in GpuLight light, int index, Span<Vector3> vertices, Span<uint> indices)
    {
        int n = (int)ShaderConstants.PtDiscSegments;
        int v0 = index * LensVertices;
        int i0 = index * LensIndices;
        for (int k = 0; k < n; k++)
        {
            indices[i0 + 3 * k] = (uint)v0;
            indices[i0 + 3 * k + 1] = (uint)(v0 + 1 + k);
            indices[i0 + 3 * k + 2] = (uint)(v0 + 1 + (k + 1) % n);
        }

        float radius = light.SourceRadius;
        if (!Emits(light) || !(radius > 0f) || !float.IsFinite(radius))
        {
            vertices.Slice(v0, LensVertices).Fill(new Vector3(float.NaN));
            return;
        }

        var centre = light.Position + light.Direction * LensOffset;
        float r = radius * PolygonRadiusFactor;
        vertices[v0] = centre;
        for (int k = 0; k < n; k++)
        {
            float phi = 2f * MathF.PI * k / n;
            vertices[v0 + 1 + k] = centre + r * (MathF.Cos(phi) * light.Right + MathF.Sin(phi) * light.Up);
        }
    }

    /// <summary>
    /// The box the haze fills: everything the scene holds — traceable instances (bounding spheres), lights, smoke and
    /// flame boxes and the camera — grown by <see cref="BeamReach"/> on every side and kept within ±<see cref="MaxExtent"/>.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) MediaBox(VulkanRenderWorld world, ReadOnlySpan<GpuLight> lights, Vector3 camera)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        void Include(Vector3 lo, Vector3 hi)
        {
            if (!float.IsFinite(lo.X) || !float.IsFinite(lo.Y) || !float.IsFinite(lo.Z) || !float.IsFinite(hi.X) || !float.IsFinite(hi.Y) ||
                !float.IsFinite(hi.Z))
                return;
            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
        }

        foreach (int index in world.LiveInstances)
        {
            var instance = world.InstanceAt(index);
            if ((instance.Flags & (InstanceFlags.Hidden | InstanceFlags.Reference)) != 0 || instance.Mesh.Destroyed)
                continue;
            var s = instance.WorldSphere;
            var c = new Vector3(s.X, s.Y, s.Z);
            float r = MathF.Max(s.W, 0f);
            Include(c - new Vector3(r), c + new Vector3(r));
        }

        foreach (ref readonly var light in lights)
        {
            if (Emits(light))
                Include(light.Position, light.Position);
        }

        var atmosphere = world.AtmosphereGpu;
        if (atmosphere.Smoke.Present)
            Include(atmosphere.Smoke.BoxMin, atmosphere.Smoke.BoxMax);
        if (atmosphere.Flame.Present)
            Include(atmosphere.Flame.BoxMin, atmosphere.Flame.BoxMax);
        Include(camera, camera);
        if (min.X > max.X)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        var limit = new Vector3(MaxExtent);
        return (Vector3.Clamp(min - new Vector3(BeamReach), -limit, limit), Vector3.Clamp(max + new Vector3(BeamReach), -limit, limit));
    }
}
