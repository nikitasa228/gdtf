using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// Per-view light decisions, made on the CPU every <see cref="IRenderer.Render"/> without allocation:
/// <list type="bullet">
/// <item>an estimate of each light's contribution to the view — luminous flux (peak cd × effective solid angle ×
/// facets) × (¼ + on-screen size of the beam's bounding sphere, capped at 1) / (distance to that sphere + 1 m)², zero
/// when the sphere (beam cone up to the light's range, at most 60 m) is outside the view frustum;</item>
/// <item>shadow tiles for the <see cref="PreviewSettings.MaxShadowedLights"/> highest-scoring lights that cast shadows,
/// the tile size halving for every factor of 4 the score is below the best light's (at least 1/8 of the full
/// resolution), then reduced — the least important of the largest tiles first — until all of them fit the view's
/// share of the atlas;</item>
/// <item>the volumetric beams: the <see cref="PreviewSettings.MaxRaymarchedBeams"/> highest-scoring spot lights whose
/// bounding cone is at most <see cref="MaxBeamBoundingAngle"/> wide get per-pixel single scattering and are skipped by
/// the froxel injection (diffuse and very wide emitters, and the lights beyond the limit, stay in the froxels), with
/// the load estimate that scales the per-pixel sample counts (<see cref="BeamQuality"/>);</item>
/// <item>the lights whose reflective shadow maps feed indirect light — the <see cref="MaxRsmLights"/> highest-scoring
/// shadowed lights, each with <see cref="MinVplsPerLight"/> plus a share of the rest of the <see cref="VplBudget"/>
/// virtual point lights proportional to its score —, or the lights the ray-traced GI evaluates at its hits;</item>
/// <item>the beam outline segments (<see cref="ViewOptions.ShowBeamOutlines"/>).</item>
/// </list>
/// </summary>
internal sealed class LightPlanner
{
    /// <summary>Widest bounding half-angle (cut-off, iris and prism spread) of a volumetric beam, rad (80°).</summary>
    public const float MaxBeamBoundingAngle = 80f * MathF.PI / 180f;

    /// <summary>
    /// Beam crossings per pixel (sum over the beams of their share of the screen, prism facets counted) that the
    /// per-pixel integration affords at full quality; above it the segments and cookie samples per crossing shrink
    /// in proportion (down to <see cref="MinBeamQuality"/>).
    /// </summary>
    public const float BeamBudget = 48f;

    /// <summary>Lowest quality factor of the beam integration (a quarter of the segments and samples).</summary>
    public const float MinBeamQuality = 0.25f;

    /// <summary>Virtual point lights per view.</summary>
    public const int VplBudget = 256;

    /// <summary>Fewest VPLs of a light with a reflective shadow map.</summary>
    public const int MinVplsPerLight = 8;

    /// <summary>Most lights whose reflective shadow maps feed the VPLs of a view (each gets at least <see cref="MinVplsPerLight"/>).</summary>
    public const int MaxRsmLights = VplBudget / MinVplsPerLight;

    /// <summary>Most lights evaluated at the hits of the ray-traced GI.</summary>
    public const int MaxGiLights = 64;

    private const int OutlineGenerators = 8;
    private const int OutlineRing = 24;

    private readonly int[] _candidates = new int[ShadowAtlas.MaxTiles];
    private readonly int[] _levels = new int[ShadowAtlas.MaxTiles];
    private float[] _sortKeys = new float[64];
    private int[] _order = new int[64];
    private float[] _scores = new float[64];

    /// <summary>Score of each light in the last plan (index = light).</summary>
    public ReadOnlySpan<float> Scores => _scores;

    /// <summary>Lights in descending score order (last plan).</summary>
    public ReadOnlySpan<int> Order => _order;

    /// <summary>Volumetric beams in the last plan (first entries of the list buffer).</summary>
    public int BeamCount { get; private set; }

    /// <summary>Estimated beam crossings per pixel of the last plan (share of the screen per beam, facets counted).</summary>
    public float BeamCoverage { get; private set; }

