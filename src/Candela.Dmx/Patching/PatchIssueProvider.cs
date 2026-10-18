using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
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

    /// <inheritdoc />
    public IEnumerable<Issue> Analyze(Scene scene) => Analyze(Patch.Build(scene, _profiles));

    /// <summary>Issues of an already built patch.</summary>
    public static IReadOnlyList<Issue> Analyze(Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var issues = new List<Issue>();
        var valid = new List<(PatchRange Range, PatchEntry Entry)>();
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
            else if (res.MatchKind is FixtureMatchKind.Substitute)
            {
                issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Patch, "patch.substitute-gdtf",
                    $"{e.Label}: '{e.Fixture.GdtfSpec}' is not in the library; '{res.Entry?.FileName}' is used as a substitute.", id));
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
                if (r.End > 512)
                {
                    issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Patch, "patch.out-of-range",
                        $"{e.Label}: {r.Length} channels from {r.Universe}.{r.Start:D3} run past slot 512 of universe {r.Universe} " +
                        $"(would end at {r.End}).", id));
                }
                valid.Add((r with { End = Math.Min(r.End, 512) }, e));
            }
        }
        FindOverlaps(valid, issues);
        return issues;
    }

    private static void FindOverlaps(List<(PatchRange Range, PatchEntry Entry)> ranges, List<Issue> issues)
    {
        ranges.Sort((a, b) => a.Range.Universe != b.Range.Universe
            ? a.Range.Universe.CompareTo(b.Range.Universe)
            : a.Range.Start != b.Range.Start ? a.Range.Start.CompareTo(b.Range.Start) : a.Range.End.CompareTo(b.Range.End));
        var active = new List<(PatchRange Range, PatchEntry Entry)>();
        var reported = new HashSet<(Guid, Guid)>();
        int count = 0;
        int universe = int.MinValue;
        foreach (var cur in ranges)
        {
            if (cur.Range.Universe != universe)
            {
                active.Clear();
                universe = cur.Range.Universe;
            }
            active.RemoveAll(a => a.Range.End < cur.Range.Start);
            foreach (var other in active)
            {
                if (ReferenceEquals(other.Entry, cur.Entry)) continue;
                var key = Order(other.Entry.Entity.Id, cur.Entry.Entity.Id);
                if (!reported.Add(key)) continue;
                if (++count > MaxOverlapIssues) return;
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
    }

    private static (Guid, Guid) Order(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
}
