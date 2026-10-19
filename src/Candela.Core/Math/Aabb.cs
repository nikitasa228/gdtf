using System.Numerics;

namespace Candela.Core.Math;

/// <summary>Axis-aligned bounding box in metres. An empty box has Min &gt; Max.</summary>
public struct Aabb
{
    /// <summary>Lower corner, metres.</summary>
    public Vector3 Min;

    /// <summary>Upper corner, metres.</summary>
    public Vector3 Max;

    /// <summary>Creates a box from its corners.</summary>
    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The empty box (Min = +∞, Max = −∞); including a point makes it that point.</summary>
    public static Aabb Empty => new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    /// <summary>True when the box contains no point.</summary>
    public readonly bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    /// <summary>Centre of the box (meaningless for an empty box).</summary>
    public readonly Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>Extent along each axis, metres (zero for an empty box).</summary>
    public readonly Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    /// <summary>Grows the box to contain <paramref name="p"/>.</summary>
    public void Include(Vector3 p)
    {
        Min = Vector3.Min(Min, p);
        Max = Vector3.Max(Max, p);
    }

    /// <summary>Grows the box to contain <paramref name="b"/> (an empty box changes nothing).</summary>
    public void Include(Aabb b)
    {
        if (b.IsEmpty) return;
        Include(b.Min);
        Include(b.Max);
    }

    /// <summary>True when <paramref name="p"/> lies inside or on the box.</summary>
    public readonly bool Contains(Vector3 p) =>
        p.X >= Min.X && p.Y >= Min.Y && p.Z >= Min.Z && p.X <= Max.X && p.Y <= Max.Y && p.Z <= Max.Z;

    /// <summary>Box of the eight transformed corners.</summary>
    public readonly Aabb Transform(Matrix4x4 m)
    {
        if (IsEmpty) return this;
        var r = Empty;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? Min.X : Max.X, (i & 2) == 0 ? Min.Y : Max.Y, (i & 4) == 0 ? Min.Z : Max.Z);
            r.Include(Vector3.Transform(c, m));
        }
        return r;
    }

    /// <summary>Slab test; returns the entry distance or null when the ray misses.</summary>
    public readonly float? Intersect(Ray ray)
    {
        float tmin = 0f, tmax = float.PositiveInfinity;
        for (int a = 0; a < 3; a++)
        {
            float o = ray.Origin[a], d = ray.Direction[a], lo = Min[a], hi = Max[a];
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < lo || o > hi) return null;
                continue;
            }
            float t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return tmin;
    }
}

/// <summary>Ray with a normalised direction.</summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    /// <summary>The point at distance <paramref name="t"/> along the ray, metres.</summary>
    public Vector3 At(float t) => Origin + Direction * t;
}