    /// <summary>Quality factor of the beam integration in the last plan (1 = full, down to <see cref="MinBeamQuality"/>).</summary>
    public float BeamQuality { get; private set; } = 1f;

    /// <summary>Lights in view that stay in the froxel volume (not beams) in the last plan.</summary>
    public int FroxelLights { get; private set; }

    /// <summary>Lights after the beams in the list buffer (RSM lights or GI lights).</summary>
    public int IndirectLightCount { get; private set; }

    /// <summary>Virtual point lights in the last plan.</summary>
    public int VplCount { get; private set; }

    /// <summary>Shadow tiles used by the last plan.</summary>
    public int ShadowedLights { get; private set; }

    /// <summary>Builds the plan for one view and fills the target's light views, light lists and beam outlines.</summary>
    public void Plan(VulkanRenderWorld world, VulkanRenderTarget target, ShadowAtlas? atlas, in PlanInputs inputs)
    {
        var lights = world.Lights;
        var keys = world.LightKeys;
        int n = lights.Length;
        EnsureCapacity(n);
        target.LightViews.Resize(n);
        for (int i = 0; i < n; i++)
        {
            target.LightViews[i] = new GpuLightView { ShadowSlot = ShaderConstants.NoShadow };
            // Lights beyond the clustered-light limit are not drawn: no shadow tile, beam, VPLs or GI for them either.
            _scores[i] = i < inputs.ListedLights ? Score(lights[i], inputs) : 0f;
            _sortKeys[i] = -_scores[i];
            _order[i] = i;
        }

        if (n > 1)
            Array.Sort(_sortKeys, _order, 0, n);

        // Shadow tiles.
        ShadowedLights = 0;
        if (atlas is not null && inputs.Shadows)
        {
            int count = PlanShadowTiles(lights, atlas, inputs);
            for (int c = 0; c < count; c++)
            {
                int i = _candidates[c];
                int tile = atlas.Request(keys[i], lights[i], atlas.TileSize(_levels[c]), inputs.Rsm, inputs.FrameNumber);
                if (tile < 0)
                    continue;
                target.LightViews[i].ShadowSlot = (uint)tile;
                ShadowedLights++;
            }
        }

        // Lists: volumetric beams first (score order), then the indirect-light lights.
        target.Lists.Resize(0);
        BeamCount = 0;
        BeamCoverage = 0f;
        FroxelLights = 0;
        if (inputs.VolumeLights)
        {
            int maxBeams = Math.Clamp(inputs.MaxRaymarchedBeams, 0, ShaderConstants.MaxBeams);
            for (int k = 0; k < n; k++)
            {
                int i = _order[k];
                if (_scores[i] <= 0f)
                    break;
                ref readonly var l = ref lights[i];
                if (BeamCount >= maxBeams || !IsBeam(l))
                {
                    FroxelLights++;
                    continue;
                }

                target.LightViews[i].Flags |= ShaderConstants.LightViewBeam;
                Append(target, (uint)i);
                BeamCount++;
                BeamCoverage += ScreenShare(l, inputs) * Math.Max((int)l.PrismFacets, 1);
            }
        }

        BeamQuality = QualityFor(BeamCoverage);

        IndirectLightCount = 0;
        VplCount = 0;
        if (inputs.Rsm)
        {
            // The most important shadowed lights (at most MaxRsmLights) share the VPL budget: MinVplsPerLight each
            // plus the rest in proportion to their scores, so the gather never evaluates more than VplBudget VPLs.
            int rsmLights = 0;
            float total = 0f;
            for (int k = 0; k < n && rsmLights < MaxRsmLights; k++)
            {
                int i = _order[k];
                if (target.LightViews[i].ShadowSlot == ShaderConstants.NoShadow)
                    continue;
                total += _scores[i];
                rsmLights++;
            }

            int budget = Math.Min(VplBudget, inputs.VplCapacity);
            int spare = Math.Max(budget - MinVplsPerLight * rsmLights, 0);
            for (int k = 0; k < n && IndirectLightCount < rsmLights && total > 0f; k++)
            {
                int i = _order[k];
                if (target.LightViews[i].ShadowSlot == ShaderConstants.NoShadow)
                    continue;
                int count = MinVplsPerLight + (int)(spare * (_scores[i] / total));
                count = Math.Min(count, budget - VplCount);
                if (count <= 0)
                    break;
                ref var view = ref target.LightViews[i];
                view.Flags |= ShaderConstants.LightViewRsm;
                view.VplOffset = (uint)VplCount;
                view.VplCount = (uint)count;
                VplCount += count;
                Append(target, (uint)i);
                IndirectLightCount++;
            }
        }
        else if (inputs.RayTracedGi)
        {
            for (int k = 0; k < n && IndirectLightCount < MaxGiLights; k++)
            {
                int i = _order[k];
                if (_scores[i] <= 0f)
                    break;
                Append(target, (uint)i);
                IndirectLightCount++;
            }
        }

        if (target.Lists.Count == 0)
            Append(target, 0u); // keep the buffer non-empty (unused)
        target.LightViews.MarkDirty(0, n);
        target.Lists.MarkDirty(0, target.Lists.Count);

        target.BeamLines.Resize(0);
        if (inputs.BeamOutlines)
            BuildOutlines(lights, target.BeamLines);
    }

