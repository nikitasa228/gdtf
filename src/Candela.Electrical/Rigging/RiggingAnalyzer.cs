using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical;

/// <summary>Tunable values of the rigging checks.</summary>
public sealed record RiggingOptions
{
    /// <summary>Weight of the clamp (hook clamp, half coupler) added per mounted fixture, in kg.</summary>
    public float ClampWeightKg { get; init; } = 0.5f;

    /// <summary>A fixture higher than this above the floor (m) must be attached to a structure.</summary>
    public float UnmountedHeightThresholdM { get; init; } = 0.5f;
}

/// <summary>One weight carried by a truss.</summary>
/// <param name="EntityId">Fixture or cable entity.</param>
/// <param name="Description">What it is ("#3 'Spot 3'", "cable 'D-1'").</param>
/// <param name="WeightKg">Weight in kg (fixture + clamp, or the cable part on the truss).</param>
/// <param name="WorldPosition">Where the weight hangs.</param>
public readonly record struct TrussLoadItem(Guid EntityId, string Description, double WeightKg, Vector3 WorldPosition);

/// <summary>Load on one truss/pipe.</summary>
public sealed record TrussLoad
{
    /// <summary>Truss entity.</summary>
    public required Guid TrussEntityId { get; init; }

    /// <summary>Truss name.</summary>
    public required string Name { get; init; }

    /// <summary>Kind of structure.</summary>
    public required TrussKind Kind { get; init; }

    /// <summary>Cross-section label (F34, …).</summary>
    public required string Profile { get; init; }

    /// <summary>Length in metres.</summary>
    public required float LengthM { get; init; }

    /// <summary>Own weight in kg (component value, else estimated from the profile).</summary>
    public required double OwnWeightKg { get; init; }

    /// <summary>True when <see cref="OwnWeightKg"/> is estimated from the profile table.</summary>
    public required bool OwnWeightEstimated { get; init; }

    /// <summary>Fixtures hanging on the truss.</summary>
    public required IReadOnlyList<Guid> Fixtures { get; init; }

    /// <summary>Sum of the known fixture weights in kg.</summary>
    public required double FixtureWeightKg { get; init; }

    /// <summary>Clamps (one per fixture) in kg.</summary>
    public required double ClampWeightKg { get; init; }

    /// <summary>Cable weight carried by the truss in kg.</summary>
    public required double CableWeightKg { get; init; }

    /// <summary>Fixtures whose weight is unknown (counted as 0 kg).</summary>
    public required IReadOnlyList<Guid> UnknownWeightFixtures { get; init; }

    /// <summary>Heaviest single item (fixture + clamp) in kg.</summary>
    public required double HeaviestPointLoadKg { get; init; }

    /// <summary>Maximum uniform load of the truss in kg (null = unknown).</summary>
    public float? MaxUniformLoadKg { get; init; }

    /// <summary>Maximum point load of the truss in kg (null = unknown).</summary>
    public float? MaxPointLoadKg { get; init; }

    /// <summary>Every weight on the truss.</summary>
    public required IReadOnlyList<TrussLoadItem> Items { get; init; }

    /// <summary>
    /// Largest bending moment the carried items cause, in kg·m (multiply by g for N·m), with the truss taken as a
    /// beam simply supported at both ends of its length (the span its rated loads refer to). The truss's own weight is
    /// not included: load tables give the load a truss carries in addition to itself.
    /// </summary>
    public double MaxBendingMomentKgM { get; init; }

    /// <summary>
    /// The single load at mid-span that bends the truss as much as the carried items do (4·M / L), in kg; compared
    /// with <see cref="MaxPointLoadKg"/>. Two heavy fixtures side by side near mid-span count almost double.
    /// </summary>
    public double EquivalentCentrePointLoadKg { get; init; }

    /// <summary>The uniformly distributed load that bends the truss as much as the carried items do (8·M / L), in kg.</summary>
    public double EquivalentUniformLoadKg { get; init; }

