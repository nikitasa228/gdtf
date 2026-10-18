using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets;

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
        var path = SamplePath(points, o.SlackFactor, o.MaxSampleSpacing);
        int seg = System.Math.Clamp(o.RadialSegments, 3, 64);
        var b = new MeshBuilder(path.Length * (seg + 1) + 2 * (seg + 1), path.Length * seg * 6 + seg * 6);
        AddSweep(b, path, Radius(o.Radius), seg, o.CapEnds, Uv(o.UvScale));
        return b.ToMesh("Cable");
    }

    /// <summary>
    /// Builds the tubes of a cable tree. The tree is split into chains between branch points and ends, each swept as one
    /// continuous tube. <paramref name="nodePosition"/> resolves node positions (e.g. following anchors); by default the
    /// stored <see cref="CableNode.Position"/> is used. Segments that reference unknown nodes are ignored.
    /// The cable's own <see cref="CableComponent.SlackFactor"/> is used unless <paramref name="options"/> is given.
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
        foreach (var node in cable.Nodes)
        {
            if (index.ContainsKey(node.Id)) continue;
            var p = nodePosition(node);
            index[node.Id] = positions.Count;
            positions.Add(MeshTools.IsFinite(p) ? p : Vector3.Zero);
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
                    chain.Add(positions[start]);
                    int current = first, edge = firstEdge;
                    while (true)
                    {
                        used[edge] = true;
                        chain.Add(positions[current]);
                        if (adjacency[current].Count != 2) break;
                        var next = adjacency[current][0].Segment == edge ? adjacency[current][1] : adjacency[current][0];
                        if (used[next.Segment]) break;
                        edge = next.Segment;
                        current = next.To;
                    }
                    var path = SamplePath(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chain), o.SlackFactor, o.MaxSampleSpacing);
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
    public static Vector3[] SamplePath(ReadOnlySpan<Vector3> points, float slackFactor, float maxSampleSpacing)
    {
        var clean = new List<Vector3>(points.Length);
        foreach (var p in points)
        {
            if (!MeshTools.IsFinite(p)) continue;
            if (clean.Count > 0 && Vector3.DistanceSquared(clean[^1], p) < 1e-12f) continue;
            clean.Add(p);
        }
        if (clean.Count < 2) return clean.ToArray();
        float slack = float.IsFinite(slackFactor) ? System.Math.Clamp(slackFactor, 1f, 10f) : 1f;
        float spacing = float.IsFinite(maxSampleSpacing) && maxSampleSpacing > 0.001f ? maxSampleSpacing : 0.1f;
        if (slack <= 1.0001f) return clean.ToArray();

        var result = new List<Vector3>(clean.Count * 8) { clean[0] };
        for (int i = 0; i + 1 < clean.Count; i++)
        {
            Vector3 a = clean[i], c = clean[i + 1];
            var d = c - a;
            float straight = d.Length();
            float horizontal = new Vector2(d.X, d.Y).Length();
            float fraction = straight > 0 ? horizontal / straight : 0;
            if (fraction < MinHorizontalFraction)
            {
                result.Add(c);
                continue;
            }
            float k = CatenaryParameter(slack);
            float sag = CatenarySag(straight, slack) * fraction;
            int samples = System.Math.Clamp((int)MathF.Ceiling(straight / spacing), 2, 512);
            float coshK = MathF.Cosh(k);
            for (int s = 1; s <= samples; s++)
            {
                float t = s / (float)samples;
                float shape = (coshK - MathF.Cosh(k * (2 * t - 1))) / (coshK - 1);
                result.Add(Vector3.Lerp(a, c, t) - Vector3.UnitZ * (sag * shape));
            }
        }
        return result.ToArray();
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
