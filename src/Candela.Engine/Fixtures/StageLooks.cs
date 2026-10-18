using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;

namespace Candela.Engine;

/// <summary>Options of <see cref="StageLooks.ApplyOpenWhite"/>.</summary>
public sealed record OpenWhiteOptions
{
    /// <summary>Point the moving heads aim at (world, m); null: the centre of the scene's floor.</summary>
    public Vector3? Target { get; init; }

    /// <summary>
    /// How far each head's aim point stays towards the head's own floor position, 0 … 1: 0 aims every head at
    /// <see cref="Target"/> (one hot spot), 0.75 (default) points every head mostly down, leaning towards the target, so the pools spread over the stage.
    /// </summary>
    public float Spread { get; init; } = 0.75f;

    /// <summary>Zoom position within the fixture's zoom range, 0 (narrowest) … 1 (widest); default the middle.</summary>
    public float Zoom { get; init; } = 0.5f;

    /// <summary>Records the change as one undo step (default true).</summary>
    public bool Undoable { get; init; } = true;
}

/// <summary>
/// Ready-made looks that set the fixtures' manual values (used when no console drives them), e.g. for a first render
/// of an imported scene.
/// </summary>
public static partial class StageLooks
{
    private const int PanSteps = 36;
    private const int TiltSteps = 18;

    /// <summary>
    /// "Open white": every light at full intensity with its shutter open, open white (<see cref="DmxLooks.OpenWhite"/>:
    /// highlight values of the GDTF, the brightest dimmer range, shutters open without strobe, additive emitters at full,
    /// indirect RGB white), zoom at <see cref="OpenWhiteOptions.Zoom"/>, and moving heads
    /// (fixtures with pan or tilt) aimed towards the stage centre. Writes <see cref="FixtureComponent.ManualAttributes"/>
    /// (one undo step) and returns the number of fixtures changed; fixtures whose GDTF cannot be resolved are skipped.
    /// </summary>
    public static int ApplyOpenWhite(StageEngine engine, OpenWhiteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        options ??= new OpenWhiteOptions();
        var scene = engine.Scene;
        var target = options.Target ?? StageCentre(engine);
        float spread = float.IsFinite(options.Spread) ? Math.Clamp(options.Spread, 0f, 1f) : 0.75f;
        float zoom = float.IsFinite(options.Zoom) ? Math.Clamp(options.Zoom, 0f, 1f) : 0.5f;

        var edits = new List<(FixtureComponent Fixture, Dictionary<string, float> Values)>();
        foreach (var (entity, fixture) in scene.Query<FixtureComponent>().ToList())
        {
            var profile = engine.Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode).Profile;
            if (profile is null) continue;
            var aim = target;
            var p = entity.WorldPosition;
            if (float.IsFinite(p.X) && float.IsFinite(p.Y)) aim += spread * new Vector3(p.X - target.X, p.Y - target.Y, 0f);
            var values = OpenWhite(profile, entity.WorldMatrix, aim, zoom);
            if (values.Count > 0) edits.Add((fixture, values));
        }

