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
    /// <summary>
    /// The saturated colours of the demo look (linear Rec.709 primaries mixed for each colour; white is open white).
    /// </summary>
    public static IReadOnlyList<(string Name, LightColor? Color)> DemoPalette { get; } =
    [
        ("white", null),
        ("deep blue", LightColor.FromRec709(new Vector3(0.02f, 0.06f, 1f))),
        ("magenta", LightColor.FromRec709(new Vector3(1f, 0.02f, 0.55f))),
        ("amber", LightColor.FromRec709(new Vector3(1f, 0.32f, 0.01f))),
        ("cyan", LightColor.FromRec709(new Vector3(0f, 0.7f, 1f))),
        ("red", LightColor.FromRec709(new Vector3(1f, 0.02f, 0.02f))),
    ];

    /// <summary>
    /// A deterministic, show-like state for judging visual quality, written to every fixture's manual values. The rig is
    /// split into bars — fixtures of one type at one height — taken from the top down; each bar gets a colour of
    /// <see cref="DemoPalette"/> (open white, deep blue, magenta, amber, cyan, red in turn) and a pattern:
    /// <list type="bullet">
    /// <item>moving heads low on the stage (less than 3.5 m above the floor) fan their beams out over the audience
    /// (the −Y side, in front of the rig), cross them over the stage, or fan them straight up, bar by bar, symmetric about
    /// the stage's centre line; their zoom is the narrowest;</item>
    /// <item>overhead moving heads colour the floor: washes spread their pools over the stage in two alternating colours
    /// (wide zoom), spots and beams cross onto the floor or fan across the downstage edge with a gobo on every other
    /// bar (narrow zoom);</item>
    /// <item>fixed lights take their bar's colour at full; strobes stay dark (a still image of a strobe is a lottery);</item>
    /// <item>hazers run at <see cref="DemoLookOptions.HazeOutput"/> with their fans at half; fog machines stay off.</item>
    /// </list>
    /// Colours are made with whatever the fixture has (emitters, CMY, colour wheel …, <see cref="DmxLooks.SetColor"/>),
    /// dimmers are at full and shutters open (<see cref="DmxLooks.OpenWhite"/>). The same scene always gets the same
    /// look. Returns the number of fixtures changed; fixtures whose GDTF cannot be resolved are skipped.
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
        float front = bounds.IsEmpty ? centre.Y - 5f : bounds.Min.Y;

        var members = new List<(Entity Entity, FixtureComponent Fixture, FixtureProfile Profile)>();
        foreach (var (entity, fixture) in scene.Query<FixtureComponent>())
            if (engine.Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode).Profile is { } profile)
                members.Add((entity, fixture, profile));

        // bars: one type at one height, from the top of the rig down, then by type name for a stable order
        var bars = members
            .GroupBy(m => (Type: m.Profile.Key, Band: (int)MathF.Round(m.Entity.WorldPosition.Z / 0.5f)))
            .OrderByDescending(g => g.Key.Band).ThenBy(g => g.Key.Type, StringComparer.Ordinal)
            .Select(g => g.OrderBy(m => m.Entity.WorldPosition.X).ThenBy(m => m.Entity.WorldPosition.Y).ThenBy(m => m.Entity.Id).ToList())
            .ToList();

        var edits = new List<(FixtureComponent Fixture, Dictionary<string, float> Values)>();
        int lowBar = 0, highBar = 0, colourIndex = 0;
        foreach (var bar in bars)
        {
            var profile = bar[0].Profile;
            if (profile.IsAtmospheric)
            {
                foreach (var m in bar) edits.Add((m.Fixture, Atmosphere(profile, options.HazeOutput)));
                continue;
            }
            if (profile.Category == FixtureCategory.Strobe)
            {
                foreach (var m in bar) edits.Add((m.Fixture, new Dictionary<string, float>(StringComparer.Ordinal)));
                continue;
            }
            var (pan, tilt) = PanTilt(profile);
            bool moving = pan >= 0 || tilt >= 0;
            bool low = bar[0].Entity.WorldPosition.Z - floor < 3.5f;
            bool wash = profile.Category is FixtureCategory.Wash or FixtureCategory.Par or FixtureCategory.LedBar;
            var colour = DemoPalette[colourIndex % DemoPalette.Count].Color;
            var second = DemoPalette[(colourIndex + 2) % DemoPalette.Count].Color;
            colourIndex++;
            int pattern = moving ? (low ? lowBar++ : highBar++) : 0;
            float zoom = low || !wash ? 0f : 0.7f;
            bool gobo = moving && !low && !wash && pattern % 2 == 1;

            for (int i = 0; i < bar.Count; i++)
            {
                var (entity, fixture, p) = bar[i];
                var position = entity.WorldPosition;
                // the bar's own symmetry: −1 at its left end … +1 at its right end, mirrored about the stage centre line
                float u = Math.Clamp((position.X - centre.X) / halfWidth, -1.5f, 1.5f);
                var buffer = new FixtureDmxBuffer(p);
                DmxLooks.OpenWhite(buffer, zoom);
                var c = wash && !low && Mirror(position.X - centre.X) ? second : colour;
                if (c is { } light) DmxLooks.SetColor(buffer, light);
                if (gobo) SetGobo(p, buffer, pattern / 2);
                var state = new FixtureState(p);
                if (moving)
                {
                    Vector3 target = low
                        ? (pattern % 3) switch
                        {
                            0 => new Vector3(centre.X + u * halfWidth * 2.2f, front - 12f, floor + 7f), // fan over the audience
                            1 => new Vector3(centre.X - u * halfWidth * 0.9f, position.Y + 3f, floor + 9f), // crossing over the stage
                            _ => new Vector3(centre.X + u * halfWidth * 1.4f, position.Y + 1f, floor + 16f), // fan straight up
                        }
                        : wash
                            ? new Vector3(centre.X + 0.6f * (position.X - centre.X), centre.Y + 0.6f * (position.Y - centre.Y), floor) // pools over the stage
                            : (pattern % 2) switch
                            {
                                0 => new Vector3(centre.X - 0.8f * (position.X - centre.X), centre.Y + 0.4f * (position.Y - centre.Y), floor), // crossing onto the floor
                                _ => new Vector3(centre.X + u * halfWidth * 0.9f, front + 1.5f, floor), // fan across the downstage edge
                            };
                    Aim(p, buffer, state, pan, tilt, entity.WorldMatrix, target);
                }
                FixtureEvaluator.Evaluate(state, buffer, 0, null, applyDynamics: false);
                var values = new Dictionary<string, float>(StringComparer.Ordinal);
                state.CopyAttributes(values);
                edits.Add((fixture, values));
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

    /// <summary>
    /// Which of a wash bar's two colours a fixture takes: they alternate every 1.5 m outwards from the stage's centre
    /// line, so mirror partners match.
    /// </summary>
    private static bool Mirror(float offsetX) => (int)MathF.Round(MathF.Abs(offsetX) / 1.5f) % 2 == 1;

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

    /// <summary>Puts a gobo of the first gobo wheel in: the <paramref name="choice"/>-th slot that is not open.</summary>
    private static void SetGobo(FixtureProfile profile, FixtureDmxBuffer buffer, int choice)
    {
        foreach (var f in profile.Functions)
        {
            if (f.Class.Kind != AttributeKind.WheelSelect || f.Class.Wheel != WheelFamily.Gobo || f.Wheel < 0) continue;
            var wheel = profile.Wheels[f.Wheel];
            var gobos = Enumerable.Range(0, wheel.SlotCount).Where(s => !wheel.SlotOpen[s]).ToList();
            if (gobos.Count == 0) return;
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
