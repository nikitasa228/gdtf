using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>
/// Duplicate and Paste copy fixtures with their DMX addresses, so every copy overlaps its original (open review item of
/// the editor): this finds the copied fixtures whose channels overlap any other fixture's and patches them to the next
/// free addresses — in their original order, one after the other, each break of a fixture within one universe — as one
/// undo step. Linear in the number of patched ranges plus copies (sorted interval lists, one free-address search for
/// all copies).
/// </summary>
internal static class DuplicatePatch
{
    /// <summary>
    /// The fixtures under <paramref name="roots"/> (the copies and their descendants) whose patched channels overlap
    /// another fixture's, in patch order.
    /// </summary>
    public static List<Entity> Overlapping(SceneModel scene, FixtureProfileCache profiles, IEnumerable<Guid> roots)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(roots);
        var copies = new HashSet<Guid>();
        foreach (var id in roots)
        {
            if (scene.Find(id) is not { } root)
                continue;
            foreach (var e in root.SelfAndDescendants())
            {
                if (e.Has<FixtureComponent>())
                    copies.Add(e.Id);
            }
        }

        var result = new List<Entity>();
        if (copies.Count == 0)
            return result;
        var patch = Patch.Build(scene, profiles);
        // Every patched range by universe, sorted by start: an overlap test is a binary search.
        var byUniverse = new Dictionary<int, List<(int Start, int End, Guid Owner)>>();
        foreach (var entry in patch.Entries)
        {
            foreach (var r in entry.Ranges)
            {
                if (!byUniverse.TryGetValue(r.Universe, out var list))
                    byUniverse[r.Universe] = list = new List<(int, int, Guid)>();
                list.Add((r.Start, r.End, entry.Entity.Id));
            }
        }

        foreach (var list in byUniverse.Values)
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        foreach (var entry in patch.Entries)
        {
            if (!copies.Contains(entry.Entity.Id))
                continue;
            foreach (var r in entry.Ranges)
            {
                if (byUniverse.TryGetValue(r.Universe, out var list) && Overlaps(list, r.Start, r.End, entry.Entity.Id))
                {
                    result.Add(entry.Entity);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Patches <paramref name="fixtures"/> to the next free addresses (their own current channels count as free) as one
    /// undo step; returns how many were patched. Fixtures without a resolvable footprint keep their addresses.
    /// </summary>
    public static int Repatch(SceneModel scene, FixtureProfileCache profiles, UndoStack undo, IReadOnlyList<Entity> fixtures)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(fixtures);
        var moving = new HashSet<Guid>(fixtures.Where(f => f.Scene == scene).Select(f => f.Id));
        if (moving.Count == 0)
            return 0;
        var patch = Patch.Build(scene, profiles);
        var used = new List<PatchRange>();
        var plan = new List<(Entity Entity, List<DmxBreakLayout> Breaks)>();
        foreach (var entry in patch.Entries)
        {
            if (!moving.Contains(entry.Entity.Id))
            {
                used.AddRange(entry.Ranges);
                continue;
            }

            var breaks = entry.Ranges.GroupBy(r => r.Break).OrderBy(g => g.Key)
                .Select(g => new DmxBreakLayout(g.Key, g.Max(r => r.End) - g.Min(r => r.Start) + 1)).ToList();
            if (breaks.Count > 0)
                plan.Add((entry.Entity, breaks));
        }

        // All copies in one search: each one goes after the previous one, so the copies get consecutive addresses.
        var all = plan.SelectMany(p => p.Breaks).ToList();
        var found = AutoPatch.FindFree(used, all);
        if (found is null || found.Count != all.Count)
            return 0;
        int n = 0, k = 0;
        using (undo.Group(plan.Count == 1 ? $"Patch {plan[0].Entity.Name}" : $"Patch {plan.Count} copied fixtures"))
        {
            foreach (var (entity, breaks) in plan)
            {
                var addresses = found.Skip(k).Take(breaks.Count).ToList();
                k += breaks.Count;
                if (entity.Get<FixtureComponent>() is not { } fixture)
                    continue;
                undo.Execute(ModifyComponentCommand.Create(fixture, f => f.Addresses = addresses, "Patch copy"));
                n++;
            }
        }

        undo.BreakMerge();
        return n;
    }

    private static bool Overlaps(List<(int Start, int End, Guid Owner)> sorted, int start, int end, Guid self)
    {
        // First range that could reach `start`: ranges are sorted by start; any range starting at or before `end` may overlap.
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid].Start <= end)
                lo = mid + 1;
            else
                hi = mid;
        }

        // Ranges [0, lo) start at or before `end`; walk back while they could still overlap (footprints are ≤ 512).
        for (int i = lo - 1; i >= 0; i--)
        {
            var r = sorted[i];
            if (r.Start < start - 512)
                break;
            if (r.Owner != self && r.End >= start)
                return true;
        }

        return false;
    }
}
