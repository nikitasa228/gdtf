using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets;

/// <summary>
/// The surfaces a cable can rest on (floors, the ground): the height in metres (world Z) of the highest supporting
/// surface at the horizontal position (<paramref name="x"/>, <paramref name="y"/>) that is not above
/// <paramref name="maxHeight"/>, or <see cref="float.NegativeInfinity"/> when nothing supports the cable there.
/// <paramref name="maxHeight"/> is the height of the span's straight chord at that point (at a node, the node itself), so
/// a surface above the cable's route (a platform the cable runs under) is ignored while one the cable would sag through
/// is found. Implementations allow a small tolerance so a point lying exactly on a surface is supported by it.
/// A cable never sinks below the surface: a sagging span lies on it instead.
/// </summary>
public delegate float CableSupportHeight(float x, float y, float maxHeight);

/// <summary>Settings of a generated cable tube.</summary>
public sealed record CableTubeOptions
{
    /// <summary>Tube radius in metres (a DMX cable is about 3.5 mm, a 3G2.5 power cable about 5 mm).</summary>
    public float Radius { get; init; } = 0.004f;

    /// <summary>Sides of the cross-section (3 … 64).</summary>
    public int RadialSegments { get; init; } = 8;

    /// <summary>
    /// Cable length divided by the straight distance of every span between two nodes (≥ 1). Above 1 each span hangs
    /// as a catenary under gravity (−Z); 1 gives straight runs. Same meaning as <see cref="CableComponent.SlackFactor"/>.
    /// </summary>
    public float SlackFactor { get; init; } = 1f;

    /// <summary>Longest straight piece used to sample a sagging span, in metres.</summary>
    public float MaxSampleSpacing { get; init; } = 0.1f;

    /// <summary>Close both ends with flat discs.</summary>
    public bool CapEnds { get; init; } = true;

    /// <summary>Texture repeats per metre (U around, V along the cable).</summary>
    public float UvScale { get; init; } = 1f;

    /// <summary>
    /// Slack of each span of a <see cref="CableComponent"/> tree (≥ 1; the arguments are the span's two nodes), or null
    /// for <see cref="SlackFactor"/> everywhere. Lets a span that is fixed along an object — both nodes anchored to the
    /// same truss, wall or floor — run straight while the free spans of the same cable sag. Drawing only: the cut
    /// length of the cable stays routed length × <see cref="CableComponent.SlackFactor"/>.
    /// </summary>
    public Func<CableNode, CableNode, float>? SpanSlack { get; init; }

    /// <summary>
    /// The surfaces the cable rests on, or null for none. Every point of the drawn path — nodes and the samples of a
    /// sagging span — is kept at least <see cref="Radius"/> above the supporting surface under it (the highest one not
    /// above the span's straight chord there), so a cable on a floor lies on it instead of sagging through it.
    /// </summary>
    public CableSupportHeight? Support { get; init; }
}

/// <summary>
/// Tube meshes for cables: a polyline (or a <see cref="CableComponent"/> tree) swept with a circular cross-section using
/// rotation-minimising (double-reflection) frames, so the tube does not twist. Spans can sag as catenaries.
/// Vertex count for one polyline of N path points: N × (segments + 1), plus 2 × (segments + 1) for the end caps.
/// </summary>
public static class CableTubeMesh
{
    /// <summary>Spans steeper than this (horizontal / straight length below 5 %) hang straight down and do not sag.</summary>
    private const float MinHorizontalFraction = 0.05f;

