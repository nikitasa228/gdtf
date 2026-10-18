using System.Globalization;
using System.Numerics;
using System.Text;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Gdtf;
using Candela.Photometry;
using RenderSpotLight = Candela.Rendering.SpotLight;

namespace Candela.Engine;

/// <summary>One fixture's share of the illuminance at a point.</summary>
/// <param name="FixtureId">Entity id of the fixture (or flame).</param>
/// <param name="Lux">Illuminance from its lights, lx.</param>
public readonly record struct LuxContribution(Guid FixtureId, float Lux);

/// <summary>
/// Illuminance queries against the lights of the current frame — for tooltips under the cursor, measurement points
/// and reports — with <see cref="Candela.Photometry.LightMeter"/>, the CPU reference the GPU lux overlay is tested
/// against: <c>E = I(θ)·cos α / d²</c> summed over every lit beam (candela, zoom angles and beam type from the DMX
/// state). Gobos, iris, framing blades, prisms and shadows are not applied (open beams, free field).
/// </summary>
public sealed class LightMeterService
{
    private readonly StageEngine _engine;
    private Photometry.SpotLight[] _lights = Array.Empty<Photometry.SpotLight>();
    private Guid[] _fixtures = Array.Empty<Guid>();
    private int _count;
    private bool _valid;

    internal LightMeterService(StageEngine engine) => _engine = engine;

    /// <summary>Number of lights the meter currently sums.</summary>
    public int LightCount
    {
        get
        {
            Refresh();
            return _count;
        }
    }

    /// <summary>Illuminance on a surface at <paramref name="point"/> facing <paramref name="normal"/>, lx.</summary>
    public float IlluminanceAt(Vector3 point, Vector3 normal)
    {
        Refresh();
        return Photometry.LightMeter.IlluminanceAt(_lights.AsSpan(0, _count), point, normal);
    }

    /// <summary>Illuminance on a horizontal surface facing up (stage floor), lx.</summary>
    public float HorizontalIlluminanceAt(Vector3 point) => IlluminanceAt(point, Vector3.UnitZ);

    /// <summary>Colour of the light arriving at a point (linear Rec.2020, luminance-weighted), with its illuminance.</summary>
    public Vector3 IlluminanceRgbAt(Vector3 point, Vector3 normal)
    {
        Refresh();
        return Photometry.LightMeter.IlluminanceRgbAt(_lights.AsSpan(0, _count), point, normal);
    }

    /// <summary>The fixtures lighting a point, brightest first (at most <paramref name="max"/>), for a tooltip breakdown.</summary>
    public IReadOnlyList<LuxContribution> Breakdown(Vector3 point, Vector3 normal, int max = 8)
    {
        Refresh();
        var perFixture = new Dictionary<Guid, float>();
        var one = new Photometry.SpotLight[1];
        for (int i = 0; i < _count; i++)
        {
            one[0] = _lights[i];
            float e = Photometry.LightMeter.IlluminanceAt(one, point, normal);
            if (!(e > 0f)) continue;
            perFixture[_fixtures[i]] = perFixture.GetValueOrDefault(_fixtures[i]) + e;
        }
        return perFixture.Select(kv => new LuxContribution(kv.Key, kv.Value))
            .OrderByDescending(c => c.Lux).ThenBy(c => c.FixtureId).Take(Math.Max(0, max)).ToList();
    }

