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

/// <summary>
/// The channel spacing of fixtures patched one after another, which tells how many channels their real mode uses. The
/// stand-in's mode for a spacing is chosen by <see cref="FixtureType.FitModeToSpacing(DmxMode?, int?)"/>.
/// </summary>
public static class PatchSpacing
{
    /// <summary>
    /// The smallest distance (channels) between two different start addresses of the same universe, or null when fewer
    /// than two fixtures are patched in one universe (<see cref="DmxPatchSpacing.Infer"/>, the rule the MVR importer uses
    /// too). Fixtures patched back to back in their real mode are this far apart, so it is the largest footprint the real
    /// mode can have.
    /// </summary>
    public static int? Infer(IEnumerable<DmxAddress> starts)
    {
        ArgumentNullException.ThrowIfNull(starts);
        return DmxPatchSpacing.Infer(starts.Where(a => a.Universe >= 1 && a.Address is >= 1 and <= 512).Select(a => a.Absolute));
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
