namespace Candela.Gdtf;

/// <summary>
/// Expands a DMX mode into addressable channel instances (<see cref="DmxMode.ChannelInstances"/>) and per-break
/// footprints (<see cref="DmxMode.BreakFootprints"/>).
/// </summary>
/// <remarks>
/// <para>A channel whose geometry lies in the mode's root tree is addressed at its written offsets and break.</para>
/// <para>
/// A channel whose geometry lies in another top-level geometry T is a template: it is instanced once for every
/// <see cref="GeometryReference"/> to T reachable from the mode root (also through references nested inside other
/// referenced geometries). The instance's offsets are the written ones plus <c>Break.DMXOffset − 1</c> of each reference
/// on the chain that has a Break for the channel's DMX break. Channels with DMXBreak "Overwrite" take the last Break of
/// a reference (its break number and offset). Example: an LED bar whose "Pixel" channels have offsets 1–4 and whose
/// references Pixel 1…6 have DMXOffset 3, 7, …, 23 occupies slots 3–26 of break 1.
/// </para>
/// </remarks>
public static class DmxLayout
{
    /// <summary>Deepest chain of nested geometry references that is expanded.</summary>
    internal const int MaxReferenceDepth = 16;

    /// <summary>Most reference instances expanded per mode (a hostile file could nest references exponentially).</summary>
    internal const int MaxChains = 200_000;

    /// <summary>
    /// Most channel instances per mode (the library's largest mode has 2254): channels × reference instances could
    /// otherwise multiply to billions in a hostile file.
    /// </summary>
    internal const int MaxInstances = 1_000_000;

    /// <summary>
    /// Highest slot offset accepted within one DMX break (64 universes; the library's largest footprint is 1813 slots).
    /// Larger written offsets are dropped by the reader and larger instanced offsets are not laid out, so a hostile file
    /// cannot make the DMX layer allocate gigabytes for one fixture.
    /// </summary>
    public const int MaxSlotOffset = 64 * 512;

    /// <summary>Recomputes the layout of <paramref name="mode"/> (call after editing a mode in code).</summary>
    public static void Build(DmxMode mode) => Build(mode, null);

    internal static void Build(DmxMode mode, WarningSink? warnings)
    {
        ArgumentNullException.ThrowIfNull(mode);
        string modePath = ReadContext.ChildPath("DMXModes", mode.Name);
        var root = mode.Geometry;
        var chains = new Dictionary<GdtfGeometry, List<GeometryReference[]>>(ReferenceEqualityComparer.Instance);
        if (root is not null) CollectChains(root, chains, warnings, modePath);

        var instances = new List<DmxChannelInstance>(mode.Channels.Count);
        var footprints = new SortedDictionary<int, int>();
        var warnedTemplates = new HashSet<GdtfGeometry>(ReferenceEqualityComparer.Instance);
        bool warnedRange = false, full = false;

        foreach (var ch in mode.Channels)
        {
            if (full) break;
            var g = ch.Geometry;
            List<GeometryReference[]>? list = null;
            bool direct = g is null || root is null || g.IsSelfOrDescendantOf(root) || !chains.TryGetValue(g.TopLevel, out list);
            if (direct)
            {
                if (g is not null && root is not null && !g.IsSelfOrDescendantOf(root) && warnedTemplates.Add(g.TopLevel))
                {
                    warnings?.Add(modePath, $"Geometry '{g.TopLevel.Name}' used by channel '{ch.Name}' is not part of the mode's geometry " +
                        "tree and not referenced from it; its channels are addressed at their written offsets.");
                }
                int brk = ch.DmxBreak;
                if (ch.BreakIsOverwrite)
                {
                    brk = 1;
                    warnings?.Add(ReadContext.ChildPath(modePath, ch.Name), "DMXBreak \"Overwrite\" on a channel that is not instanced by a geometry reference; break 1 used.");
                }
                if (!Add(new DmxChannelInstance(ch, brk, ch.Offsets, g, Array.Empty<GeometryReference>()))) break;
                continue;
            }

            bool warnedMissingBreak = false;
            foreach (var chain in list!)
            {
                long add = 0;
                int brk = ch.BreakIsOverwrite ? -1 : ch.DmxBreak;
                bool matched = false;
                foreach (var r in chain)
                {
                    if (r.Breaks.Count == 0) continue;
                    GeometryBreak? b = null;
                    if (ch.BreakIsOverwrite) b = r.Breaks[^1];
                    else
                        foreach (var candidate in r.Breaks)
                            if (candidate.DmxBreak == brk) { b = candidate; break; }
                    if (b is not { } hit) continue;
                    add += hit.DmxOffset - 1L;
                    if (ch.BreakIsOverwrite) brk = hit.DmxBreak;
                    matched = true;
                }
                if (!matched && ch.Offsets.Length > 0 && !warnedMissingBreak)
                {
                    warnedMissingBreak = true;
                    warnings?.Add(ReadContext.ChildPath(modePath, ch.Name), $"Reference '{chain[^1].Name}' has no Break for DMX break " +
                        $"{(ch.BreakIsOverwrite ? "Overwrite" : ch.DmxBreak.ToString(System.Globalization.CultureInfo.InvariantCulture))}; written offsets used.");
                }
                if (brk < 1) brk = 1;
                int[] offsets = ch.Offsets;
                if (add != 0 && offsets.Length > 0)
                {
                    offsets = new int[ch.Offsets.Length];
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        long o = (long)ch.Offsets[i] + add;
                        offsets[i] = o is >= 1 and <= MaxSlotOffset ? (int)o : -1;
                    }
                    if (Array.IndexOf(offsets, -1) >= 0)
                    {
                        if (!warnedRange)
                            warnings?.Add(ReadContext.ChildPath(modePath, ch.Name), $"Reference '{chain[^1].Name}' places the channel beyond slot " +
                                $"{MaxSlotOffset} of its break; such instances are not laid out.");
                        warnedRange = true;
                        continue;
                    }
                }
                if (!Add(new DmxChannelInstance(ch, brk, offsets, g, chain))) break;
            }
        }

