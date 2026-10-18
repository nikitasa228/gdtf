using System.Globalization;
using Candela.Core.Diagnostics;
using Candela.Gdtf;

namespace Candela.Mvr;

/// <summary>
/// Resolves the <c>GDTFSpec</c> references of an MVR: a complete embedded GDTF wins; a stub (no geometry or no DMX
/// modes, as Depence writes them) or a missing file is replaced by the fixture library's file for the same
/// manufacturer and model; the stub itself is the last resort.
/// </summary>
internal sealed class GdtfResolver
{
    private readonly MvrDocument _doc;
    private readonly FixtureLibrary? _library;
    private readonly IssueSink _sink;
    private readonly Dictionary<string, MvrGdtfReference> _bySpec = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MvrGdtfReference> _ordered = new();

    public GdtfResolver(MvrDocument doc, FixtureLibrary? library, IssueSink sink)
    {
        _doc = doc;
        _library = library;
        _sink = sink;
    }

    public IReadOnlyList<MvrGdtfReference> References => _ordered;

    /// <summary>The resolution of a spec (resolved once per distinct spec; <paramref name="users"/> are listed in issues).</summary>
    public MvrGdtfReference Resolve(string spec, IReadOnlyList<Guid> users)
    {
        string key = spec.Trim();
        if (_bySpec.TryGetValue(key, out var existing)) return existing;
        var r = new MvrGdtfReference(key);
        _bySpec[key] = r;
        _ordered.Add(r);
        ResolveCore(r, users.Take(64).ToArray());
        return r;
    }

    private void ResolveCore(MvrGdtfReference r, Guid[] users)
    {
        string spec = r.SpecName;
        FixtureType? embedded = null;
        var file = _doc.FindFile(spec);
        if (file is not null)
        {
            r.EmbeddedFileName = file.Name;
            if (file.Bytes is null)
            {
                _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.GdtfUnreadable,
                    $"Embedded GDTF '{file.Name}' cannot be read from the archive ({file.Error}).", users);
            }
            else
            {
                r.EmbeddedBytes = file.Bytes;
                try
                {
                    using var archive = GdtfArchive.FromBytes(file.Bytes, MvrDocument.FileNameOf(file.Name));
                    embedded = GdtfReader.Read(archive);
                    r.EmbeddedIsStub = !IsComplete(embedded, archive);
                }
                catch (Exception e) when (e is GdtfFormatException or InvalidDataException or IOException)
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.GdtfUnreadable,
                        $"Embedded GDTF '{file.Name}' is not a readable GDTF ({e.Message}).", users);
                    r.EmbeddedBytes = null;
                }
            }
            if (embedded is not null && !r.EmbeddedIsStub)
            {
                r.Source = MvrGdtfSource.Embedded;
                r.FileName = EnsureGdtfExtension(MvrDocument.FileNameOf(file.Name));
                r.FixtureType = embedded;
                return;
            }
        }

        var match = FindInLibrary(spec, embedded);
        if (match is not null)
        {
            FixtureType? type = null;
            try
            {
                type = _library!.GetFixtureType(match.Entry);
            }
            catch (Exception e) when (e is GdtfFormatException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.GdtfUnreadable,
                    $"Library file '{match.Entry.FileName}' found for '{spec}' cannot be read ({e.Message}).", users);
            }
            if (type is not null)
            {
                r.Source = MvrGdtfSource.Library;
                r.FileName = match.Entry.FileName;
                r.LibraryEntry = match.Entry;
                r.MatchKind = match.Kind;
                r.MatchScore = match.Score;
                r.FixtureType = type;
                string why = embedded is not null
                    ? $"the embedded '{r.EmbeddedFileName}' is a stub without {StubLacks(embedded)}"
                    : file is null ? "it is not embedded in the MVR" : "the embedded file is unreadable";
                if (match.Kind == FixtureMatchKind.Substitute)
                {
                    _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.GdtfSubstitute,
                        string.Create(CultureInfo.InvariantCulture,
                            $"'{spec}' is not in the fixture library and {why}; the similar '{match.Entry.FileName}' is used instead (name similarity {match.Score:0.00}). Add the real GDTF to the library for an exact match."),
                        users);
                }
                else if (embedded is not null)
                {
                    _sink.Add(IssueSeverity.Note, IssueCategory.Fixture, MvrIssueCodes.GdtfStub,
                        $"'{spec}': {why}; the library file '{match.Entry.FileName}' is used.", users);
                }
                return;
            }
        }

        if (embedded is not null)
        {
            r.Source = MvrGdtfSource.EmbeddedStub;
            r.FileName = EnsureGdtfExtension(MvrDocument.FileNameOf(r.EmbeddedFileName!));
            r.FixtureType = embedded;
            _sink.Add(IssueSeverity.Warning, IssueCategory.Fixture, MvrIssueCodes.GdtfUnresolved,
                $"'{spec}' is only embedded as a stub without {StubLacks(embedded)} and the fixture library has no match; the fixtures have no real geometry or DMX layout.",
                users);
            return;
        }

        r.Source = MvrGdtfSource.Unresolved;
        r.FileName = EnsureGdtfExtension(MvrDocument.FileNameOf(MvrDocument.NormalizePath(spec)));
        _sink.Add(IssueSeverity.Error, IssueCategory.Fixture, MvrIssueCodes.GdtfUnresolved,
            _library is null
                ? $"GDTF '{spec}' is not usable from the MVR and no fixture library was given."
                : $"GDTF '{spec}' is not usable from the MVR and the fixture library has no matching fixture.",
            users);
    }

    private FixtureMatch? FindInLibrary(string spec, FixtureType? embedded)
    {
        if (_library is null) return null;
        FixtureMatch? best = null;
        void Consider(FixtureMatch? m)
        {
            if (m is null) return;
            if (best is null ||
                (m.IsExactModel && !best.IsExactModel) ||
                (m.IsExactModel == best.IsExactModel && m.Score > best.Score + 1e-12))
            {
                best = m;
            }
        }

        if (embedded is not null && embedded.FixtureTypeId != Guid.Empty && _library.FindByFixtureTypeId(embedded.FixtureTypeId) is { } byId)
            Consider(new FixtureMatch(byId, FixtureMatchKind.SameModel, 1));
        if (best is { IsExactModel: true }) return best;
        Consider(_library.FindBySpec(spec));
        if (best is { IsExactModel: true }) return best;
        if (embedded is not null && !string.IsNullOrWhiteSpace(embedded.Name))
        {
            Consider(_library.Find(embedded.Manufacturer, embedded.Name));
            if (!string.IsNullOrWhiteSpace(embedded.LongName) && embedded.LongName != embedded.Name)
                Consider(_library.Find(embedded.Manufacturer, embedded.LongName));
        }
        return best;
    }

    /// <summary>A GDTF is complete when it has a DMX mode with channels and at least one geometry with a drawable model.</summary>
    internal static bool IsComplete(FixtureType type, GdtfArchive archive)
    {
        bool hasChannels = type.DmxModes.Any(m => m.Channels.Count > 0);
        bool hasModelFiles = archive.EntryNames.Any(n => n.StartsWith("models/", StringComparison.OrdinalIgnoreCase));
        bool hasGeometry = type.AllGeometries().Any(g => g.Model is { } model &&
            (model.PrimitiveType != GdtfPrimitiveType.Undefined || (!string.IsNullOrWhiteSpace(model.File) && hasModelFiles)));
        return hasChannels && hasGeometry;
    }

    private static string StubLacks(FixtureType type)
    {
        bool hasChannels = type.DmxModes.Any(m => m.Channels.Count > 0);
        return hasChannels ? "geometry" : "DMX modes";
    }

    internal static string EnsureGdtfExtension(string name) =>
        name.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase) ? name : name + ".gdtf";
}