    /// <summary>
    /// Illuminance on a regular grid of a plane (e.g. the stage floor), lx: <paramref name="countU"/> ×
    /// <paramref name="countV"/> points from <paramref name="origin"/> in steps <paramref name="stepU"/>/<paramref name="stepV"/>,
    /// row by row (u fastest), on a surface facing stepU × stepV (u = +X, v = +Y measures a floor facing up).
    /// </summary>
    public float[] IlluminanceOnGrid(Vector3 origin, Vector3 stepU, Vector3 stepV, int countU, int countV)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(countU);
        ArgumentOutOfRangeException.ThrowIfNegative(countV);
        Refresh();
        var result = new float[countU * countV];
        Photometry.LightMeter.IlluminanceOnGrid(_lights.AsSpan(0, _count), origin, stepU, stepV, countU, countV, result);
        return result;
    }

    /// <summary>
    /// The light plot of the scene: one row per fixture with its id, type, mode, DMX address, power, weight and
    /// position, ordered by fixture id, plus totals.
    /// </summary>
    public LightPlotReport BuildLightPlot()
    {
        var rows = new List<LightPlotRow>();
        var scene = _engine.Scene;
        foreach (var (entity, fixture) in scene.Query<FixtureComponent>())
        {
            var resolution = _engine.Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode);
            var electrical = _engine.ElectricalProfiles.GetProfile(fixture.GdtfSpec, fixture.GdtfMode);
            var specs = resolution.Profile?.Specs ?? resolution.Entry?.Specs;
            string type = resolution.Profile is { } p
                ? $"{p.FixtureType.Manufacturer} {p.FixtureType.LongName.NullIfEmpty() ?? p.FixtureType.Name}".Trim()
                : resolution.Entry?.DisplayName ?? fixture.GdtfSpec;
            var addresses = fixture.Addresses.OrderBy(a => a.Break).Select(a => a.ToString()).ToList();
            float peak = 0f;
            if (_engine.Dmx.TryGetState(entity.Id, out var state))
                foreach (var beam in state.Beams)
                    peak = Math.Max(peak, beam.PeakCandela);
            rows.Add(new LightPlotRow(
                entity.Id,
                fixture.FixtureId,
                entity.Name,
                type,
                resolution.Profile?.Mode.Name ?? fixture.GdtfMode ?? "",
                (resolution.Profile?.Category ?? resolution.Entry?.Category ?? FixtureCategory.Other).DisplayName(),
                addresses.Count > 0 ? string.Join(" ", addresses) : "",
                resolution.Profile?.Footprint ?? 0,
                electrical?.PowerW ?? specs?.PowerConsumptionW,
                electrical?.WeightKg ?? specs?.WeightKg,
                entity.WorldPosition,
                scene.FindLayer(entity.LayerId)?.Name ?? "",
                resolution.MatchKind == FixtureMatchKind.Substitute,
                peak));
        }
        rows.Sort((a, b) =>
        {
            int c = (a.FixtureId == 0).CompareTo(b.FixtureId == 0);
            if (c == 0) c = a.FixtureId.CompareTo(b.FixtureId);
            if (c == 0) c = string.CompareOrdinal(a.Name, b.Name);
            return c != 0 ? c : a.EntityId.CompareTo(b.EntityId);
        });
        return new LightPlotReport(scene.Name, rows);
    }

    /// <summary>Marks the light list as changed (called by the engine after every frame).</summary>
    internal void Invalidate() => _valid = false;

    private void Refresh()
    {
        if (_valid) return;
        _valid = true;
        var source = _engine.Lights;
        if (_lights.Length < source.Length)
        {
            _lights = new Photometry.SpotLight[Math.Max(source.Length, _lights.Length * 2)];
            _fixtures = new Guid[_lights.Length];
        }
        _count = 0;
        foreach (ref readonly var l in source)
        {
            if (!(l.PeakCandela > 0f)) continue;
            _fixtures[_count] = l.FixtureId;
            _lights[_count++] = Convert(l);
        }
    }

    /// <summary>The photometric description of a renderer light (open beam, frost widening it like the renderer does).</summary>
    internal static Photometry.SpotLight Convert(in RenderSpotLight l)
    {
        float beam = float.IsFinite(l.BeamAngle) && l.BeamAngle > 0f ? l.BeamAngle : 0.4f;
        float field = float.IsFinite(l.FieldAngle) && l.FieldAngle >= beam ? l.FieldAngle : beam * 1.5f;
        var distribution = BeamDistribution.Create(l.BeamType, beam, field);
        return new Photometry.SpotLight(l.Position, l.Direction, l.PeakCandela, distribution, LightColor.FromRec2020(l.Color));
    }
}

