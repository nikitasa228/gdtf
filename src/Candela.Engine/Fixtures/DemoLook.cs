using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Gdtf;
using Candela.Photometry;

namespace Candela.Engine;

/// <summary>Options of <see cref="StageLooks.ApplyDemo"/>.</summary>
public sealed record DemoLookOptions
{
    /// <summary>Records the change as one undo step (default true).</summary>
    public bool Undoable { get; init; } = true;

    /// <summary>Output of hazers (0 … 1) while the look runs; fog machines stay off.</summary>
    public float HazeOutput { get; init; } = 0.6f;
}

public static partial class StageLooks
{
    /// <summary>Moving heads lower than this above the floor (m) are "low": they fan and cross in the air.</summary>
    private const float LowRigHeight = 3.5f;

    /// <summary>
    /// The colours of the demo look in the order the fixture groups take them (linear Rec.709 primaries mixed for each
    /// colour; white is D65, so an LED engine whose full-on mix is tinted is balanced to it).
    /// </summary>
    public static IReadOnlyList<(string Name, LightColor Color)> DemoPalette { get; } =
    [
        ("white", LightColor.FromRec709(Vector3.One)),
        ("deep blue", LightColor.FromRec709(new Vector3(0.02f, 0.06f, 1f))),
        ("magenta", LightColor.FromRec709(new Vector3(1f, 0.02f, 0.55f))),
        ("amber", LightColor.FromRec709(new Vector3(1f, 0.32f, 0.01f))),
        ("cyan", LightColor.FromRec709(new Vector3(0f, 0.7f, 1f))),
        ("red", LightColor.FromRec709(new Vector3(1f, 0.02f, 0.02f))),
    ];

    /// <summary>What a group of moving heads does in the demo look.</summary>
    private enum DemoPattern
    {
        /// <summary>Low, downstage row: a symmetric fan up and out over the audience.</summary>
        AudienceFan,

        /// <summary>Low, downstage row: beams that cross each other over the audience.</summary>
        AudienceCrossing,

        /// <summary>Low side columns: each side's beams cross over the stage to the other side.</summary>
        StageCrossing,

        /// <summary>Low, mid- or upstage row: a symmetric fan of steep beams.</summary>
        SkyFan,

        /// <summary>Overhead washes: colour pools on the floor, two colours alternating outwards from the centre line.</summary>
        FloorPools,

        /// <summary>Overhead spots and beams: each side's beams cross onto the floor of the other side.</summary>
        FloorCrossing,

        /// <summary>Overhead spots and beams: a fan across the downstage edge.</summary>
        DownstageFan,
    }

    /// <summary>
    /// A deterministic, show-like state for judging visual quality, written to every fixture's manual values. The rig is
    /// split into groups — fixtures of one type at one height in one line: a row across the stage, or the
    /// pair of columns at the same distance left and right of the centre line — taken from the top of the rig down. Each
    /// group takes the next colour of <see cref="DemoPalette"/> (white, deep blue, magenta, amber, cyan, red) and a
    /// pattern, symmetric about the stage's centre line:
    /// <list type="bullet">
    /// <item>moving heads low in the rig (less than 3.5 m above the floor) at the narrowest zoom: downstage rows fan up and
    /// out over the audience (the −Y side) or cross over it, side columns cross over the stage or fan up, other rows fan
    /// up;</item>
    /// <item>overhead moving heads: washes at a wide zoom colour the floor with pools in their group's colour and the one
    /// after next, alternating every 1.5 m outwards from the centre line; spots and beams at the narrowest zoom cross onto
    /// the floor or fan across the downstage edge, group by group; every other overhead group with a gobo wheel projects
    /// a gobo;</item>
    /// <item>fixed lights take their group's colour at full; strobes stay dark (a still image of a strobe is a lottery);</item>
    /// <item>hazers run at <see cref="DemoLookOptions.HazeOutput"/> with their fans at half; fog machines stay off.</item>
    /// </list>
    /// Colours use whatever the fixture has (<see cref="DmxLooks.SetColor"/>: emitters, CMY, colour wheel …), dimmers
    /// are at full and shutters open (<see cref="DmxLooks.OpenWhite"/>). The same scene always gets the same look.
    /// Returns the number of fixtures changed; fixtures whose GDTF cannot be resolved are skipped.
    /// </summary>
    public static int ApplyDemo(StageEngine engine, DemoLookOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        options ??= new DemoLookOptions();
        var scene = engine.Scene;
        var centre = StageCentre(engine);
        float floor = centre.Z;
        var bounds = SceneBounds.Compute(scene, engine.Models.GetBounds);
        float halfWidth = bounds.IsEmpty ? 5f : MathF.Max(1f, 0.5f * bounds.Size.X);
        float halfDepth = bounds.IsEmpty ? 5f : MathF.Max(1f, 0.5f * bounds.Size.Y);
        float front = bounds.IsEmpty ? centre.Y - 5f : bounds.Min.Y;

        var members = new List<DemoMember>();
        var resolve = engine.FixtureResolver();
        foreach (var (entity, fixture) in scene.Query<FixtureComponent>())
        {
            var position = entity.WorldPosition;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) continue;
            if (resolve(fixture).Profile is { } profile)
                members.Add(new DemoMember(entity, fixture, profile, position));
        }