        mode.ChannelInstances = instances;
        mode.BreakFootprints = new Dictionary<int, int>(footprints);

        // Adds an instance; false (after one warning) when the mode is full.
        bool Add(DmxChannelInstance instance)
        {
            if (instances.Count >= MaxInstances)
            {
                if (!full) warnings?.Add(modePath, $"More than {MaxInstances} channel instances; the rest are not laid out.");
                full = true;
                return false;
            }
            instances.Add(instance);
            if (instance.Offsets.Length == 0) return true;
            int max = 0;
            foreach (int o in instance.Offsets) max = System.Math.Max(max, o);
            footprints[instance.Break] = System.Math.Max(footprints.GetValueOrDefault(instance.Break), max);
            return true;
        }
    }

    /// <summary>
    /// Finds every reference chain reachable from <paramref name="root"/>, following references into the geometries
    /// they instance (depth first in file order, iteratively). The references inside each geometry tree are listed once,
    /// so the cost is proportional to the number of chains, which is capped at <see cref="MaxChains"/>.
    /// </summary>
    private static void CollectChains(
        GdtfGeometry root, Dictionary<GdtfGeometry, List<GeometryReference[]>> chains, WarningSink? warnings, string modePath)
    {
        var referencesUnder = new Dictionary<GdtfGeometry, GeometryReference[]>(ReferenceEqualityComparer.Instance);
        GeometryReference[] ReferencesUnder(GdtfGeometry g)
        {
            if (!referencesUnder.TryGetValue(g, out var list))
            {
                list = g.DescendantsAndSelf().OfType<GeometryReference>().Where(r => r.ReferencedGeometry is not null).ToArray();
                referencesUnder[g] = list;
            }
            return list;
        }

        // Each frame walks the references under one geometry; Template is that geometry (removed from the active set
        // when the frame is done, which detects cycles), Chain the references that led there.
        var stack = new Stack<(GdtfGeometry Template, GeometryReference[] Chain, int Next)>();
        var active = new HashSet<GdtfGeometry>(ReferenceEqualityComparer.Instance) { root };
        stack.Push((root, Array.Empty<GeometryReference>(), 0));
        int total = 0;
        bool warnedRecursion = false;
        while (stack.Count > 0)
        {
            var (node, chain, next) = stack.Pop();
            var refs = ReferencesUnder(node);
            if (next >= refs.Length)
            {
                active.Remove(node);
                continue;
            }
            stack.Push((node, chain, next + 1));
            var r = refs[next];
            var template = r.ReferencedGeometry!;
            if (total >= MaxChains)
            {
                warnings?.Add(modePath, $"More than {MaxChains} geometry reference instances; the rest are ignored.");
                return;
            }
            var extended = new GeometryReference[chain.Length + 1];
            chain.CopyTo(extended, 0);
            extended[^1] = r;
            if (!chains.TryGetValue(template, out var list)) chains[template] = list = new List<GeometryReference[]>();
            list.Add(extended);
            total++;

            if (extended.Length >= MaxReferenceDepth || active.Contains(template))
            {
                if (!warnedRecursion)
                    warnings?.Add(modePath, $"Geometry reference '{r.Name}' to '{template.Name}' is recursive or nested too deeply; not expanded further.");
                warnedRecursion = true;
                continue;
            }
            active.Add(template);
            stack.Push((template, extended, 0));
        }
    }
}
