namespace Candela.Gdtf;

/// <summary>
/// The channel spacing of fixtures patched one after another, which tells how many channels their real mode can use.
/// Together with <see cref="FixtureType.FitModeToSpacing(DmxMode?, int?)"/> it is the one rule by which the MVR importer
/// and the DMX engine choose the mode of a stand-in GDTF.
/// </summary>
public static class DmxPatchSpacing
{
    /// <summary>
    /// The smallest distance (channels) between two different start addresses within one universe, or null when fewer
    /// than two fixtures are patched in one universe. Fixtures patched back to back in their real mode are this far apart,
    /// so it is the largest first-break footprint their mode can have. O(n log n).
    /// </summary>
    /// <param name="absoluteStartAddresses">Absolute start addresses, <c>(universe − 1) · 512 + address</c> with universes
    /// and addresses from 1; values below 1 are ignored, repeats count once.</param>
    public static int? Infer(IEnumerable<int> absoluteStartAddresses)
    {
        ArgumentNullException.ThrowIfNull(absoluteStartAddresses);
        var sorted = absoluteStartAddresses.Where(a => a > 0).Distinct().Order().ToArray();
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
}