    /// <summary>Builds a tube along the polyline. Fewer than two distinct points yield an empty mesh.</summary>
    public static MeshData Create(IReadOnlyList<Vector3> points, CableTubeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        var arr = new Vector3[points.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = points[i];
        return Create(arr.AsSpan(), options);
    }

    /// <summary>Builds a tube along the polyline. Fewer than two distinct points yield an empty mesh.</summary>
    public static MeshData Create(Vector3[] points, CableTubeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        return Create(points.AsSpan(), options);
    }

    /// <summary>Builds a tube along the polyline. Fewer than two distinct points yield an empty mesh.</summary>
    public static MeshData Create(ReadOnlySpan<Vector3> points, CableTubeOptions? options = null)
    {
        var o = options ?? new CableTubeOptions();
        var path = SamplePath(points, o.SlackFactor, o.MaxSampleSpacing, o.Support, Radius(o.Radius));
        int seg = System.Math.Clamp(o.RadialSegments, 3, 64);
        var b = new MeshBuilder(path.Length * (seg + 1) + 2 * (seg + 1), path.Length * seg * 6 + seg * 6);
        AddSweep(b, path, Radius(o.Radius), seg, o.CapEnds, Uv(o.UvScale));
        return b.ToMesh("Cable");
    }

    /// <summary>
    /// Builds the tubes of a cable tree. The tree is split into chains between branch points and ends, each swept as one
    /// continuous tube. <paramref name="nodePosition"/> resolves node positions (e.g. following anchors); by default the
    /// stored <see cref="CableNode.Position"/> is used. Segments that reference unknown nodes are ignored.
    /// The cable's own <see cref="CableComponent.SlackFactor"/> is used unless <paramref name="options"/> is given; with
    /// <see cref="CableTubeOptions.SpanSlack"/> each span gets its own slack, and <see cref="CableTubeOptions.Support"/>
    /// keeps the tube on the surfaces it lies on.
    /// </summary>
    public static MeshData Create(CableComponent cable, Func<CableNode, Vector3>? nodePosition = null, CableTubeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cable);
        var o = options ?? new CableTubeOptions { SlackFactor = cable.SlackFactor };
        nodePosition ??= n => n.Position;
        int seg = System.Math.Clamp(o.RadialSegments, 3, 64);
        float radius = Radius(o.Radius), uv = Uv(o.UvScale);

        var index = new Dictionary<Guid, int>(cable.Nodes.Count);
        var positions = new List<Vector3>(cable.Nodes.Count);
        var nodes = new List<CableNode>(cable.Nodes.Count);
        foreach (var node in cable.Nodes)
        {
            if (node is null || index.ContainsKey(node.Id)) continue;
            var p = nodePosition(node);
            index[node.Id] = positions.Count;
            positions.Add(MeshTools.IsFinite(p) ? p : Vector3.Zero);
            nodes.Add(node);
        }
        var adjacency = new List<(int To, int Segment)>[positions.Count];
        for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<(int, int)>(2);
        var edges = new List<(int A, int B)>();
        foreach (var s in cable.Segments)
        {
            if (!index.TryGetValue(s.FromNode, out int a) || !index.TryGetValue(s.ToNode, out int c) || a == c) continue;
            adjacency[a].Add((c, edges.Count));
            adjacency[c].Add((a, edges.Count));
            edges.Add((a, c));
        }

        var b = new MeshBuilder(edges.Count * 4 * (seg + 1), edges.Count * 4 * seg * 6);
        var used = new bool[edges.Count];
        var chain = new List<Vector3>();
        var chainSlack = new List<float>();
        // Chains start at ends and branch points (degree ≠ 2); what is left afterwards are closed loops.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int start = 0; start < positions.Count; start++)
            {
                if (pass == 0 && adjacency[start].Count == 2) continue;
                foreach (var (first, firstEdge) in adjacency[start])
                {
                    if (used[firstEdge]) continue;
                    chain.Clear();
                    chainSlack.Clear();
                    chain.Add(positions[start]);
                    int previous = start, current = first, edge = firstEdge;
                    while (true)
                    {
                        used[edge] = true;
                        chain.Add(positions[current]);
                        chainSlack.Add(o.SpanSlack is { } spanSlack ? spanSlack(nodes[previous], nodes[current]) : o.SlackFactor);
                        if (adjacency[current].Count != 2) break;
                        var next = adjacency[current][0].Segment == edge ? adjacency[current][1] : adjacency[current][0];
                        if (used[next.Segment]) break;
                        edge = next.Segment;
                        previous = current;
                        current = next.To;
                    }
                    var path = SamplePath(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chain),
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chainSlack), o.MaxSampleSpacing, o.Support, radius);
                    AddSweep(b, path, radius, seg, o.CapEnds, uv);
                }
            }
        }
        return b.ToMesh("Cable");
    }

    /// <summary>
    /// Points along the polyline with every span replaced by a sampled catenary when <paramref name="slackFactor"/> &gt; 1.
    /// Consecutive duplicate points are removed. The sag of an inclined span is that of a level span of the same straight
    /// length, applied vertically and scaled by the span's horizontal fraction (an approximation that is exact for level
    /// spans and vanishes for vertical drops).
    /// </summary>
    public static Vector3[] SamplePath(ReadOnlySpan<Vector3> points, float slackFactor, float maxSampleSpacing) =>
        SamplePath(points, slackFactor, maxSampleSpacing, null, 0f);

    /// <summary>
    /// Like <see cref="SamplePath(ReadOnlySpan{Vector3}, float, float)"/>, with the path kept at least
    /// <paramref name="clearance"/> (the tube radius) above the surfaces of <paramref name="support"/> (null: none).
    /// </summary>
    public static Vector3[] SamplePath(ReadOnlySpan<Vector3> points, float slackFactor, float maxSampleSpacing, CableSupportHeight? support, float clearance)
    {
        Span<float> one = stackalloc float[1];
        one[0] = slackFactor;
        return SamplePathCore(points, one, uniform: true, maxSampleSpacing, support, clearance);
    }

    /// <summary>
    /// Points along the polyline where span i (from point i to point i + 1) hangs with <paramref name="spanSlack"/>[i]
    /// (≥ 1; a missing, non-finite or smaller value draws the span straight), kept at least <paramref name="clearance"/>
    /// above the surfaces of <paramref name="support"/> (null: none): a sagging span that would dip below the surface under
    /// it lies on that surface instead, and a node lying on a surface is lifted by the clearance so the tube rests on it.
    /// Only the drawing changes; a cable's cut length is measured by its geometry, not by this path.
    /// </summary>
    public static Vector3[] SamplePath(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> spanSlack, float maxSampleSpacing,
        CableSupportHeight? support = null, float clearance = 0f) =>
        SamplePathCore(points, spanSlack, uniform: false, maxSampleSpacing, support, clearance);

    private static Vector3[] SamplePathCore(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> spanSlack, bool uniform, float maxSampleSpacing,
        CableSupportHeight? support, float clearance)
    {
        var clean = new List<Vector3>(points.Length);
        var slacks = new List<float>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (!MeshTools.IsFinite(p)) continue;
            if (clean.Count > 0 && Vector3.DistanceSquared(clean[^1], p) < 1e-12f) continue;
            // the span that reaches this point (the last one when points were skipped)
            if (clean.Count > 0) slacks.Add(SlackOf(spanSlack, uniform ? 0 : i - 1));
            clean.Add(p);
        }
        float lift = float.IsFinite(clearance) && clearance > 0 ? clearance : 0f;
        if (clean.Count < 2)
        {
            if (support is not null)
                for (int i = 0; i < clean.Count; i++) clean[i] = Rest(clean[i], clean[i].Z, support, lift);
            return clean.ToArray();
        }
        float spacing = float.IsFinite(maxSampleSpacing) && maxSampleSpacing > 0.001f ? maxSampleSpacing : 0.1f;

        var result = new List<Vector3>(clean.Count * 8) { Rest(clean[0], clean[0].Z, support, lift) };
        var forward = support is null ? null : new List<float>();
        for (int i = 0; i + 1 < clean.Count; i++)
        {
            Vector3 a = clean[i], c = clean[i + 1];
            float slack = slacks[i];
            var d = c - a;
            float straight = d.Length();
            float horizontal = new Vector2(d.X, d.Y).Length();
            float fraction = straight > 0 ? horizontal / straight : 0;
            if (slack <= 1.0001f || fraction < MinHorizontalFraction)
            {
                var straightEnd = Rest(c, c.Z, support, lift);
                if (forward is not null && support is not null && horizontal > spacing)
                {
                    // a straight span can still cut through a surface on its way (a taut cable leaving a riser): it is
                    // sampled between its resting ends and draped over what it would pass through; a span that cuts
                    // through nothing stays one straight piece
                    var from = result[^1];
                    int pieces = System.Math.Clamp((int)MathF.Ceiling(straight / spacing), 2, 512);
                    int start = result.Count;
                    for (int s = 1; s < pieces; s++) result.Add(Vector3.Lerp(from, straightEnd, s / (float)pieces));
                    if (!RestSpan(result, start, from, straightEnd, straightEnd.Z, support, lift, forward))
                        result.RemoveRange(start, result.Count - start);
                }
                result.Add(straightEnd);
                continue;
            }
            float k = CatenaryParameter(slack);
            float sag = CatenarySag(straight, slack) * fraction;
            int samples = System.Math.Clamp((int)MathF.Ceiling(straight / spacing), 2, 512);
            float coshK = MathF.Cosh(k);
            int first = result.Count;
            for (int s = 1; s < samples; s++)
            {
                float t = s / (float)samples;
                float shape = (coshK - MathF.Cosh(k * (2 * t - 1))) / (coshK - 1);
                result.Add(Vector3.Lerp(a, c, t) - Vector3.UnitZ * (sag * shape));
            }
            var end = Rest(c, c.Z, support, lift);
            if (forward is not null && support is not null) RestSpan(result, first, a, c, end.Z, support, lift, forward);
            result.Add(end);
        }
        // a cable lying on a surface makes runs of equal points where several samples rest at one spot: keep one
        var path = new List<Vector3>(result.Count) { result[0] };
        for (int i = 1; i < result.Count; i++)
            if (Vector3.DistanceSquared(path[^1], result[i]) >= 1e-12f) path.Add(result[i]);
        return path.ToArray();
    }

    /// <summary>
    /// Keeps the inner samples <paramref name="path"/>[<paramref name="first"/>..] of the span from <paramref name="a"/>
    /// to <paramref name="c"/> on the surfaces they would sag through. A surface supports a sample when it is not above the
    /// span's straight chord there or the cable's height at the previous sample: the cable cannot pass down through a
    /// surface it is lying on or above (so a cable leaving a riser drapes over its edge), but a surface over the cable's
    /// route (a platform it runs under) is left alone. The walk is done from both ends (the start rests on
    /// <c>path[first − 1]</c>, the end at <paramref name="endZ"/>) and each sample keeps the higher result, so the drawing
    /// does not depend on the direction the span was stored in. Returns true when a sample was raised.
    /// </summary>
    private static bool RestSpan(List<Vector3> path, int first, Vector3 a, Vector3 c, float endZ, CableSupportHeight support, float lift,
        List<float> forward)
    {
        int count = path.Count - first;
        if (count <= 0) return false;
        float span = 1f / (count + 1);
        forward.Clear();
        float previous = path[first - 1].Z;
        for (int s = 0; s < count; s++)
        {
            var p = path[first + s];
            float chord = a.Z + (c.Z - a.Z) * ((s + 1) * span);
            previous = Rest(p, MathF.Max(chord, previous), support, lift).Z;
            forward.Add(previous);
        }
        float next = endZ;
        bool raised = false;
        for (int s = count - 1; s >= 0; s--)
        {
            var p = path[first + s];
            float chord = a.Z + (c.Z - a.Z) * ((s + 1) * span);
            next = Rest(p, MathF.Max(chord, next), support, lift).Z;
            float z = MathF.Max(forward[s], next);
            raised |= z > p.Z + 1e-6f;
            path[first + s] = new Vector3(p.X, p.Y, z);
        }
        return raised;
    }

    /// <summary>Slack of span <paramref name="i"/>: at least 1, at most 10; missing or invalid values are 1 (straight).</summary>
    private static float SlackOf(ReadOnlySpan<float> spanSlack, int i) =>
        (uint)i < (uint)spanSlack.Length && float.IsFinite(spanSlack[i]) ? System.Math.Clamp(spanSlack[i], 1f, 10f) : 1f;

    /// <summary>
    /// <paramref name="p"/> raised, when needed, to <paramref name="clearance"/> above the surface that supports it (the
    /// highest one not above <paramref name="maxHeight"/>, the chord of its span there), measured along the surface's
    /// normal: on a ramp the tube's centre must be <paramref name="clearance"/> / cos(slope) above the surface, or its
    /// lower side would cut into it.
    /// </summary>
    private static Vector3 Rest(Vector3 p, float maxHeight, CableSupportHeight? support, float clearance)
    {
        if (support is null) return p;
        float surface = support(p.X, p.Y, maxHeight);
        if (!float.IsFinite(surface)) return p;
        float min = surface + clearance;
        // only a point this close to the surface can need more than the level clearance (slopes up to 60°: twice it)
        if (clearance > 0 && p.Z < surface + MaxSecant * clearance) min = surface + clearance * Secant(support, p.X, p.Y, surface, clearance);
        return p.Z < min ? new Vector3(p.X, p.Y, min) : p;
    }

    /// <summary>tan²(60°): a steeper change of the supporting height is an edge between two surfaces, not a slope.</summary>
    private const float MaxSlopeSquared = 3.0003f;

    /// <summary>1 / cos(60°): the largest factor <see cref="Secant"/> returns.</summary>
    private const float MaxSecant = 2f;

    /// <summary>
    /// 1 / cos of the slope of the surface at (<paramref name="x"/>, <paramref name="y"/>) — the height of the surface is
    /// <paramref name="surface"/> there — from its heights a small step away in X and Y (the larger slope of the two
    /// sides of each axis, so a ridge is not averaged away; a side that is off the surface or across an edge is left
    /// out). 1 on level ground.
    /// </summary>
    private static float Secant(CableSupportHeight support, float x, float y, float surface, float clearance)
    {
        float h = MathF.Max(clearance, 1e-3f);
        float limit = surface + MaxSecant * h; // a slope up to 60° rises at most √3 × h over h
        float gx = Slope(support(x + h, y, limit), support(x - h, y, limit), surface, h);
        float gy = Slope(support(x, y + h, limit), support(x, y - h, limit), surface, h);
        float g2 = gx * gx + gy * gy;
        return g2 <= MaxSlopeSquared ? MathF.Sqrt(1 + g2) : MaxSecant;
    }

    /// <summary>The steeper of the slopes towards <paramref name="ahead"/> and from <paramref name="behind"/>, edges left out.</summary>
    private static float Slope(float ahead, float behind, float surface, float h)
    {
        float a = (ahead - surface) / h, b = (surface - behind) / h;
        bool useA = float.IsFinite(a) && a * a <= MaxSlopeSquared, useB = float.IsFinite(b) && b * b <= MaxSlopeSquared;
        if (useA && useB) return MathF.Abs(a) >= MathF.Abs(b) ? a : b;
        return useA ? a : useB ? b : 0f;
    }

    /// <summary>
    /// Mid-span sag in metres of a level catenary whose length is <paramref name="slackFactor"/> × <paramref name="span"/>
    /// (from S = 2a·sinh(L/2a) and d = a·(cosh(L/2a) − 1)). 5 % slack gives a sag of about 14 % of the span.
    /// </summary>
    public static float CatenarySag(float span, float slackFactor)
    {
        if (!(span > 0) || !float.IsFinite(span) || !(slackFactor > 1)) return 0f;
        float k = CatenaryParameter(slackFactor);
        return span * (MathF.Cosh(k) - 1) / (2 * k);
    }

    /// <summary>Solves sinh(k)/k = slack for k = L / 2a (Newton iteration).</summary>
    private static float CatenaryParameter(float slack)
    {
        double s = System.Math.Clamp((double)slack, 1.000001, 10.0);
        double k = System.Math.Sqrt(6 * (s - 1));
        for (int i = 0; i < 50; i++)
        {
            double sh = System.Math.Sinh(k), ch = System.Math.Cosh(k);
            double f = sh / k - s;
            double df = (k * ch - sh) / (k * k);
            if (df <= 0) break;
            double next = System.Math.Clamp(k - f / df, 1e-6, 20.0);
            if (System.Math.Abs(next - k) < 1e-10)
            {
                k = next;
                break;
            }
            k = next;
        }
        return (float)k;
    }

    private static void AddSweep(MeshBuilder b, Vector3[] path, float radius, int seg, bool caps, float uvScale)
    {
        int n = path.Length;
        if (n < 2) return;
        var tangents = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var d = path[System.Math.Min(i + 1, n - 1)] - path[System.Math.Max(i - 1, 0)];
            tangents[i] = d.LengthSquared() > 1e-20f ? Vector3.Normalize(d) : (i > 0 ? tangents[i - 1] : Vector3.UnitX);
        }
        // Initial normal: horizontal and perpendicular to the cable where possible.
        var r = Vector3.Cross(tangents[0], Vector3.UnitZ);
        r = r.LengthSquared() > 1e-8f ? Vector3.Normalize(r) : MeshTools.AnyPerpendicular(tangents[0]);

        float circumference = 2 * MathF.PI * radius;
        float along = 0;
        int firstRing = b.VertexCount;
        var startNormal = r;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                // Double reflection (Wang et al. 2008): rotation-minimising frame transport.
                var v1 = path[i] - path[i - 1];
                float c1 = Vector3.Dot(v1, v1);
                along += MathF.Sqrt(c1);
                if (c1 > 1e-20f)
                {
                    var rL = r - (2 / c1) * Vector3.Dot(v1, r) * v1;
                    var tL = tangents[i - 1] - (2 / c1) * Vector3.Dot(v1, tangents[i - 1]) * v1;
                    var v2 = tangents[i] - tL;
                    float c2 = Vector3.Dot(v2, v2);
                    r = c2 > 1e-20f ? rL - (2 / c2) * Vector3.Dot(v2, rL) * v2 : rL;
                }
                r -= tangents[i] * Vector3.Dot(tangents[i], r);
                r = r.LengthSquared() > 1e-20f ? Vector3.Normalize(r) : MeshTools.AnyPerpendicular(tangents[i]);
            }
            var bi = Vector3.Cross(tangents[i], r);
            for (int s = 0; s <= seg; s++)
            {
                float t = s / (float)seg;
                float ang = t * 2 * MathF.PI;
                var radial = r * MathF.Cos(ang) + bi * MathF.Sin(ang);
                b.AddVertex(path[i] + radial * radius, radial, new Vector2(t * circumference * uvScale, along * uvScale));
            }
        }
        int stride = seg + 1;
        for (int i = 0; i + 1 < n; i++)
        {
            for (int s = 0; s < seg; s++)
            {
                int a0 = firstRing + i * stride + s, a1 = a0 + 1, b0 = a0 + stride, b1 = b0 + 1;
                b.AddQuad(a0, a1, b1, b0);
            }
        }
        if (caps)
        {
            b.AddDisc(path[0], -tangents[0], startNormal, radius, seg, uvScale);
            b.AddDisc(path[n - 1], tangents[n - 1], r, radius, seg, uvScale);
        }
    }

    private static float Radius(float r) => float.IsFinite(r) && r > 0 ? System.Math.Min(r, 1f) : 0.004f;

    private static float Uv(float s) => float.IsFinite(s) && s > 0 ? s : 1f;
}