    /// <summary>Quality factor of the beam integration for a load of <paramref name="beamsPerPixel"/> beam crossings per pixel.</summary>
    public static float QualityFor(float beamsPerPixel) =>
        beamsPerPixel > BeamBudget && float.IsFinite(beamsPerPixel) ? Math.Max(BeamBudget / beamsPerPixel, MinBeamQuality) : 1f;

    /// <summary>
    /// Replaces the CPU estimate of the beam load by a GPU measurement (tile-beam pairs per tile of an earlier frame).
    /// </summary>
    public void UseMeasuredBeamLoad(float beamsPerTile) => BeamQuality = QualityFor(beamsPerTile);

    /// <summary>True when a light can be a volumetric beam: a profiled spot light with a bounded cone.</summary>
    public static bool IsBeam(in GpuLight light) =>
        light.Shape != ShaderConstants.ShapeLambertian && light.BoundingAngle <= MaxBeamBoundingAngle && light.MaxIntensity > 0f;

    /// <summary>
    /// Estimated share of the screen covered by a light's beam (at most 1): its cone up to where it meets the floor
    /// (Z = 0; 30 m at most, 10 m when it points up) seen from the side (length × cap radius) or end-on (cap disc),
    /// whichever is larger, projected at the distance of the cone's centre.
    /// </summary>
    public static float ScreenShare(in GpuLight light, in PlanInputs inputs)
    {
        float length = light.Direction.Z < -0.05f ? Math.Clamp(-light.Position.Z / light.Direction.Z, 0.5f, 30f) : 10f;
        float capRadius = length * MathF.Tan(MathF.Min(light.BoundingAngle, 1.4f)) + light.SourceRadius;
        var centre = light.Position + light.Direction * (0.5f * length);
        float area = MathF.Max(length * capRadius, MathF.PI * capRadius * capRadius);
        float scale = inputs.Orthographic
            ? 0.5f * MathF.Max(inputs.OrthoHeight, 1e-3f)
            : MathF.Max(Vector3.Distance(centre, inputs.CameraPosition), 0.5f) * MathF.Max(inputs.TanHalfFovY, 1e-3f);
        // Screen area in units of the half height squared: 2 x 2 x aspect.
        return MathF.Min(area / (scale * scale * 4f * MathF.Max(inputs.Aspect, 0.1f)), 1f);
    }

