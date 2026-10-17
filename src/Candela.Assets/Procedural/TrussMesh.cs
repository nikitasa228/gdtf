using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets;

/// <summary>
/// Procedural truss geometry for a <see cref="TrussComponent"/> without its own model: chords (Ø from the profile)
/// running along local X from 0 to <c>Length</c>, zig-zag diagonals on every face at roughly 45°, and a perpendicular
/// brace at both ends of each face. The cross-section is centred on the X axis, so the mesh bounds are exactly
/// <c>[0, Length] × [−Size/2, Size/2]</c> across (Y) and, for square sections, the same vertically (Z):
/// <list type="bullet">
/// <item>Square (F34…): chords at (±c, ±c) with c = Size/2 − chord radius.</item>
/// <item>Triangle (F33…): two chords on top, one at the bottom ("point down", as usually flown with fixtures on the
/// bottom chord); width Size, height (Size − d)·√3/2 + d for chord diameter d.</item>
/// <item>Ladder (F32…): two chords one above the other (z = ±c) braced in the XZ plane; width = chord diameter.</item>
/// <item>Pipe: one tube of the profile diameter.</item>
/// <item>Tower: a square/triangular truss with a 12 mm base plate at x = 0. Like every truss its length runs along
/// local X; the entity's rotation stands it up.</item>
/// </list>
/// UVs are in metres (U around each tube, V along it). One sub-mesh (material slot 0, usually the aluminium truss
/// material).
/// </summary>
public static class TrussMesh
{
    /// <summary>Thickness of a tower base plate in metres.</summary>
    public const float BasePlateThickness = 0.012f;

    /// <summary>Builds the mesh for a truss component from its kind, length and profile.</summary>
    public static MeshData Create(TrussComponent truss, int chordSegments = 12)
    {
        ArgumentNullException.ThrowIfNull(truss);
        return Create(truss.Kind, truss.Length, TrussProfile.Parse(truss.Profile, truss.Kind), chordSegments);
    }

    /// <summary>
    /// Builds a truss of <paramref name="length"/> metres along +X. <paramref name="chordSegments"/> is rounded up to a
    /// multiple of 4 (8 … 64) so ring vertices fall on the bounding box.
    /// </summary>
    public static MeshData Create(TrussKind kind, float length, TrussProfile profile, int chordSegments = 12)
    {
        ArgumentNullException.ThrowIfNull(profile);
        float len = float.IsFinite(length) ? System.Math.Clamp(length, 0.01f, WallMesh.MaximumSize) : 0.01f;
        int seg = System.Math.Clamp((chordSegments + 3) / 4 * 4, 8, 64);
        int braceSeg = System.Math.Max(6, seg / 2);
        float size = System.Math.Clamp(profile.Size, 0.005f, 5f);
        float rc = System.Math.Clamp(profile.ChordDiameter / 2, 0.001f, size / 2);
        float rb = System.Math.Clamp(profile.BraceDiameter / 2, 0.0005f, rc * 0.9f);
        var chords = ChordPositions(profile.Shape, size, rc);

        int faces = profile.Shape switch { TrussShape.Square => 4, TrussShape.Triangle => 3, TrussShape.Ladder => 1, _ => 0 };
        var b = new MeshBuilder(chords.Length * (4 * seg + 8) + faces * 64 * braceSeg, chords.Length * seg * 12 + faces * 64 * braceSeg * 6);
        float r = profile.Shape == TrussShape.Pipe ? size / 2 : rc;
        foreach (var c in chords)
            b.AddTube(new Vector3(0, c.X, c.Y), new Vector3(len, c.X, c.Y), r, seg, true, true, 1f, Vector3.UnitY);

        for (int f = 0; f < faces; f++)
        {
            var a = chords[f];
            var c = chords[(f + 1) % chords.Length];
            AddFaceBracing(b, a, c, len, rb, braceSeg, swap: (f & 1) == 1);
        }

        if (kind == TrussKind.Tower)
        {
            float plate = size + 0.3f;
            b.AddBox(new Vector3(0, -plate / 2, -plate / 2), new Vector3(BasePlateThickness, plate / 2, plate / 2), 1f);
        }
        return b.ToMesh($"{kind} {profile.Name}");
    }

    /// <summary>Chord centre positions in the YZ plane (X = Y, Y = Z of the returned vectors).</summary>
    public static Vector2[] ChordPositions(TrussShape shape, float size, float chordRadius)
    {
        float c = size / 2 - chordRadius;
        switch (shape)
        {
            case TrussShape.Pipe:
                return new[] { Vector2.Zero };
            case TrussShape.Ladder:
                return new[] { new Vector2(0, c), new Vector2(0, -c) };
            case TrussShape.Triangle:
                {
                    float a = size - 2 * chordRadius;
                    float h = a * MathF.Sqrt(3f) / 2 + 2 * chordRadius;
                    float top = h / 2 - chordRadius, bottom = -h / 2 + chordRadius;
                    return new[] { new Vector2(a / 2, top), new Vector2(-a / 2, top), new Vector2(0, bottom) };
                }
            default:
                return new[] { new Vector2(c, c), new Vector2(-c, c), new Vector2(-c, -c), new Vector2(c, -c) };
        }
    }

    /// <summary>Overall height (Z extent) of a section: equals Size except for triangles.</summary>
    public static float SectionHeight(TrussProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Shape switch
        {
            TrussShape.Triangle => (profile.Size - profile.ChordDiameter) * MathF.Sqrt(3f) / 2 + profile.ChordDiameter,
            _ => profile.Size,
        };
    }

    /// <summary>Overall width (Y extent) of a section: equals Size except for ladders (one chord wide).</summary>
    public static float SectionWidth(TrussProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Shape == TrussShape.Ladder ? profile.ChordDiameter : profile.Size;
    }

    private static void AddFaceBracing(MeshBuilder b, Vector2 a, Vector2 c, float len, float rb, int seg, bool swap)
    {
        var pa = new Vector3(0, a.X, a.Y);
        var pc = new Vector3(0, c.X, c.Y);
        if (swap) (pa, pc) = (pc, pa);
        float spacing = System.Math.Max(Vector3.Distance(pa, pc), 0.05f);
        float x0 = rb, x1 = len - rb;
        if (x1 <= x0)
        {
            var mid = new Vector3(len / 2, 0, 0);
            b.AddTube(pa + mid, pc + mid, rb, seg, false, false, 1f, Vector3.UnitX);
            return;
        }
        int n = System.Math.Max(1, (int)MathF.Round((x1 - x0) / spacing));
        float step = (x1 - x0) / n;
        var ex = Vector3.UnitX;
        b.AddTube(pa + ex * x0, pc + ex * x0, rb, seg, false, false, 1f, Vector3.UnitX);
        for (int k = 0; k < n; k++)
        {
            float xa = x0 + k * step, xb = x0 + (k + 1) * step;
            var from = (k & 1) == 0 ? pa : pc;
            var to = (k & 1) == 0 ? pc : pa;
            b.AddTube(from + ex * xa, to + ex * xb, rb, seg, false, false, 1f, Vector3.UnitX);
        }
        b.AddTube(pa + ex * x1, pc + ex * x1, rb, seg, false, false, 1f, Vector3.UnitX);
    }
}
