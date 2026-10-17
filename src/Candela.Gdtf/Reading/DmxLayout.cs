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
    private const int MaxReferenceDepth = 16;
    private const int MaxChains = 200_000;

    /// <summary>Recomputes the layout of <paramref name="mode"/> (call after editing a mode in code).</summary>
    public static void Build(DmxMode mode) => Build(mode, null);

    internal static void Build(DmxMode mode, WarningSink? warnings)
    {
        ArgumentNullException.ThrowIfNull(mode);
        string modePath = $"DMXModes/{mode.Name}";
        var root = mode.Geometry;
        var chains = new Dictionary<GdtfGeometry, List<GeometryReference[]>>(ReferenceEqualityComparer.Instance);
        if (root is not null)
        {
            var active = new HashSet<GdtfGeometry>(ReferenceEqualityComparer.Instance) { root };
            int total = 0;
            CollectChains(root, Array.Empty<GeometryReference>(), chains, active, ref total, warnings, modePath);
        }

        var instances = new List<DmxChannelInstance>(mode.Channels.Count);
        var footprints = new SortedDictionary<int, int>();
        var warnedTemplates = new HashSet<GdtfGeometry>(ReferenceEqualityComparer.Instance);

        foreach (var ch in mode.Channels)
        {
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
                    warnings?.Add($"{modePath}/{ch.Name}", "DMXBreak \"Overwrite\" on a channel that is not instanced by a geometry reference; break 1 used.");
                }
                Add(new DmxChannelInstance(ch, brk, ch.Offsets, g, Array.Empty<GeometryReference>()));
                continue;
            }

            bool warnedMissingBreak = false;
            foreach (var chain in list!)
            {
                int add = 0;
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
                    add += hit.DmxOffset - 1;
                    if (ch.BreakIsOverwrite) brk = hit.DmxBreak;
                    matched = true;
                }
                if (!matched && ch.Offsets.Length > 0 && !warnedMissingBreak)
                {
                    warnedMissingBreak = true;
                    warnings?.Add($"{modePath}/{ch.Name}", $"Reference '{chain[^1].Name}' has no Break for DMX break " +
                        $"{(ch.BreakIsOverwrite ? "Overwrite" : ch.DmxBreak.ToString(System.Globalization.CultureInfo.InvariantCulture))}; written offsets used.");
                }
                if (brk < 1) brk = 1;
                int[] offsets = ch.Offsets;
                if (add != 0 && offsets.Length > 0)
                {
                    offsets = new int[ch.Offsets.Length];
                    for (int i = 0; i < offsets.Length; i++) offsets[i] = ch.Offsets[i] + add;
                }
                Add(new DmxChannelInstance(ch, brk, offsets, g, chain));
            }
        }

        mode.ChannelInstances = instances;
        mode.BreakFootprints = new Dictionary<int, int>(footprints);

        void Add(DmxChannelInstance instance)
        {
            instances.Add(instance);
            if (instance.Offsets.Length == 0) return;
            int max = instance.Offsets.Max();
            footprints[instance.Break] = System.Math.Max(footprints.GetValueOrDefault(instance.Break), max);
        }
    }

    /// <summary>Finds every reference chain under <paramref name="node"/>, recursing into referenced geometries.</summary>
    private static void CollectChains(
        GdtfGeometry node, GeometryReference[] chain, Dictionary<GdtfGeometry, List<GeometryReference[]>> chains,
        HashSet<GdtfGeometry> active, ref int total, WarningSink? warnings, string modePath)
    {
        foreach (var g in node.DescendantsAndSelf())
        {
            if (g is not GeometryReference r || r.ReferencedGeometry is not { } template) continue;
            if (total >= MaxChains)
            {
                warnings?.Add(modePath, $"More than {MaxChains} geometry reference instances; the rest are ignored.");
                return;
            }
            var next = new GeometryReference[chain.Length + 1];
            chain.CopyTo(next, 0);
            next[^1] = r;
            if (!chains.TryGetValue(template, out var list)) chains[template] = list = new List<GeometryReference[]>();
            list.Add(next);
            total++;

            if (next.Length >= MaxReferenceDepth || active.Contains(template))
            {
                warnings?.Add(modePath, $"Geometry reference '{r.Name}' to '{template.Name}' is recursive or nested too deeply; not expanded further.");
                continue;
            }
            active.Add(template);
            CollectChains(template, next, chains, active, ref total, warnings, modePath);
            active.Remove(template);
        }
    }
}