        if (options.Undoable)
        {
            using (engine.Undo.Group("Open white"))
            {
                foreach (var (fixture, values) in edits)
                    engine.Undo.Execute(ModifyComponentCommand.Create(fixture, f => f.ManualAttributes = values, "Open white"));
            }
        }
        else
        {
            foreach (var (fixture, values) in edits)
            {
                fixture.ManualAttributes = values;
                fixture.NotifyChanged();
            }
        }
        return edits.Count;
    }

    /// <summary>
    /// Manual values for open white on one fixture of <paramref name="profile"/> placed at <paramref name="fixtureWorld"/>,
    /// aiming moving heads at <paramref name="target"/>. The values reproduce the encoded DMX when compiled back
    /// (checked; attributes are added until the round trip gives the same intensity).
    /// </summary>
    public static Dictionary<string, float> OpenWhite(FixtureProfile profile, Matrix4x4 fixtureWorld, Vector3 target, float zoom = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var buffer = new FixtureDmxBuffer(profile);
        var touched = new HashSet<int>(DmxLooks.OpenWhite(buffer, zoom));

        int pan = -1, tilt = -1;
        foreach (var f in profile.Functions)
        {
            if (f.Class.Kind == AttributeKind.Pan && pan < 0) pan = f.Index;
            else if (f.Class.Kind == AttributeKind.Tilt && tilt < 0) tilt = f.Index;
        }
        if (pan >= 0) touched.Add(profile.Functions[pan].Channel);
        if (tilt >= 0) touched.Add(profile.Functions[tilt].Channel);

        var state = new FixtureState(profile);
        if (pan >= 0 || tilt >= 0) Aim(profile, buffer, state, pan, tilt, fixtureWorld, target);

        FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: false);
        float wanted = MaxIntensity(state);
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        AddChannels(profile, state, touched, values);
        if (!RoundTrips(profile, values, wanted))
        {
            // some channel's default differs from what this look needs: carry every channel's value
            values.Clear();
            state.CopyAttributes(values);
        }
        return values;
    }

    /// <summary>The centre of the scene's content on its floor (lowest content height, at least 0), m.</summary>
    public static Vector3 StageCentre(StageEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var bounds = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);
        if (bounds.IsEmpty) return Vector3.Zero;
        var c = bounds.Center;
        return new Vector3(c.X, c.Y, Math.Max(0f, Math.Min(bounds.Min.Z, 0.5f)));
    }

    private static float MaxIntensity(FixtureState state)
    {
        float max = 0f;
        foreach (var b in state.Beams) max = Math.Max(max, b.Intensity);
        return max;
    }

    private static void AddChannels(FixtureProfile profile, FixtureState state, HashSet<int> channels, Dictionary<string, float> values)
    {
        foreach (int c in channels)
        {
            var v = state.GetChannel(c);
            if (v.Function < 0 || !float.IsFinite(v.Value)) continue;
            var ch = profile.Channels[c];
            string attribute = profile.Functions[v.Function].Attribute;
            values[ch.Instance.References.Count > 0 ? $"{attribute}@{ch.InstanceName}" : attribute] = v.Value;
        }
    }

    private static bool RoundTrips(FixtureProfile profile, Dictionary<string, float> values, float wanted)
    {
        var overrides = AttributeOverrides.Compile(profile, values);
        var buffer = new FixtureDmxBuffer(profile);
        buffer.ResetToDefaults();
        var state = new FixtureState(profile);
        FixtureEvaluator.Evaluate(state, buffer, 0, overrides, applyDynamics: false);
        return MaxIntensity(state) >= wanted * 0.98f - 1e-4f;
    }

    /// <summary>
    /// Finds pan/tilt (physical values of the two functions) that point the first beam at <paramref name="target"/>:
    /// a grid search over both ranges, then pattern search with halving steps. Writes the result into
    /// <paramref name="buffer"/>.
    /// </summary>
    private static void Aim(FixtureProfile profile, FixtureDmxBuffer buffer, FixtureState state, int pan, int tilt, Matrix4x4 fixtureWorld, Vector3 target)
    {
        if (profile.Beams.Count == 0) return;
        int beamPart = profile.Beams[0].PartIndex;
        var animation = new Matrix4x4[profile.Geometry.Parts.Count];
        var world = new Matrix4x4[profile.Geometry.Parts.Count];
        var functions = profile.Functions;
        float panMin = pan >= 0 ? functions[pan].MinValue : 0f, panMax = pan >= 0 ? functions[pan].MaxValue : 0f;
        float tiltMin = tilt >= 0 ? functions[tilt].MinValue : 0f, tiltMax = tilt >= 0 ? functions[tilt].MaxValue : 0f;

        float Error(float p, float t)
        {
            if (pan >= 0) DmxEncoder.EncodeFunction(buffer, pan, p);
            if (tilt >= 0) DmxEncoder.EncodeFunction(buffer, tilt, t);
            FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: false);
            state.WriteAnimation(animation);
            profile.Geometry.ComputeWorldTransforms(world, fixtureWorld, animation);
            var m = world[beamPart];
            var dir = -new Vector3(m.M31, m.M32, m.M33);
            var to = target - m.Translation;
            float dl = dir.Length(), tl = to.Length();
            if (!(dl > 1e-9f) || !(tl > 1e-9f) || !float.IsFinite(dl) || !float.IsFinite(tl)) return float.MaxValue;
            float cos = Vector3.Dot(dir, to) / (dl * tl);
            return 1f - Math.Clamp(cos, -1f, 1f);
        }

        float bestP = 0.5f * (panMin + panMax), bestT = 0.5f * (tiltMin + tiltMax);
        float best = float.MaxValue;
        int ps = pan >= 0 ? PanSteps : 1, ts = tilt >= 0 ? TiltSteps : 1;
        for (int i = 0; i < ps; i++)
        {
            float p = ps == 1 ? bestP : panMin + (panMax - panMin) * i / (ps - 1);
            for (int j = 0; j < ts; j++)
            {
                float t = ts == 1 ? bestT : tiltMin + (tiltMax - tiltMin) * j / (ts - 1);
                float e = Error(p, t);
                if (e < best)
                {
                    best = e;
                    bestP = p;
                    bestT = t;
                }
            }
        }
        float stepP = (panMax - panMin) / Math.Max(1, ps - 1), stepT = (tiltMax - tiltMin) / Math.Max(1, ts - 1);
        for (int iteration = 0; iteration < 40 && (stepP > 1e-3f || stepT > 1e-3f); iteration++)
        {
            bool improved = false;
            foreach (var (dp, dt) in new[] { (stepP, 0f), (-stepP, 0f), (0f, stepT), (0f, -stepT) })
            {
                float p = Math.Clamp(bestP + dp, Math.Min(panMin, panMax), Math.Max(panMin, panMax));
                float t = Math.Clamp(bestT + dt, Math.Min(tiltMin, tiltMax), Math.Max(tiltMin, tiltMax));
                float e = Error(p, t);
                if (e < best)
                {
                    best = e;
                    bestP = p;
                    bestT = t;
                    improved = true;
                }
            }
            if (!improved)
            {
                stepP *= 0.5f;
                stepT *= 0.5f;
            }
        }
        if (pan >= 0) DmxEncoder.EncodeFunction(buffer, pan, bestP);
        if (tilt >= 0) DmxEncoder.EncodeFunction(buffer, tilt, bestT);
    }
}
