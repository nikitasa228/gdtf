using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Dmx;

/// <summary>DMX slots one break of a fixture occupies in a universe (1-based, inclusive).</summary>
/// <param name="Universe">1-based universe.</param>
/// <param name="Start">First slot (the break's start address).</param>
/// <param name="End">Last slot (start + footprint − 1; may exceed 512 when the patch is wrong).</param>
/// <param name="Break">GDTF DMX break.</param>
public readonly record struct PatchRange(int Universe, int Start, int End, int Break)
{
    /// <summary>Number of slots.</summary>
    public int Length => End - Start + 1;

    /// <summary>True when the two ranges share at least one slot of the same universe.</summary>
    public bool Overlaps(PatchRange other) => Universe == other.Universe && Start <= other.End && other.Start <= End;

    /// <inheritdoc />
    public override string ToString() => $"{Universe}.{Start:D3}–{Universe}.{End:D3}";
}

/// <summary>One fixture of the scene in the patch: its profile (if it could be resolved) and the DMX ranges it occupies.</summary>
public sealed class PatchEntry
{
    internal PatchEntry(Entity entity, FixtureComponent fixture, ProfileResolution resolution, IReadOnlyList<PatchRange> ranges,
        IReadOnlyList<int> missingBreaks)
    {
        Entity = entity;
        Fixture = fixture;
        Resolution = resolution;
        Ranges = ranges;
        MissingBreaks = missingBreaks;
    }

    /// <summary>The fixture's entity.</summary>
    public Entity Entity { get; }

    /// <summary>The fixture component.</summary>
    public FixtureComponent Fixture { get; }

    /// <summary>Profile lookup result.</summary>
    public ProfileResolution Resolution { get; }

    /// <summary>The compiled profile, or null.</summary>
    public FixtureProfile? Profile => Resolution.Profile;

    /// <summary>Occupied DMX ranges, one per patched break with a non-zero footprint.</summary>
    public IReadOnlyList<PatchRange> Ranges { get; }

    /// <summary>GDTF breaks of the mode that have no address.</summary>
    public IReadOnlyList<int> MissingBreaks { get; }

    /// <summary>"Fixture 101 'Spot 1'" — how issues name the fixture.</summary>
    public string Label => Patch.Label(Entity, Fixture);

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The DMX patch of a scene: every <see cref="FixtureComponent"/> with its resolved profile and the universe slots each
/// DMX break occupies (address from <see cref="FixtureComponent.Addresses"/>, footprint from the profile).
/// </summary>
public sealed class Patch
{
    private readonly Dictionary<Guid, PatchEntry> _byEntity;
    private readonly Dictionary<int, PatchEntry[][]> _slotIndex = new(); // per universe, built on first use: fixtures per slot

    private Patch(List<PatchEntry> entries)
    {
        Entries = entries;
        _byEntity = new Dictionary<Guid, PatchEntry>(entries.Count);
        foreach (var e in entries) _byEntity.TryAdd(e.Entity.Id, e);
        Universes = entries.SelectMany(e => e.Ranges).Select(r => r.Universe).Where(u => u >= 1).Distinct().Order().ToArray();
    }

    /// <summary>All fixtures in scene order.</summary>
    public IReadOnlyList<PatchEntry> Entries { get; }

    /// <summary>Universes (≥ 1) that at least one fixture uses, ascending.</summary>
    public IReadOnlyList<int> Universes { get; }

    /// <summary>Builds the patch of <paramref name="scene"/>, resolving profiles through <paramref name="profiles"/>.</summary>
    public static Patch Build(Scene scene, FixtureProfileCache profiles)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(profiles);
        var list = new List<PatchEntry>();
        foreach (var (entity, fixture) in scene.Query<FixtureComponent>())
            list.Add(CreateEntry(entity, fixture, profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode)));
        return new Patch(list);
    }

    /// <summary>Entry of an entity, or null.</summary>
    public PatchEntry? Find(Guid entityId) => _byEntity.GetValueOrDefault(entityId);

    /// <summary>
    /// Fixtures that occupy slot <paramref name="address"/> (1 … 512) of <paramref name="universe"/>, in scene order. The
    /// first query of a universe indexes its slots once, so drawing a whole patch view costs O(slots + fixtures).
    /// </summary>
    public IReadOnlyList<PatchEntry> At(int universe, int address)
    {
        if (address is < 1 or > 512) return Array.Empty<PatchEntry>();
        lock (_slotIndex)
        {
            if (!_slotIndex.TryGetValue(universe, out var slots)) _slotIndex[universe] = slots = BuildSlotIndex(universe);
            return slots[address - 1];
        }
    }

    private PatchEntry[][] BuildSlotIndex(int universe)
    {
        var lists = new List<PatchEntry>?[512];
        foreach (var e in Entries)
            foreach (var r in e.Ranges)
            {
                if (r.Universe != universe) continue;
                for (int a = Math.Max(r.Start, 1); a <= Math.Min(r.End, 512); a++)
                {
                    var list = lists[a - 1] ??= new List<PatchEntry>(1);
                    if (list.Count == 0 || !ReferenceEquals(list[^1], e)) list.Add(e);
                }
            }
        var slots = new PatchEntry[512][];
        for (int i = 0; i < 512; i++) slots[i] = lists[i]?.ToArray() ?? Array.Empty<PatchEntry>();
        return slots;
    }

    internal static PatchEntry CreateEntry(Entity entity, FixtureComponent fixture, ProfileResolution resolution)
    {
        var ranges = new List<PatchRange>();
        var missing = new List<int>();
        if (resolution.Profile is { } profile)
        {
            foreach (var b in profile.Breaks)
            {
                DmxAddress? address = AddressFor(fixture, b.Break, profile.Breaks.Count);
                if (address is not { } a)
                {
                    if (fixture.Addresses.Count > 0) missing.Add(b.Break);
                    continue;
                }
                ranges.Add(new PatchRange(a.Universe, a.Address, a.Address + b.Footprint - 1, b.Break));
            }
        }
        return new PatchEntry(entity, fixture, resolution, ranges, missing);
    }

    /// <summary>
    /// The address of GDTF break <paramref name="dmxBreak"/>: the address with that break number; a single-break mode also
    /// accepts the fixture's only address whatever break number it carries (MVR files often write break 0 or 1).
    /// </summary>
    internal static DmxAddress? AddressFor(FixtureComponent fixture, int dmxBreak, int breakCount)
    {
        foreach (var a in fixture.Addresses)
            if (a.Break == dmxBreak) return a;
        if (breakCount == 1 && fixture.Addresses.Count == 1) return fixture.Addresses[0];
        return null;
    }

    /// <summary>"Fixture 101 'Spot 1'" (or "Fixture 'Spot 1'" without a fixture id).</summary>
    public static string Label(Entity entity, FixtureComponent fixture)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fixture);
        return fixture.FixtureId != 0 ? $"Fixture {fixture.FixtureId} '{entity.Name}'" : $"Fixture '{entity.Name}'";
    }
}