        var edits = new List<(FixtureComponent Fixture, Dictionary<string, float> Values)>();
        int colourIndex = 0, audienceGroups = 0, lowCrossings = 0, overheadBeams = 0, goboGroups = 0;
        foreach (var group in DemoGroups(members, centre))
        {
            var profile = group.Members[0].Profile;
            if (profile.IsAtmospheric)
            {
                foreach (var m in group.Members) edits.Add((m.Fixture, Atmosphere(profile, options.HazeOutput)));
                continue;
            }
            if (profile.Category == FixtureCategory.Strobe)
            {
                foreach (var m in group.Members) edits.Add((m.Fixture, new Dictionary<string, float>(StringComparer.Ordinal)));
                continue;
            }
            var (pan, tilt) = PanTilt(profile);
            bool moving = pan >= 0 || tilt >= 0;
            bool low = group.Height - floor < LowRigHeight;
            bool wash = profile.Category is FixtureCategory.Wash or FixtureCategory.Par or FixtureCategory.LedBar;
            var colour = DemoPalette[colourIndex % DemoPalette.Count].Color;
            var second = DemoPalette[(colourIndex + 2) % DemoPalette.Count].Color;
            colourIndex++;

            DemoPattern? pattern = null;
            if (moving && low)
            {
                pattern = group.IsColumn ? DemoPattern.StageCrossing
                    : group.Line < centre.Y - 0.5f * halfDepth ? DemoPattern.AudienceFan
                    : DemoPattern.SkyFan;
                if (pattern == DemoPattern.StageCrossing && lowCrossings++ % 2 == 1) pattern = DemoPattern.SkyFan;
                if (pattern == DemoPattern.AudienceFan && audienceGroups++ % 2 == 1) pattern = DemoPattern.AudienceCrossing;
            }
            else if (moving)
            {
                pattern = wash ? DemoPattern.FloorPools : overheadBeams++ % 2 == 0 ? DemoPattern.FloorCrossing : DemoPattern.DownstageFan;
            }
            // textured light on the floor: every other overhead group whose fixtures have a gobo wheel
            bool gobo = pattern is DemoPattern.FloorPools or DemoPattern.FloorCrossing or DemoPattern.DownstageFan && HasGobos(profile) &&
                goboGroups++ % 2 == 1;
            float zoom = pattern == DemoPattern.FloorPools ? 0.7f : wash && !moving ? 0.5f : 0f;

            foreach (var m in group.Members)
            {
                var p = m.Profile;
                var position = m.Position;
                float dx = position.X - centre.X;
                // −1 … +1 across the stage (beyond for fixtures outside the scene's bounds), mirrored about the centre line
                float u = Math.Clamp(dx / halfWidth, -1.5f, 1.5f);
                var buffer = new FixtureDmxBuffer(p);
                DmxLooks.OpenWhite(buffer, zoom);
                DmxLooks.SetColor(buffer, pattern == DemoPattern.FloorPools && AlternateBand(dx) ? second : colour);
                if (gobo) SetGobo(p, buffer, goboGroups / 2);
                var state = new FixtureState(p);
                if (pattern is { } kind)
                {
                    var target = kind switch
                    {
                        DemoPattern.AudienceFan => new Vector3(centre.X + u * halfWidth * 1.6f, front - 8f, floor + 14f),
                        DemoPattern.AudienceCrossing => new Vector3(centre.X - u * halfWidth * 1.2f, front - 8f, floor + 12f),
                        DemoPattern.StageCrossing => new Vector3(centre.X - MathF.Sign(dx) * halfWidth * 0.9f, position.Y + 2f, floor + 7f),
                        DemoPattern.SkyFan => new Vector3(centre.X + u * halfWidth * 2.5f, position.Y + 2f, floor + 12f),
                        DemoPattern.FloorPools => new Vector3(centre.X + 0.6f * dx, centre.Y + 0.6f * (position.Y - centre.Y), floor),
                        DemoPattern.FloorCrossing => new Vector3(centre.X - 0.8f * dx, centre.Y + 0.4f * (position.Y - centre.Y), floor),
                        _ => new Vector3(centre.X + u * halfWidth * 0.9f, front + 1.5f, floor),
                    };
                    Aim(p, buffer, state, pan, tilt, m.Entity.WorldMatrix, target);
                }
                FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: false);
                var values = new Dictionary<string, float>(StringComparer.Ordinal);
                state.CopyAttributes(values);
                edits.Add((m.Fixture, values));
            }
        }

        if (options.Undoable)
        {
            using (engine.Undo.Group("Demo look"))
            {
                foreach (var (fixture, values) in edits)
                    engine.Undo.Execute(ModifyComponentCommand.Create(fixture, f => f.ManualAttributes = values, "Demo look"));
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

    private sealed record DemoMember(Entity Entity, FixtureComponent Fixture, FixtureProfile Profile, Vector3 Position);

    /// <summary>
    /// A group of the demo look: fixtures of one type at one height in one line — a row (<see cref="Line"/> is its Y) or
    /// a pair of mirrored columns (<see cref="Line"/> is their distance from the centre line).
    /// </summary>
    private sealed record DemoGroup(List<DemoMember> Members, float Height, bool IsColumn, float Line);

    /// <summary>
    /// The demo look's groups in a stable order: by height from the top down, then type, then rows from downstage to
    /// upstage before column pairs from the outside in. Heights, rows (Y) and column pairs (distance from the centre
    /// line) are clusters of values less than 0.4 m apart. A fixture belongs to its row when the row has three or more
    /// fixtures of its type and height, or at least as many as its column pair; otherwise to the column pair.
    /// </summary>
    private static IEnumerable<DemoGroup> DemoGroups(List<DemoMember> members, Vector3 centre)
    {
        var height = Clusters(members.Select(m => m.Position.Z));
        var bars = members
            .GroupBy(m => (Type: m.Profile.Key, Height: height[m.Position.Z]))
            .OrderByDescending(g => g.Key.Height).ThenBy(g => g.Key.Type, StringComparer.Ordinal);
        foreach (var bar in bars)
        {
            var list = bar.ToList();
            var row = Clusters(list.Select(m => m.Position.Y));
            var column = Clusters(list.Select(m => MathF.Abs(m.Position.X - centre.X)));
            var rowCount = list.GroupBy(m => row[m.Position.Y]).ToDictionary(g => g.Key, g => g.Count());
            var columnCount = list.GroupBy(m => column[MathF.Abs(m.Position.X - centre.X)]).ToDictionary(g => g.Key, g => g.Count());
            var lines = list
                .GroupBy(m =>
                {
                    int r = row[m.Position.Y], c = column[MathF.Abs(m.Position.X - centre.X)];
                    // a real row (three or more) keeps its ends; a fixture with a partner across the stage only is in a column pair
                    return rowCount[r] < 3 && columnCount[c] > rowCount[r] ? (Column: true, Key: c) : (Column: false, Key: r);
                })
                .OrderBy(g => g.Key.Column).ThenBy(g => g.Key.Column ? -g.Key.Key : g.Key.Key);
            foreach (var line in lines)
            {
                var ordered = line.OrderBy(m => m.Position.X).ThenBy(m => m.Position.Y).ThenBy(m => m.Entity.Id).ToList();
                float z = ordered.Average(m => m.Position.Z);
                float key = line.Key.Column ? ordered.Average(m => MathF.Abs(m.Position.X - centre.X)) : ordered.Average(m => m.Position.Y);
                yield return new DemoGroup(ordered, z, line.Key.Column, key);
            }
        }
    }

    /// <summary>
    /// Clusters of values (m): sorted, a new cluster starts where the gap to the previous value exceeds 0.4 m. Maps each
    /// value to its cluster's index (ascending with the values).
    /// </summary>
    private static Dictionary<float, int> Clusters(IEnumerable<float> values)
    {
        var map = new Dictionary<float, int>();
        int cluster = -1;
        float previous = float.NegativeInfinity;
        foreach (float v in values.Distinct().Order())
        {
            if (v - previous > 0.4f) cluster++;
            map[v] = cluster;
            previous = v;
        }
        return map;
    }

    /// <summary>
    /// Which of a wash group's two colours a fixture takes: they alternate every 1.5 m outwards from the stage's centre
    /// line, so mirror partners match.
    /// </summary>
    private static bool AlternateBand(float offsetX) => (int)MathF.Round(MathF.Abs(offsetX) / 1.5f) % 2 == 1;

    /// <summary>The first pan and tilt functions of a profile (−1 when it has none).</summary>
    private static (int Pan, int Tilt) PanTilt(FixtureProfile profile)
    {
        int pan = -1, tilt = -1;
        foreach (var f in profile.Functions)
        {
            if (f.Class.Kind == AttributeKind.Pan && pan < 0) pan = f.Index;
            else if (f.Class.Kind == AttributeKind.Tilt && tilt < 0) tilt = f.Index;
        }
        return (pan, tilt);
    }

    /// <summary>True when the profile selects slots of a gobo wheel that has at least one gobo (a slot that is not open).</summary>
    private static bool HasGobos(FixtureProfile profile)
    {
        foreach (var f in profile.Functions)
        {
            if (f.Class.Kind != AttributeKind.WheelSelect || f.Class.Wheel != WheelFamily.Gobo || f.Wheel < 0) continue;
            var wheel = profile.Wheels[f.Wheel];
            for (int s = 0; s < wheel.SlotCount; s++)
                if (!wheel.SlotOpen[s]) return true;
        }
        return false;
    }

    /// <summary>Puts a gobo of the first gobo wheel in: the <paramref name="choice"/>-th slot that is not open.</summary>
    private static void SetGobo(FixtureProfile profile, FixtureDmxBuffer buffer, int choice)
    {
        foreach (var f in profile.Functions)
        {
            if (f.Class.Kind != AttributeKind.WheelSelect || f.Class.Wheel != WheelFamily.Gobo || f.Wheel < 0) continue;
            var wheel = profile.Wheels[f.Wheel];
            var gobos = Enumerable.Range(0, wheel.SlotCount).Where(s => !wheel.SlotOpen[s]).ToList();
            if (gobos.Count == 0) continue;
            DmxEncoder.EncodeFunction(buffer, f.Index, gobos[choice % gobos.Count] + 1);
            return;
        }
    }

    /// <summary>Manual values of an atmospheric device: haze at <paramref name="haze"/>, fans at half, fog off.</summary>
    private static Dictionary<string, float> Atmosphere(FixtureProfile profile, float haze)
    {
        var buffer = new FixtureDmxBuffer(profile);
        buffer.ResetToDefaults();
        haze = float.IsFinite(haze) ? Math.Clamp(haze, 0f, 1f) : 0.6f;
        foreach (var f in profile.Functions)
        {
            float level = f.Class.Kind switch
            {
                AttributeKind.Haze => haze,
                AttributeKind.Fan => 0.5f,
                AttributeKind.Fog => 0f,
                _ => float.NaN,
            };
            if (float.IsNaN(level)) continue;
            DmxEncoder.EncodeFunction(buffer, f.Index, f.MinValue + level * (f.MaxValue - f.MinValue));
        }
        var state = FixtureEvaluator.Evaluate(profile, buffer);
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        state.CopyAttributes(values);
        return values;
    }
}