/// <summary>One fixture of the light plot.</summary>
/// <param name="EntityId">Scene entity.</param>
/// <param name="FixtureId">Console fixture id (0 = none).</param>
/// <param name="Name">Entity name.</param>
/// <param name="Type">Manufacturer and model (of the GDTF in use; see <paramref name="IsSubstitute"/>).</param>
/// <param name="Mode">DMX mode.</param>
/// <param name="Category">Library category (PROFILE, WASH …).</param>
/// <param name="Address">Patch, universe.address per DMX break ("" = unpatched).</param>
/// <param name="Footprint">DMX channels of the mode.</param>
/// <param name="PowerW">Power draw, W (null = unknown).</param>
/// <param name="WeightKg">Weight, kg (null = unknown).</param>
/// <param name="Position">World position, m.</param>
/// <param name="Layer">Layer name.</param>
/// <param name="IsSubstitute">True when a similar model stands in for a GDTF that is not available.</param>
/// <param name="PeakCandela">Brightest beam at the current state, cd.</param>
public sealed record LightPlotRow(Guid EntityId, int FixtureId, string Name, string Type, string Mode, string Category, string Address,
    int Footprint, double? PowerW, double? WeightKg, Vector3 Position, string Layer, bool IsSubstitute, float PeakCandela);

/// <summary>The light plot (fixture schedule) of a scene with totals; <see cref="ToCsv"/> and <see cref="ToText"/> render it.</summary>
public sealed class LightPlotReport
{
    internal LightPlotReport(string sceneName, IReadOnlyList<LightPlotRow> rows)
    {
        SceneName = sceneName;
        Rows = rows;
        TotalPowerW = rows.Sum(r => r.PowerW ?? 0);
        TotalWeightKg = rows.Sum(r => r.WeightKg ?? 0);
        UnknownPower = rows.Count(r => r.PowerW is null);
        UnknownWeight = rows.Count(r => r.WeightKg is null);
    }

    /// <summary>Name of the scene.</summary>
    public string SceneName { get; }

    /// <summary>Fixtures ordered by fixture id.</summary>
    public IReadOnlyList<LightPlotRow> Rows { get; }

    /// <summary>Sum of the known power draws, W.</summary>
    public double TotalPowerW { get; }

    /// <summary>Sum of the known weights, kg.</summary>
    public double TotalWeightKg { get; }

    /// <summary>Fixtures without a power value.</summary>
    public int UnknownPower { get; }

    /// <summary>Fixtures without a weight value.</summary>
    public int UnknownWeight { get; }

    /// <summary>Comma-separated values (invariant culture, quoted where needed) with a header row.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fixture ID,Name,Type,Mode,Category,Address,Channels,Power W,Weight kg,X m,Y m,Z m,Layer,Substitute");
        foreach (var r in Rows)
        {
            sb.AppendJoin(',', new[]
            {
                r.FixtureId == 0 ? "" : r.FixtureId.ToString(CultureInfo.InvariantCulture),
                Csv(r.Name), Csv(r.Type), Csv(r.Mode), Csv(r.Category), Csv(r.Address),
                r.Footprint.ToString(CultureInfo.InvariantCulture),
                r.PowerW?.ToString("0.#", CultureInfo.InvariantCulture) ?? "",
                r.WeightKg?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                r.Position.X.ToString("0.###", CultureInfo.InvariantCulture),
                r.Position.Y.ToString("0.###", CultureInfo.InvariantCulture),
                r.Position.Z.ToString("0.###", CultureInfo.InvariantCulture),
                Csv(r.Layer), r.IsSubstitute ? "yes" : "",
            });
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>A fixed-width text table for the terminal, with totals.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Light plot: {SceneName} ({Rows.Count} fixtures)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{"ID",5}  {"Type",-38} {"Mode",-16} {"Address",-12} {"Power",8} {"Weight",8}");
        foreach (var r in Rows)
        {
            string type = r.IsSubstitute ? r.Type + " *" : r.Type;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{(r.FixtureId == 0 ? "-" : r.FixtureId.ToString(CultureInfo.InvariantCulture)),5}  {Cut(type, 38),-38} {Cut(r.Mode, 16),-16} " +
                $"{Cut(r.Address.Length == 0 ? "-" : r.Address, 12),-12} {(r.PowerW is { } w ? $"{w:0} W" : "?"),8} {(r.WeightKg is { } kg ? $"{kg:0.#} kg" : "?"),8}");
        }
        string unknown = UnknownPower + UnknownWeight > 0 ? $" ({UnknownPower} without power, {UnknownWeight} without weight)" : "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Total: {TotalPowerW / 1000:0.##} kW, {TotalWeightKg:0.#} kg{unknown}");
        if (Rows.Any(r => r.IsSubstitute)) sb.AppendLine("* a similar model stands in for a GDTF that is not in the library");
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
