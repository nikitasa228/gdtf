using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Dmx;

/// <summary>
/// Patch checks for the issues panel: unknown GDTF files and modes, invalid universes and addresses, footprints that run
/// past slot 512, breaks without an address and overlapping addresses. Every message names the fixtures by fixture id
/// and name; clicking an issue selects them.
/// </summary>
/// <remarks>Overlaps are found with a sort-and-sweep per universe (O(n log n) plus the overlaps found).</remarks>
public sealed class PatchIssueProvider : IIssueProvider
{
    /// <summary>Most overlap issues reported per analysis (a fully double-patched rig would otherwise flood the panel).</summary>
    public const int MaxOverlapIssues = 500;

    private readonly FixtureProfileCache _profiles;

    /// <summary>Creates the provider; profiles (footprints) come from <paramref name="profiles"/>.</summary>
    public PatchIssueProvider(FixtureProfileCache profiles) => _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));

    /// <inheritdoc />
    public string Name => "DMX patch";

    /// <summary>
    /// Fixtures whose GDTF file is a stand-in chosen by an import (see <see cref="Patch.Build"/>); null when only stand-ins
    /// resolved from MVR stub names are known.
    /// </summary>
    public Func<Entity, FixtureComponent, FixtureSubstitution?>? Substitutions { get; init; }

    /// <inheritdoc />
    public IEnumerable<Issue> Analyze(Scene scene) => Analyze(Patch.Build(scene, _profiles, Substitutions));

    /// <summary>Issues of an already built patch.</summary>
    public static IReadOnlyList<Issue> Analyze(Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var issues = new List<Issue>();
        var valid = new List<Slots>();
        var substitutes = new Dictionary<(string, string, string), (FixtureSubstitution Substitution, FixtureProfile Profile, List<Guid> Entities)>();
        foreach (var e in patch.Entries)
        {
            var id = e.Entity.Id;
            var res = e.Resolution;
            if (res.Profile is null)
            {
                var (code, text) = res.ErrorKind switch
                {
                    ProfileErrorKind.UnknownMode => ("patch.unknown-mode",
                        $"{e.Label}: DMX mode '{e.Fixture.GdtfMode}' does not exist in '{e.Fixture.GdtfSpec}'. {res.Error}"),
                    ProfileErrorKind.UnreadableFile => ("patch.unreadable-gdtf", $"{e.Label}: {res.Error}"),
                    _ => ("patch.unknown-gdtf", string.IsNullOrWhiteSpace(e.Fixture.GdtfSpec)
                        ? $"{e.Label} has no GDTF file."
                        : $"{e.Label}: GDTF file '{e.Fixture.GdtfSpec}' is not in the fixture library."),
                };
                issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Patch, code, text, id));
            }
            else if (e.Substitution is { } sub)
            {
                var key = (sub.RequestedSpec, sub.StandIn, res.Profile.Mode.Name);
                if (!substitutes.TryGetValue(key, out var group)) substitutes[key] = group = (sub, res.Profile, new List<Guid>());
                group.Entities.Add(id);
            }
            else if (res.MatchKind is FixtureMatchKind.SameModel or FixtureMatchKind.ModelOnly)
            {
                issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Patch, "patch.matched-gdtf",
                    $"{e.Label}: '{e.Fixture.GdtfSpec}' resolved to '{res.Entry?.FileName}'.", id));
            }

            foreach (var a in e.Fixture.Addresses)
            {
                if (a.Universe < 1)
                    issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Patch, "patch.invalid-universe",
                        $"{e.Label}: universe {a.Universe} is invalid (universes start at 1).", id));
                if (a.Address is < 1 or > 512)
                    issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Patch, "patch.invalid-address",
                        $"{e.Label}: address {a.Address} is outside 1–512.", id));
            }
            if (res.Profile is { } profile && e.Fixture.Addresses.Count == 0 && profile.Footprint > 0)
                issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Patch, "patch.unpatched", $"{e.Label} is not patched.", id));
            foreach (int b in e.MissingBreaks)
                issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Patch, "patch.missing-break",
                    $"{e.Label}: DMX break {b} of mode '{res.Profile?.Mode.Name}' has no address.", id));

            foreach (var r in e.Ranges)
            {
                if (r.Universe < 1 || r.Start is < 1 or > 512) continue;
                // a stand-in wider than its type's patch spacing: the real model fits the spacing, so slots beyond it
                // are the stand-in's, not the patch's
                int realEnd = e.Substitution?.PatchSpacing is { } spacing && spacing < r.Length ? r.Start + spacing - 1 : r.End;
                if (r.End > 512)
                {
                    issues.Add(realEnd <= 512
                        ? new Issue(IssueSeverity.Warning, IssueCategory.Patch, "patch.out-of-range",
                            $"{e.Label}: the stand-in '{e.Substitution!.StandIn}' in mode '{res.Profile?.Mode.Name}' uses {r.Length} channels from " +
                            $"{r.Universe}.{r.Start:D3}, past slot 512; '{e.Substitution.RequestedSpec}' fixtures are patched " +
                            $"{e.Substitution.PatchSpacing} channels apart, so the real model fits — not a patch error until its GDTF says otherwise.", id)
                        : new Issue(IssueSeverity.Error, IssueCategory.Patch, "patch.out-of-range",
                            $"{e.Label}: {r.Length} channels from {r.Universe}.{r.Start:D3} run past slot 512 of universe {r.Universe} " +
                            $"(would end at {r.End}).", id));
                }
                valid.Add(new Slots(r with { End = Math.Min(r.End, 512) }, Math.Min(realEnd, 512), e));
            }
        }
        foreach (var (_, (sub, profile, entities)) in substitutes)
        {
            string fit = sub.PatchSpacing is { } spacing
                ? $" (the mode that best fits their {spacing}-channel patch spacing{(string.Equals(profile.Mode.Name, sub.RequestedMode, StringComparison.OrdinalIgnoreCase) ? "" : $"; the scene asks for '{sub.RequestedMode}'")})"
                : string.IsNullOrWhiteSpace(sub.RequestedMode) || string.Equals(profile.Mode.Name, sub.RequestedMode, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $" (the scene asks for '{sub.RequestedMode}')";
            issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Patch, "patch.substitute-gdtf",
                $"{entities.Count} fixture(s) of '{sub.RequestedSpec}' use the stand-in '{sub.StandIn}' ({profile.FixtureType.Manufacturer} " +
                $"{profile.FixtureType.Name}, mode '{profile.Mode.Name}', {profile.Breaks.FirstOrDefault().Footprint} channels{fit}): the library has no GDTF " +
                "of that model, so its body, beams and DMX layout are the stand-in's until the real GDTF is added.",
                entities.ToArray()));
        }
        FindOverlaps(valid, issues);
        return issues;
    }

    /// <summary>A patched range; <see cref="RealEnd"/> is where it ends when a stand-in is limited to its type's patch spacing.</summary>
    private readonly record struct Slots(PatchRange Range, int RealEnd, PatchEntry Entry);

    private static void FindOverlaps(List<Slots> ranges, List<Issue> issues)
    {
        ranges.Sort((a, b) => a.Range.Universe != b.Range.Universe
            ? a.Range.Universe.CompareTo(b.Range.Universe)
            : a.Range.Start != b.Range.Start ? a.Range.Start.CompareTo(b.Range.Start) : a.Range.End.CompareTo(b.Range.End));
        var active = new List<Slots>();
        var reported = new HashSet<(Guid, Guid)>();
        var standInOnly = new Dictionary<string, (FixtureSubstitution Substitution, int Count, HashSet<Guid> Entities)>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        int universe = int.MinValue;
        foreach (var cur in ranges)
        {
            if (cur.Range.Universe != universe)
            {
                active.Clear();
                universe = cur.Range.Universe;
            }
            if (count > MaxOverlapIssues) break;
            active.RemoveAll(a => a.Range.End < cur.Range.Start);
            foreach (var other in active)
            {
                if (ReferenceEquals(other.Entry, cur.Entry))
                {
                    // two DMX breaks of one fixture patched over each other
                    if (other.Range.Break == cur.Range.Break || ++count > MaxOverlapIssues) continue;
                    int a = Math.Max(other.Range.Start, cur.Range.Start), b = Math.Min(other.Range.End, cur.Range.End);
                    issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Patch, "patch.overlap",
                        $"{cur.Entry.Label}: DMX break {other.Range.Break} ({other.Range}) overlaps its break {cur.Range.Break} ({cur.Range}) on slots " +
                        $"{universe}.{a:D3}–{universe}.{b:D3}.", cur.Entry.Entity.Id));
                    continue;
                }
                var key = Order(other.Entry.Entity.Id, cur.Entry.Entity.Id);
                if (!reported.Add(key)) continue;
                if ((other.Entry.Substitution is not null || cur.Entry.Substitution is not null) && other.Range.Start != cur.Range.Start &&
                    !(other.Range.Start <= cur.RealEnd && cur.Range.Start <= other.RealEnd))
                {
                    // only the stand-in's footprint overlaps: one warning per stand-in type, below
                    var sub = (cur.Entry.Substitution ?? other.Entry.Substitution)!;
                    if (!standInOnly.TryGetValue(sub.RequestedSpec, out var group)) group = (sub, 0, new HashSet<Guid>());
                    group.Entities.Add(other.Entry.Entity.Id);
                    group.Entities.Add(cur.Entry.Entity.Id);
                    standInOnly[sub.RequestedSpec] = (group.Substitution, group.Count + 1, group.Entities);
                    continue;
                }
                if (++count > MaxOverlapIssues) break;
                int from = Math.Max(other.Range.Start, cur.Range.Start), to = Math.Min(other.Range.End, cur.Range.End);
                bool identical = other.Range.Start == cur.Range.Start && other.Range.End == cur.Range.End &&
                    ReferenceEquals(other.Entry.Profile, cur.Entry.Profile);
                issues.Add(identical
                    ? new Issue(IssueSeverity.Warning, IssueCategory.Patch, "patch.shared-address",
                        $"{other.Entry.Label} and {cur.Entry.Label} share address {universe}.{cur.Range.Start:D3} (same fixture type and " +
                        "mode: they will always show the same state).", other.Entry.Entity.Id, cur.Entry.Entity.Id)
                    : new Issue(IssueSeverity.Error, IssueCategory.Patch, "patch.overlap",
                        $"{other.Entry.Label} ({other.Range}) overlaps {cur.Entry.Label} ({cur.Range}) on slots " +
                        $"{universe}.{from:D3}–{universe}.{to:D3}.", other.Entry.Entity.Id, cur.Entry.Entity.Id));
            }
            active.Add(cur);
        }
        foreach (var (spec, (sub, n, entities)) in standInOnly)
        {
            issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Patch, "patch.overlap",
                $"{n} overlap(s) between fixtures of '{spec}' come only from the stand-in '{sub.StandIn}' being wider than their " +
                $"{sub.PatchSpacing}-channel patch spacing: the real model fits the patch — not a patch error until its GDTF says otherwise.",
                entities.ToArray()));
        }
    }

    private static (Guid, Guid) Order(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
}
