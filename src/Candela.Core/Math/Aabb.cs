using System.Numerics;

namespace Candela.Core.Math;

/// <summary>Axis-aligned bounding box in metres. An empty box has Min &gt; Max.</summary>
public struct Aabb
{
    public Vector3 Min;
    public Vector3 Max;

    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public static Aabb Empty => new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    public readonly bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;
    public readonly Vector3 Center => (Min + Max) * 0.5f;
    public readonly Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public void Include(Vector3 p)
    {
        Min = Vector3.Min(Min, p);
        Max = Vector3.Max(Max, p);
    }

    public void Include(Aabb b)
    {
        if (b.IsEmpty) return;
        Include(b.Min);
        Include(b.Max);
    }

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
    public Vector3 At(float t) => Origin + Direction * t;
}