    /// <summary>Load carried (fixtures + clamps + cables) in kg, without the truss's own weight.</summary>
    public double LoadKg => FixtureWeightKg + ClampWeightKg + CableWeightKg;

    /// <summary>Load as a percentage of the maximum uniform load, or null.</summary>
    public double? UniformUtilisationPercent => MaxUniformLoadKg is { } max && max > 0 ? 100.0 * LoadKg / max : null;
}

/// <summary>Load on one rigging point (hoist, rope).</summary>
public sealed record RiggingPointLoad
{
    /// <summary>Rigging point entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Capacity in kg.</summary>
    public required float CapacityKg { get; init; }

    /// <summary>Load in kg.</summary>
    public required double LoadKg { get; init; }

    /// <summary>What makes up the load: carried entity and its share in kg.</summary>
    public required IReadOnlyList<(Guid EntityId, string Name, double Kg)> Shares { get; init; }

    /// <summary>Load as a percentage of the capacity, or null.</summary>
    public double? UtilisationPercent => CapacityKg > 0 ? 100.0 * LoadKg / CapacityKg : null;
}

/// <summary>Result of a rigging analysis.</summary>
/// <param name="Trusses">Truss loads in scene order.</param>
/// <param name="Points">Rigging point loads in scene order.</param>
/// <param name="Issues">Issues found.</param>
public sealed record RiggingSolution(IReadOnlyList<TrussLoad> Trusses, IReadOnlyList<RiggingPointLoad> Points, IReadOnlyList<Issue> Issues);

