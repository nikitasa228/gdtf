using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets;

/// <summary>
/// Mesh of a <see cref="WallComponent"/>: a thin box (24 vertices, 12 triangles) in the entity's local space, Z up,
/// metres. Placement relative to the entity origin (the conventions documented on <see cref="WallKind"/>, completed
/// where they leave a choice):
/// <list type="bullet">
/// <item><see cref="WallKind.Wall"/>: width along X centred on the origin (−W/2 … W/2), height up Z from the origin
/// (0 … H), the visible front face (normal −Y, towards the audience) in the plane y = 0 and the thickness behind it
/// (0 … T along +Y).</item>
/// <item><see cref="WallKind.Floor"/>: width along X and length (<c>Height</c>) along Y, both centred; top face at
/// z = 0, thickness downwards (−T … 0).</item>
/// <item><see cref="WallKind.Ceiling"/>: as a floor, but the bottom face at z = 0 and thickness upwards (0 … T).</item>
/// </list>
/// UVs are in metres × <see cref="WallComponent.UvScale"/>, starting at 0 on each face's top-left corner (vertical faces:
/// "up" is +Z; horizontal faces: "up" is +Y). A zero thickness yields only the main face.
/// </summary>
public static class WallMesh
{
    /// <summary>Smallest dimension used for degenerate or invalid input (1 mm).</summary>
    public const float MinimumSize = 0.001f;

    /// <summary>Largest dimension accepted (10 km); larger or non-finite values are clamped.</summary>
    public const float MaximumSize = 10_000f;

    /// <summary>Builds the mesh for a wall component (one sub-mesh, material slot 0, tangents included).</summary>
    public static MeshData Create(WallComponent wall)
    {
        ArgumentNullException.ThrowIfNull(wall);
        return Create(wall.Kind, wall.Width, wall.Height, wall.Thickness, wall.UvScale);
    }

    /// <summary>
    /// Builds a wall/floor/ceiling mesh. <paramref name="height"/> is the extent along Z for walls and along Y for floors
    /// and ceilings; <paramref name="uvScale"/> is texture repeats per metre.
    /// </summary>
    public static MeshData Create(WallKind kind, float width, float height, float thickness, float uvScale = 1f)
    {
        float w = Clamp(width), h = Clamp(height);
        float t = float.IsFinite(thickness) && thickness > 0 ? System.Math.Min(thickness, MaximumSize) : 0f;
        float s = float.IsFinite(uvScale) && uvScale > 0 ? uvScale : 1f;
        var b = new MeshBuilder(24, 36);
        Vector3 min, max;
        switch (kind)
        {
            case WallKind.Floor:
                min = new Vector3(-w / 2, -h / 2, -t);
                max = new Vector3(w / 2, h / 2, 0);
                if (t > 0) b.AddBox(min, max, s);
                else b.AddRectangle(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, w, h, s);
                break;
            case WallKind.Ceiling:
                min = new Vector3(-w / 2, -h / 2, 0);
                max = new Vector3(w / 2, h / 2, t);
                if (t > 0) b.AddBox(min, max, s);
                else b.AddRectangle(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, w, h, s);
                break;
            default:
                min = new Vector3(-w / 2, 0, 0);
                max = new Vector3(w / 2, t, h);
                if (t > 0) b.AddBox(min, max, s);
                else b.AddRectangle(new Vector3(0, 0, h / 2), -Vector3.UnitY, Vector3.UnitZ, w, h, s);
                break;
        }
        return b.ToMesh(kind switch { WallKind.Floor => "Floor", WallKind.Ceiling => "Ceiling", _ => "Wall" });
    }

    private static float Clamp(float v) => float.IsFinite(v) ? System.Math.Clamp(v, MinimumSize, MaximumSize) : MinimumSize;
}
