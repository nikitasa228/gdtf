using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>
/// Patches a fixture placed from the library to the first free DMX addresses: every DMX break of its mode gets the
/// lowest address, at or after the previous break, where its whole footprint fits into one universe without overlapping
/// a channel another fixture uses (universe 1, address 1 first). Fixtures whose type cannot be resolved occupy only the
/// channels their patch names. The scan is linear in the number of patched ranges.
/// </summary>
internal static class AutoPatch
{
    /// <summary>Channels per universe.</summary>
    public const int UniverseSize = 512;

    /// <summary>Highest universe that is searched (the sACN limit).</summary>
    public const int MaxUniverse = 63999;

    /// <summary>
    /// The addresses for a new fixture with <paramref name="breaks"/> (footprints in break order) given the ranges in use;
    /// an empty list for a mode without channels, null when a break does not fit into a universe (more than 512 channels)
    /// or no space is left.
    /// </summary>
    public static IReadOnlyList<DmxAddress>? FindFree(IEnumerable<PatchRange> used, IReadOnlyList<DmxBreakLayout> breaks)
    {
        ArgumentNullException.ThrowIfNull(used);
        ArgumentNullException.ThrowIfNull(breaks);
        var result = new List<DmxAddress>(breaks.Count);
        if (breaks.Count == 0)
            return result;
        foreach (var b in breaks)
        {
            if (b.Footprint > UniverseSize)
                return null;
        }

        // Occupied intervals as absolute channels, sorted and merged.
        var intervals = new List<(long Start, long End)>();
        foreach (var r in used)
        {
            if (r.Universe < 1 || r.Start < 1 || r.End < r.Start)
                continue;
            long start = (long)(r.Universe - 1) * UniverseSize + Math.Min(r.Start, UniverseSize);
            long end = (long)(r.Universe - 1) * UniverseSize + Math.Min(r.End, UniverseSize);
            intervals.Add((start, end));
        }

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>(intervals.Count);
        foreach (var iv in intervals)
        {
            if (merged.Count > 0 && iv.Start <= merged[^1].End + 1)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, iv.End));
            else
                merged.Add(iv);
        }

        long position = 1; // absolute channel (universe 1, address 1)
        int cursor = 0;    // first merged interval that may still lie after `position`
        foreach (var b in breaks)
        {
            if (b.Footprint <= 0)
                continue;
            long? found = FindGap(merged, ref cursor, position, b.Footprint);
            if (found is not { } start)
                return null;
            int universe = (int)((start - 1) / UniverseSize) + 1;
            int address = (int)((start - 1) % UniverseSize) + 1;
            result.Add(new DmxAddress(universe, address, b.Break));
            // The next break starts after this one, and this one's channels are taken.
            position = start + b.Footprint;
            InsertTaken(merged, ref cursor, start, start + b.Footprint - 1);
        }

        return result;
    }

    /// <summary>The ranges every patched fixture of <paramref name="scene"/> occupies (resolved footprints).</summary>
    public static IReadOnlyList<PatchRange> UsedRanges(SceneModel scene, FixtureProfileCache profiles)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(profiles);
        var patch = Patch.Build(scene, profiles);
        var ranges = new List<PatchRange>();
        foreach (var entry in patch.Entries)
            ranges.AddRange(entry.Ranges);
        return ranges;
    }

    /// <summary>The next fixture id: one more than the highest id in <paramref name="scene"/> (1 for the first).</summary>
    public static int NextFixtureId(SceneModel scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        int max = 0;
        foreach (var (_, fixture) in scene.Query<FixtureComponent>())
            max = Math.Max(max, fixture.FixtureId);
        return max >= int.MaxValue - 1 ? 0 : max + 1;
    }

    private static long? FindGap(List<(long Start, long End)> taken, ref int cursor, long from, int length)
    {
        long candidate = from;
        int i = cursor;
        while (true)
        {
            // A break never straddles two universes.
            long universeStart = (candidate - 1) / UniverseSize * UniverseSize + 1;
            if (candidate + length - 1 > universeStart + UniverseSize - 1)
                candidate = universeStart + UniverseSize;
            if ((candidate - 1) / UniverseSize + 1 > MaxUniverse)
                return null;
            while (i < taken.Count && taken[i].End < candidate)
                i++;
            if (i >= taken.Count || taken[i].Start > candidate + length - 1)
            {
                cursor = i;
                return candidate;
            }

            candidate = taken[i].End + 1;
        }
    }

    private static void InsertTaken(List<(long Start, long End)> taken, ref int cursor, long start, long end)
    {
        int i = cursor;
        while (i < taken.Count && taken[i].Start < start)
            i++;
        taken.Insert(i, (start, end));
        cursor = Math.Max(0, i - 1);
    }
}