/// <summary>Chooses the DMX mode of a resolved GDTF for the mode name an MVR asks for.</summary>
internal static class ModeMatcher
{
    /// <summary>
    /// The mode named <paramref name="requested"/> (exact, then ignoring case/spaces/punctuation). Otherwise the mode
    /// whose first-break footprint is closest to <paramref name="inferredFootprint"/> (when known) combined with the
    /// name similarity; ties keep the GDTF's mode order. Null only when the GDTF has no modes.
    /// </summary>
    public static (DmxMode? Mode, bool Exact) Match(FixtureType type, string? requested, int? inferredFootprint)
    {
        if (type.DmxModes.Count == 0) return (null, false);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (type.FindMode(requested) is { } exact) return (exact, true);
            string key = FixtureNames.Key(requested);
            foreach (var m in type.DmxModes)
                if (key.Length > 0 && FixtureNames.Key(m.Name) == key) return (m, true);
        }

        DmxMode? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var m in type.DmxModes)
        {
            double score = NameSimilarity(requested, m.Name);
            if (inferredFootprint is { } inferred)
            {
                int fp = FirstBreakFootprint(m);
                score = 0.4 * score + 0.6 / (1.0 + System.Math.Abs(fp - inferred));
            }
            if (score > bestScore + 1e-12)
            {
                best = m;
                bestScore = score;
            }
        }
        return (best, false);
    }

    /// <summary>Slots of the mode's first DMX break (the break MVR's <c>break="0"</c> address patches).</summary>
    public static int FirstBreakFootprint(DmxMode mode)
    {
        if (mode.BreakFootprints.TryGetValue(1, out int f)) return f;
        if (mode.BreakFootprints.Count > 0) return mode.BreakFootprints.OrderBy(kv => kv.Key).First().Value;
        return mode.DeclaredChannelCount;
    }

    /// <summary>Similarity of two mode names in 0 … 1: equal keys 1, containment 0.8, else shared words (prefixes of ≥ 3 letters count).</summary>
    public static double NameSimilarity(string? a, string? b)
    {
        string ka = FixtureNames.Key(a), kb = FixtureNames.Key(b);
        if (ka.Length == 0 || kb.Length == 0) return 0;
        if (ka == kb) return 1;
        if (ka.Contains(kb, StringComparison.Ordinal) || kb.Contains(ka, StringComparison.Ordinal)) return 0.8;
        var wa = FixtureNames.Words(a);
        var wb = FixtureNames.Words(b);
        int shared = 0;
        foreach (var x in wa)
            foreach (var y in wb)
            {
                if (x == y || (x.Length >= 3 && y.Length >= 3 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal))))
                {
                    shared++;
                    break;
                }
            }
        int union = wa.Count + wb.Count - shared;
        return union <= 0 ? 0 : 0.7 * shared / union;
    }

    /// <summary>
    /// The DMX footprint suggested by the spacing of absolute addresses of fixtures of one type and mode: the most
    /// frequent gap between consecutive start addresses within a universe (ties: the smaller gap). Null with fewer than
    /// two fixtures in one universe.
    /// </summary>
    public static int? InferFootprint(IEnumerable<int> absoluteAddresses)
    {
        var byUniverse = absoluteAddresses.Where(a => a > 0).Distinct().GroupBy(a => (a - 1) / 512);
        var histogram = new Dictionary<int, int>();
        foreach (var u in byUniverse)
        {
            var sorted = u.OrderBy(a => a).ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                int gap = sorted[i] - sorted[i - 1];
                if (gap is > 0 and <= 512) histogram[gap] = histogram.GetValueOrDefault(gap) + 1;
            }
        }
        if (histogram.Count == 0) return null;
        return histogram.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }
}