    /// <summary>Estimated contribution of a light to the view (see the class remarks).</summary>
    public static float Score(in GpuLight light, in PlanInputs inputs)
    {
        if (light.MaxIntensity <= 0f)
            return 0f;
        float flux = light.MaxIntensity * MathF.Max(light.SolidAngle, 1e-6f);
        var sphere = BoundingSphere(light, inputs);
        if (!inputs.Frustum.Intersects(sphere))
            return 0f;
        var c = new Vector3(sphere.X, sphere.Y, sphere.Z);
        float distance = MathF.Max(Vector3.Distance(c, inputs.CameraPosition) - sphere.W, 0f) + 1f;
        float screen = inputs.Orthographic
            ? sphere.W / MathF.Max(0.5f * inputs.OrthoHeight, 1e-3f)
            : sphere.W / (distance * MathF.Max(inputs.TanHalfFovY, 1e-3f));
        return flux * (0.25f + MathF.Min(screen, 1f)) / (distance * distance);
    }

    /// <summary>Bounding sphere of a light's beam up to its range (at most 60 m), or of its range for wide lights.</summary>
    private static Vector4 BoundingSphere(in GpuLight light, in PlanInputs inputs)
    {
        float range = MathF.Min(MathF.Sqrt(light.MaxIntensity * inputs.LightRangeScale), 60f);
        float angle = MathF.Min(light.BoundingAngle, 1.4f);
        if (light.BoundingAngle < 1.05f)
        {
            var centre = light.Position + light.Direction * (0.5f * range);
            float radius = MathF.Sqrt(0.25f * range * range + range * MathF.Tan(angle) * range * MathF.Tan(angle));
            return new Vector4(centre, radius);
        }

        return new Vector4(light.Position, range);
    }

    /// <summary>
    /// The lights that get a shadow tile in this view and their tile levels (size = full resolution / 2^level), in
    /// <see cref="_candidates"/> / <see cref="_levels"/>: the <see cref="PlanInputs.MaxShadowedLights"/> highest-scoring
    /// shadow casters, each a level smaller for every factor of 4 its score is below the best one's, then shrunk —
    /// always the least important of the largest tiles first — until their total area fits the view's share of the
    /// atlas (<see cref="PlanInputs.AtlasShare"/>); casters that do not fit even at the smallest size are dropped.
    /// Levels never decrease along the score order, so the atlas can place the tiles without fragmentation.
    /// </summary>
    /// <returns>Number of candidates.</returns>
    private int PlanShadowTiles(ReadOnlySpan<GpuLight> lights, ShadowAtlas atlas, in PlanInputs inputs)
    {
        int count = 0;
        int n = lights.Length;
        int max = Math.Min(inputs.MaxShadowedLights, _candidates.Length);
        for (int k = 0; k < n && count < max; k++)
        {
            int i = _order[k];
            if (_scores[i] <= 0f)
                break;
            if ((lights[i].Flags & ShaderConstants.LightCastShadows) != 0)
                _candidates[count++] = i;
        }

        if (count == 0)
            return 0;
        float best = _scores[_candidates[0]];
        int maxLevel = atlas.SmallestLevel;
        long area = 0;
        for (int c = 0; c < count; c++)
        {
            float ratio = MathF.Max(best / _scores[_candidates[c]], 1f);
            int level = Math.Clamp((int)MathF.Floor(0.5f * MathF.Log2(ratio)), 0, maxLevel);
            _levels[c] = c > 0 ? Math.Max(level, _levels[c - 1]) : level;
            area += atlas.TileCells(_levels[c]);
        }

        long capacity = atlas.CellCount / Math.Max(inputs.AtlasShare, 1);
        while (area > capacity && _levels[0] < maxLevel)
        {
            // The last of the largest tiles (levels never decrease along the order) becomes one level smaller.
            int lowest = _levels[0];
            int j = 0;
            while (j + 1 < count && _levels[j + 1] == lowest)
                j++;
            area -= atlas.TileCells(lowest) - atlas.TileCells(lowest + 1);
            _levels[j] = lowest + 1;
        }

        while (area > capacity && count > 0)
            area -= atlas.TileCells(_levels[--count]);
        return count;
    }

    private static void Append(VulkanRenderTarget target, uint value)
    {
        int count = target.Lists.Count;
        target.Lists.Resize(count + 1);
        target.Lists[count] = value;
    }

