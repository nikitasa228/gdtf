using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Dmx;

/// <summary>
/// A fixture drawn and patched with a stand-in GDTF because the library has no file of the model the scene asks for:
/// a similar model of the same manufacturer (an MVR stub resolved by <see cref="FixtureLibrary.FindBySpec"/>, or the
/// substitute an MVR import chose).
/// </summary>
/// <param name="RequestedSpec">The fixture type the scene asks for (an MVR <c>GDTFSpec</c> such as <c>Ayrton@MagicBlade R.gdtf</c>).</param>
/// <param name="RequestedMode">The DMX mode the scene asks for, or null.</param>
/// <param name="StandIn">GDTF file name of the stand-in.</param>
/// <param name="PatchSpacing">
/// Channels between the start addresses of the fixtures of this type (<see cref="PatchSpacing.Infer"/>), or null when
/// unknown: the stand-in's mode is chosen to fit it, and patch problems that only its larger footprint causes are warnings.
/// </param>
public sealed record FixtureSubstitution(string RequestedSpec, string? RequestedMode, string StandIn, int? PatchSpacing);

/// <summary>The channel spacing of fixtures patched one after another, which tells how many channels their real mode uses.</summary>
public static class PatchSpacing
{
    /// <summary>
    /// The smallest distance (channels) between two different start addresses of the same universe, or null when fewer
    /// than two fixtures are patched in one universe. Fixtures patched back to back in their real mode are this far apart,
    /// so it is the largest footprint the real mode can have.
    /// </summary>
    public static int? Infer(IEnumerable<DmxAddress> starts)
    {
        ArgumentNullException.ThrowIfNull(starts);
        var sorted = starts.Where(a => a.Universe >= 1 && a.Address is >= 1 and <= 512).Select(a => a.Absolute).Distinct().Order().ToArray();
        int? best = null;
        for (int i = 1; i < sorted.Length; i++)
        {
            int a = sorted[i - 1], b = sorted[i];
            if ((a - 1) / 512 != (b - 1) / 512) continue; // different universes
            int gap = b - a;
            if (best is null || gap < best) best = gap;
        }
        return best;
    }

    /// <summary>
    /// Spacing per fixture type and requested mode (<see cref="FixtureComponent.GdtfSpec"/>, <see cref="FixtureComponent.GdtfMode"/>)
    /// over every fixture in <paramref name="fixtures"/>, from each fixture's first-break address. O(n log n).
    /// </summary>
    public static Dictionary<(string Spec, string Mode), int?> ByType(IEnumerable<FixtureComponent> fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        var starts = new Dictionary<(string, string), List<DmxAddress>>();
        foreach (var f in fixtures)
        {
            var key = Key(f.GdtfSpec, f.GdtfMode);
            if (!starts.TryGetValue(key, out var list)) starts[key] = list = new List<DmxAddress>();
            if (FirstBreak(f) is { } a) list.Add(a);
        }
        var result = new Dictionary<(string, string), int?>(starts.Count);
        foreach (var (key, list) in starts) result[key] = Infer(list);
        return result;
    }

    /// <summary>The grouping key of <see cref="ByType"/>.</summary>
    public static (string Spec, string Mode) Key(string? spec, string? mode) => ((spec ?? "").Trim(), (mode ?? "").Trim());

    private static DmxAddress? FirstBreak(FixtureComponent f)
    {
        DmxAddress? first = null;
        foreach (var a in f.Addresses)
            if (first is null || a.Break < first.Value.Break) first = a;
        return first;
    }
}

/// <summary>Chooses the DMX mode of a stand-in GDTF so that it fits the patch of the fixtures it stands in for.</summary>
public static class SubstituteModes
{
    /// <summary>
    /// The mode of <paramref name="type"/> to use for fixtures patched <paramref name="spacing"/> channels apart: the
    /// requested mode when the stand-in has it and it fits; else the mode with the largest first-break footprint that
    /// fits (the most complete one that does not overlap the neighbours; GDTF order breaks ties); else, when none fits,
    /// the smallest one. Without a spacing, the requested mode (by name), else the first mode. Null when the type has no
    /// modes.
    /// </summary>
    public static DmxMode? Choose(FixtureType type, string? requestedMode, int? spacing)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.DmxModes.Count == 0) return null;
        var requested = string.IsNullOrWhiteSpace(requestedMode) ? null : type.FindMode(requestedMode);
        if (spacing is not { } s || s < 1) return requested ?? type.DmxModes[0];
        if (requested is not null && Footprint(requested) <= s) return requested;
        DmxMode? best = null, smallest = null;
        foreach (var m in type.DmxModes)
        {
            int f = Footprint(m);
            if (f <= s && (best is null || f > Footprint(best))) best = m;
            if (smallest is null || f < Footprint(smallest)) smallest = m;
        }
        return best ?? smallest;
    }

    /// <summary>Channels of a mode's first DMX break (what one patch address covers).</summary>
    public static int Footprint(DmxMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        int first = int.MaxValue, footprint = 0;
        foreach (var (brk, slots) in mode.BreakFootprints)
        {
            if (slots <= 0 || brk >= first) continue;
            first = brk;
            footprint = slots;
        }
        return footprint;
    }
}