/// <summary>
/// Rigging checks: fixtures above the floor that are not attached to a structure, mounts that point at nothing,
/// truss load (fixture weights from specs/GDTF + a clamp per fixture + the cable weight along the truss) against the
/// truss's maximum uniform and point loads, and rigging point capacity against the share of the trusses it carries
/// (lever rule between neighbouring points along the truss). Besides the total and the heaviest single fixture, the
/// bending moment of all loads together is compared with the ratings (as the equivalent mid-span point load, or the
/// equivalent uniform load when no point rating is given), so several heavy fixtures close together are caught.
/// </summary>
/// <remarks>
/// A fixture counts as hanging on a truss when its <c>MountedOn</c> is the truss (or an entity under it), or — without
/// <c>MountedOn</c> — when it is a child of a truss entity. Cable segments are carried by a truss when their nodes are
/// anchored to it or to fixtures hanging on it (a segment with only one end on a truss puts half its weight on it).
/// Floors are <see cref="WallComponent"/>s of kind <see cref="WallKind.Floor"/>, taken as rectangles centred on their
/// entity origin; without a floor below, the floor is at Z = 0.
/// </remarks>
public sealed class RiggingAnalyzer
{
    private static readonly Dictionary<string, float> ProfileKgPerM = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F22"] = 1.5f,
        ["F23"] = 2.1f,
        ["F24"] = 2.8f,
        ["F31"] = 1.5f,
        ["F32"] = 2.6f,
        ["F33"] = 3.5f,
        ["F34"] = 4.5f,
        ["F42"] = 3.2f,
        ["F43"] = 4.6f,
        ["F44"] = 6.0f,
        ["F52"] = 4.5f,
        ["F53"] = 6.0f,
        ["F54"] = 7.5f,
    };

    private readonly IFixtureProfileProvider? _fixtures;

    /// <summary>Creates an analyzer.</summary>
    /// <param name="fixtures">Fixture data for weights (null: every weight is unknown).</param>
    /// <param name="catalog">Cable types for cable weights; null = <see cref="CableCatalog.Default"/>.</param>
    /// <param name="options">Check values; null = defaults.</param>
    public RiggingAnalyzer(IFixtureProfileProvider? fixtures, CableCatalog? catalog = null, RiggingOptions? options = null)
    {
        _fixtures = fixtures;
        Catalog = catalog ?? CableCatalog.Default;
        Options = options ?? new RiggingOptions();
    }

    /// <summary>Cable catalogue.</summary>
    public CableCatalog Catalog { get; }

    /// <summary>Check values.</summary>
    public RiggingOptions Options { get; }

    /// <summary>Estimated own weight of a truss per metre in kg/m from its profile label and kind.</summary>
    public static float EstimatedKgPerMetre(TrussComponent truss)
    {
        ArgumentNullException.ThrowIfNull(truss);
        var profile = (truss.Profile ?? "").Trim();
        foreach (var (key, kg) in ProfileKgPerM)
            if (profile.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return kg;
        return truss.Kind switch
        {
            TrussKind.Pipe => 3.6f,   // Ø48.3 × 3.2 mm steel scaffold tube
            TrussKind.Ladder => 2.6f,
            TrussKind.Tower => 6.0f,
            _ => 4.5f,
        };
    }

    /// <summary>Analyses the scene.</summary>
    public RiggingSolution Analyze(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var issues = new List<Issue>();
        var fixtures = new List<(Entity Entity, FixtureComponent Fixture)>();
        var trusses = new List<(Entity Entity, TrussComponent Truss)>();
        var points = new List<(Entity Entity, RiggingPointComponent Point)>();
        var floors = new List<(Matrix4x4 Inverse, float Top, float HalfW, float HalfL)>();
        var cables = new List<(Entity Entity, CableComponent Cable)>();
        foreach (var e in scene.All())
        {
            // One fixture / truss / rigging point per entity (the first component of each kind counts).
            bool fixtureSeen = false, trussSeen = false, pointSeen = false;
            foreach (var c in e.Components)
            {
                switch (c)
                {
                    case FixtureComponent f when !fixtureSeen: fixtures.Add((e, f)); fixtureSeen = true; break;
                    case TrussComponent t when !trussSeen: trusses.Add((e, t)); trussSeen = true; break;
                    case RiggingPointComponent p when !pointSeen: points.Add((e, p)); pointSeen = true; break;
                    case CableComponent cable: cables.Add((e, cable)); break;
                    case WallComponent { Kind: WallKind.Floor } w when Matrix4x4.Invert(e.WorldMatrix, out var inv):
                        floors.Add((inv, e.WorldPosition.Z, MathF.Abs(w.Width) / 2, MathF.Abs(w.Height) / 2));
                        break;
                }
            }
        }

        var trussLoads = trusses.ToDictionary(t => t.Entity.Id, _ => new List<TrussLoadItem>());
        var trussFixtures = trusses.ToDictionary(t => t.Entity.Id, _ => new List<Guid>());
        var unknownByTruss = trusses.ToDictionary(t => t.Entity.Id, _ => new List<Guid>());
        var fixtureCarrier = new Dictionary<Guid, Guid>();
        var weightByFixture = new Dictionary<Guid, double?>();
        var hungFromPoint = new Dictionary<Guid, List<Entity>>();

        foreach (var (e, f) in fixtures)
        {
            var profile = _fixtures is not null && !string.IsNullOrWhiteSpace(f.GdtfSpec) ? _fixtures.GetProfile(f.GdtfSpec, f.GdtfMode) : null;
            double? weight = profile?.WeightKg;
            weightByFixture[e.Id] = weight;
            string label = Label(e, f, profile);
            Entity? mount = null;
            if (f.MountedOn is { } mountId)
            {
                mount = scene.Find(mountId);
                if (mount is null)
                {
                    issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Rigging, IssueCodes.RiggingMountMissing,
                        $"{label} is mounted on an object that no longer exists ({mountId}).", e.Id));
                    continue;
                }
            }
            if (mount is not null && mount.Has<RiggingPointComponent>())
            {
                if (!hungFromPoint.TryGetValue(mount.Id, out var hung)) hungFromPoint[mount.Id] = hung = new List<Entity>();
                hung.Add(e);
            }
            var truss = TrussOf(mount ?? e.Parent);
            if (mount is null && truss is null)
            {
                float height = e.WorldPosition.Z - FloorLevel(floors, e.WorldPosition);
                if (height > Options.UnmountedHeightThresholdM)
                    issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Rigging, IssueCodes.RiggingNotMounted,
                        $"{label} is {Fmt.M(height)} above the floor but is not attached to a truss/pipe.", e.Id));
                continue;
            }
            if (weight is null)
                issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Rigging, IssueCodes.RiggingUnknownWeight,
                    $"The weight of {label} is unknown; it is counted as 0 kg in the load of '{(truss ?? mount)!.Name}'.", e.Id));
            if (truss is not null && trussLoads.TryGetValue(truss.Id, out var items))
            {
                fixtureCarrier[e.Id] = truss.Id;
                items.Add(new TrussLoadItem(e.Id, label, (weight ?? 0) + Options.ClampWeightKg, e.WorldPosition));
                trussFixtures[truss.Id].Add(e.Id);
                if (weight is null) unknownByTruss[truss.Id].Add(e.Id);
            }
        }

        // Cable weight carried by trusses.
        var cableKg = trusses.ToDictionary(t => t.Entity.Id, _ => 0.0);
        foreach (var (e, cable) in cables)
        {
            var type = Catalog.Find(cable.CableTypeId);
            if (type is null || type.WeightKgPerM <= 0) continue;
            var geometry = CableGeometry.Compute(scene, e, cable);
            var anchorOf = new Dictionary<Guid, Guid?>(geometry.Nodes.Count);
            foreach (var n in geometry.Nodes) anchorOf[n.Id] = n.AnchorResolved ? n.AnchorEntityId : null;
            string label = string.IsNullOrWhiteSpace(cable.Label) ? e.Name : cable.Label!;
            foreach (var s in geometry.Segments)
            {
                double kg = s.LengthM * geometry.SlackFactor * type.WeightKgPerM;
                if (kg <= 0) continue;
                var a = Carrier(scene, anchorOf.GetValueOrDefault(s.FromNode), fixtureCarrier);
                var b = Carrier(scene, anchorOf.GetValueOrDefault(s.ToNode), fixtureCarrier);
                if (a is { } ta && b is { } tb && ta == tb)
                    AddCable(ta, kg, (s.From + s.To) / 2);
                else
                {
                    if (a is { } ha) AddCable(ha, kg / 2, s.From);
                    if (b is { } hb) AddCable(hb, kg / 2, s.To);
                }

                void AddCable(Guid truss, double weight, Vector3 at)
                {
                    if (!trussLoads.TryGetValue(truss, out var list)) return;
                    list.Add(new TrussLoadItem(e.Id, $"cable '{label}'", weight, at));
                    cableKg[truss] += weight;
                }
            }
        }

        var trussResults = new List<TrussLoad>(trusses.Count);
        var trussById = new Dictionary<Guid, TrussLoad>();
        foreach (var (e, t) in trusses)
        {
            var items = trussLoads[e.Id];
            var fixtureIds = trussFixtures[e.Id];
            double fixtureKg = fixtureIds.Sum(id => weightByFixture[id] ?? 0);
            double clamps = fixtureIds.Count * Options.ClampWeightKg;
            bool estimated = !(t.WeightKg > 0 && float.IsFinite(t.WeightKg));
            float length = float.IsFinite(t.Length) ? MathF.Max(0f, t.Length) : 0f;
            double own = estimated ? EstimatedKgPerMetre(t) * length : t.WeightKg;
            var fixtureSet = fixtureIds.ToHashSet();
            var fixtureItems = items.Where(i => fixtureSet.Contains(i.EntityId)).ToList();
            var heaviest = fixtureItems.Count == 0 ? default : fixtureItems.MaxBy(i => i.WeightKg);
            double moment = MaxBendingMoment(e, length, items);
            double pointEq = length > 0 ? 4 * moment / length : 0;
            double uniformEq = 2 * pointEq;
            var load = new TrussLoad
            {
                TrussEntityId = e.Id,
                Name = e.Name,
                Kind = t.Kind,
                Profile = t.Profile ?? "",
                LengthM = length,
                OwnWeightKg = own,
                OwnWeightEstimated = estimated,
                Fixtures = fixtureIds,
                FixtureWeightKg = fixtureKg,
                ClampWeightKg = clamps,
                CableWeightKg = cableKg[e.Id],
                UnknownWeightFixtures = unknownByTruss[e.Id],
                HeaviestPointLoadKg = heaviest.WeightKg,
                MaxUniformLoadKg = t.MaxUniformLoadKg > 0 ? t.MaxUniformLoadKg : null,
                MaxPointLoadKg = t.MaxPointLoadKg > 0 ? t.MaxPointLoadKg : null,
                Items = items,
                MaxBendingMomentKgM = moment,
                EquivalentCentrePointLoadKg = pointEq,
                EquivalentUniformLoadKg = uniformEq,
            };
            trussResults.Add(load);
            trussById[e.Id] = load;

            string what = $"truss '{e.Name}' ({(string.IsNullOrWhiteSpace(t.Profile) ? t.Kind.ToString() : t.Profile)}, {Fmt.M(length)})";
            string heaviestFixtures = Fmt.List(fixtureItems.OrderByDescending(i => i.WeightKg).Select(i => $"{i.Description} {Fmt.Kg(i.WeightKg)}"), 4);
            bool uniformReported = false;
            if (load.MaxUniformLoadKg is { } maxUniform && load.LoadKg > maxUniform + 1e-6)
            {
                uniformReported = true;
                issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Rigging, IssueCodes.RiggingTrussOverload,
                    $"The {what} is overloaded: it carries {Fmt.Kg(load.LoadKg)} ({Fmt.Count(fixtureIds.Count, "fixture", "fixtures")} {Fmt.Kg(fixtureKg)}, clamps {Fmt.Kg(clamps)}, cables {Fmt.Kg(load.CableWeightKg)}) but its maximum uniform load is {Fmt.Kg(maxUniform)}. Heaviest: {heaviestFixtures}.",
                    [e.Id, .. fixtureIds]));
            }
            if (load.MaxPointLoadKg is { } maxPoint)
            {
                if (heaviest.WeightKg > maxPoint + 1e-6)
                    issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Rigging, IssueCodes.RiggingTrussPointOverload,
                        $"{heaviest.Description} weighs {Fmt.Kg(heaviest.WeightKg)} with its clamp, more than the maximum point load of the {what} ({Fmt.Kg(maxPoint)}).",
                        e.Id, heaviest.EntityId));
                else if (pointEq > maxPoint + 1e-6)
                    issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Rigging, IssueCodes.RiggingTrussPointOverload,
                        $"The {what} is overloaded: its loads bend it as much as {Fmt.Kg(pointEq)} hung at mid-span, more than its maximum point load of {Fmt.Kg(maxPoint)} ({heaviestFixtures}).",
                        [e.Id, .. fixtureIds]));
            }
            else if (!uniformReported && load.MaxUniformLoadKg is { } maxUdl && uniformEq > maxUdl + 1e-6)
            {
                // Without a point-load rating, concentrated loads are judged by the uniform load that bends the truss
                // as much (a load table's uniform rating is a bending limit for all but the shortest spans).
                issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Rigging, IssueCodes.RiggingTrussOverload,
                    $"The {what} is overloaded: it carries {Fmt.Kg(load.LoadKg)}, but concentrated near mid-span it bends the truss like {Fmt.Kg(uniformEq)} spread evenly, more than its maximum uniform load of {Fmt.Kg(maxUdl)} ({heaviestFixtures}).",
                    [e.Id, .. fixtureIds]));
            }
        }

        var pointResults = AnalyzePoints(scene, points, trusses, trussById, weightByFixture, fixtures, hungFromPoint, issues);
        return new RiggingSolution(trussResults, pointResults, issues);
    }

    private List<RiggingPointLoad> AnalyzePoints(Scene scene, List<(Entity Entity, RiggingPointComponent Point)> points,
        List<(Entity Entity, TrussComponent Truss)> trusses, Dictionary<Guid, TrussLoad> trussById, Dictionary<Guid, double?> weightByFixture,
        List<(Entity Entity, FixtureComponent Fixture)> fixtures, Dictionary<Guid, List<Entity>> hungFromPoint, List<Issue> issues)
    {
        // Which points carry each truss, in scene order.
        var supports = new Dictionary<Guid, List<Entity>>();
        foreach (var (pe, p) in points)
            foreach (var id in p.Carries.Distinct())
                if (trussById.ContainsKey(id))
                {
                    if (!supports.TryGetValue(id, out var list)) supports[id] = list = new List<Entity>();
                    list.Add(pe);
                }

        // Reaction of every point for every truss it carries.
        var shares = new Dictionary<(Guid Point, Guid Truss), double>();
        foreach (var (te, t) in trusses)
        {
            if (!supports.TryGetValue(te.Id, out var sup)) continue;
            var load = trussById[te.Id];
            var origin = te.WorldPosition;
            var axis = Vector3.TransformNormal(Vector3.UnitX, te.WorldMatrix);
            axis = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitX;
            var xs = sup.Select(p => (Entity: p, X: Vector3.Dot(p.WorldPosition - origin, axis))).OrderBy(p => p.X).ToList();
            var reaction = new double[xs.Count];
            foreach (var item in load.Items) Distribute(Vector3.Dot(item.WorldPosition - origin, axis), item.WeightKg);
            int pieces = Math.Max(2, (int)MathF.Ceiling(load.LengthM / 0.25f));
            for (int i = 0; i < pieces; i++) Distribute((i + 0.5f) * load.LengthM / pieces, load.OwnWeightKg / pieces);
            for (int i = 0; i < xs.Count; i++)
                shares[(xs[i].Entity.Id, te.Id)] = shares.GetValueOrDefault((xs[i].Entity.Id, te.Id)) + reaction[i];

            void Distribute(float x, double w)
            {
                if (w <= 0) return;
                int n = xs.Count;
                if (n == 1)
                {
                    reaction[0] += w;
                    return;
                }
                int a, b;
                if (x <= xs[0].X) (a, b) = (0, 1);
                else if (x >= xs[n - 1].X) (a, b) = (n - 2, n - 1);
                else
                {
                    a = 0;
                    while (a < n - 2 && x > xs[a + 1].X) a++;
                    b = a + 1;
                }
                float span = xs[b].X - xs[a].X;
                if (span < 1e-3f)
                {
                    reaction[a] += w / 2;
                    reaction[b] += w / 2;
                    return;
                }
                double ra = w * (xs[b].X - x) / span;
                double rb = w - ra;
                reaction[a] += Math.Max(0, ra);
                reaction[b] += Math.Max(0, rb);
            }
        }

        var fixtureById = fixtures.ToDictionary(f => f.Entity.Id, f => f.Fixture);
        var results = new List<RiggingPointLoad>(points.Count);
        foreach (var (pe, p) in points)
        {
            var parts = new List<(Guid, string, double)>();
            foreach (var id in p.Carries.Distinct())
            {
                var target = scene.Find(id);
                if (target is null)
                {
                    issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Rigging, IssueCodes.RiggingPointMissingTarget,
                        $"Rigging point '{pe.Name}' carries an object that no longer exists ({id}).", pe.Id));
                    continue;
                }
                if (trussById.ContainsKey(id))
                    parts.Add((id, $"truss '{target.Name}'", shares.GetValueOrDefault((pe.Id, id))));
                else if (fixtureById.ContainsKey(id))
                    parts.Add((id, $"'{target.Name}'", (weightByFixture.GetValueOrDefault(id) ?? 0) + Options.ClampWeightKg));
            }
            // Fixtures hung straight from the point.
            if (hungFromPoint.TryGetValue(pe.Id, out var hung))
                foreach (var fe in hung)
                    if (!p.Carries.Contains(fe.Id))
                        parts.Add((fe.Id, $"'{fe.Name}'", (weightByFixture.GetValueOrDefault(fe.Id) ?? 0) + Options.ClampWeightKg));

            double total = parts.Sum(x => x.Item3);
            var result = new RiggingPointLoad { EntityId = pe.Id, Name = pe.Name, CapacityKg = p.CapacityKg, LoadKg = total, Shares = parts };
            results.Add(result);
            if (p.CapacityKg > 0 && total > p.CapacityKg + 1e-6)
                issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Rigging, IssueCodes.RiggingPointOverload,
                    $"Rigging point '{pe.Name}' carries {Fmt.Kg(total)} ({Fmt.List(parts.Select(x => $"{x.Item2} {Fmt.Kg(x.Item3)}"))}) but is rated {Fmt.Kg(p.CapacityKg)}.",
                    [pe.Id, .. parts.Select(x => x.Item1)]));
        }
        return results;
    }

    /// <summary>The entity or its nearest ancestor that is a truss, or null.</summary>
    /// <summary>
    /// Largest bending moment (kg·m) of the items on a truss of the given length, taken as a beam simply supported at
    /// both ends; item positions are projected on the truss axis (local X) and clamped to the span. O(n log n).
    /// </summary>
    private static double MaxBendingMoment(Entity truss, float length, IReadOnlyList<TrussLoadItem> items)
    {
        if (!(length > 0) || items.Count == 0) return 0;
        var origin = truss.WorldPosition;
        var axis = Vector3.TransformNormal(Vector3.UnitX, truss.WorldMatrix);
        axis = axis.LengthSquared() > 1e-12f && float.IsFinite(axis.X + axis.Y + axis.Z) ? Vector3.Normalize(axis) : Vector3.UnitX;
        var loads = new (double X, double W)[items.Count];
        int n = 0;
        foreach (var item in items)
        {
            float x = Vector3.Dot(item.WorldPosition - origin, axis);
            if (!float.IsFinite(x) || !(item.WeightKg > 0) || !double.IsFinite(item.WeightKg)) continue;
            loads[n++] = (Math.Clamp(x, 0f, length), item.WeightKg);
        }
        if (n == 0) return 0;
        Array.Sort(loads, 0, n, Comparer<(double X, double W)>.Create((a, b) => a.X.CompareTo(b.X)));
        double reactionA = 0;
        for (int i = 0; i < n; i++) reactionA += loads[i].W * (length - loads[i].X) / length;
        double max = 0, leftW = 0, leftWx = 0;
        for (int i = 0; i < n; i++)
        {
            double x = loads[i].X;
            max = Math.Max(max, reactionA * x - (leftW * x - leftWx));
            leftW += loads[i].W;
            leftWx += loads[i].W * x;
        }
        return max;
    }

    private static Entity? TrussOf(Entity? entity)
    {
        for (var e = entity; e is not null; e = e.Parent)
            if (e.Has<TrussComponent>()) return e;
        return null;
    }

    private static Guid? Carrier(Scene scene, Guid? anchor, Dictionary<Guid, Guid> fixtureCarrier)
    {
        if (anchor is not { } id) return null;
        if (fixtureCarrier.TryGetValue(id, out var truss)) return truss;
        return TrussOf(scene.Find(id))?.Id;
    }

    private static float FloorLevel(List<(Matrix4x4 Inverse, float Top, float HalfW, float HalfL)> floors, Vector3 p)
    {
        float level = float.NegativeInfinity;
        foreach (var (inv, top, hw, hl) in floors)
        {
            if (top > p.Z + 0.01f) continue;
            var local = Vector3.Transform(p, inv);
            if (MathF.Abs(local.X) <= hw && MathF.Abs(local.Y) <= hl) level = MathF.Max(level, top);
        }
        return float.IsFinite(level) ? level : 0f;
    }

    private static string Label(Entity e, FixtureComponent f, FixtureProfile? profile)
    {
        string id = f.FixtureId > 0 ? $"#{f.FixtureId} '{e.Name}'" : $"'{e.Name}'";
        return profile is null ? id : $"{id} ({profile.DisplayName})";
    }
}