    private void EnsureCapacity(int n)
    {
        if (_scores.Length >= n)
            return;
        int size = Math.Max(n, _scores.Length * 2);
        _scores = new float[size];
        _sortKeys = new float[size];
        _order = new int[size];
    }

    /// <summary>Cone edges of every light: generator lines at the field angle and a ring where the beam ends.</summary>
    private static void BuildOutlines(ReadOnlySpan<GpuLight> lights, SceneBuffer<LineVertex> lines)
    {
        int perLight = 2 * (OutlineGenerators + OutlineRing);
        lines.Resize(lights.Length * perLight);
        int v = 0;
        foreach (ref readonly var light in lights)
        {
            float half = MathF.Min(MathF.Min(light.HalfField, light.IrisAngle), 1.4f);
            var axis = light.Direction;
            float length = axis.Z < -0.05f ? Math.Clamp(-light.Position.Z / axis.Z, 0.5f, 30f) : 10f;
            var rgb709 = ColorSpaces.Rec2020ToRec709(light.Intensity);
            float max = MathF.Max(rgb709.X, MathF.Max(rgb709.Y, rgb709.Z));
            var linear = max > 0f ? Vector3.Max(rgb709 / max, Vector3.Zero) : Vector3.One;
            var color = new Vector4(ColorSpaces.SrgbEncode(Vector3.Min(linear * 0.85f + new Vector3(0.15f), Vector3.One)), 0.8f);
            float radius = length * MathF.Tan(half);
            var centre = light.Position + axis * length;
            for (int g = 0; g < OutlineGenerators; g++)
            {
                float a = 2f * MathF.PI * g / OutlineGenerators;
                var p = centre + (light.Right * MathF.Cos(a) + light.Up * MathF.Sin(a)) * radius;
                lines[v++] = new LineVertex { Position = light.Position, Color = color, Width = 1.5f };
                lines[v++] = new LineVertex { Position = p, Color = color, Width = 1.5f };
            }

            for (int s = 0; s < OutlineRing; s++)
            {
                float a0 = 2f * MathF.PI * s / OutlineRing, a1 = 2f * MathF.PI * (s + 1) / OutlineRing;
                var p0 = centre + (light.Right * MathF.Cos(a0) + light.Up * MathF.Sin(a0)) * radius;
                var p1 = centre + (light.Right * MathF.Cos(a1) + light.Up * MathF.Sin(a1)) * radius;
                lines[v++] = new LineVertex { Position = p0, Color = color, Width = 1.5f };
                lines[v++] = new LineVertex { Position = p1, Color = color, Width = 1.5f };
            }
        }

        lines.MarkDirty(0, v);
    }
}

/// <summary>View inputs of a <see cref="LightPlanner"/> plan.</summary>
internal struct PlanInputs
{
    public Frustum Frustum;
    public Vector3 CameraPosition;
    public bool Orthographic;
    public float TanHalfFovY;

    /// <summary>Width / height of the view.</summary>
    public float Aspect;

    public float OrthoHeight;
    public float LightRangeScale;
    public bool Shadows;
    public int MaxShadowedLights;

    /// <summary>Views sharing the shadow atlas in one frame: each plans its tiles within 1/AtlasShare of it.</summary>
    public int AtlasShare;
    public bool Rsm;
    public bool RayTracedGi;
    public int VplCapacity;

    /// <summary>Volumetric light is drawn in this view (media present): beams are planned.</summary>
    public bool VolumeLights;

    /// <summary>Most volumetric beams (<see cref="PreviewSettings.MaxRaymarchedBeams"/>).</summary>
    public int MaxRaymarchedBeams;
    public bool BeamOutlines;
    public ulong FrameNumber;

    /// <summary>
    /// The renderer's clustered-light limit (<see cref="VulkanRendererOptions.MaxClusteredLights"/>): lights from this
    /// index on are not in the cluster lists, so they score 0 (no shadow tile, volumetric beam, VPLs or GI).
    /// </summary>
    public int ListedLights;
}
