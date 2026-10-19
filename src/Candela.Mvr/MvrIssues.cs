using Candela.Core.Diagnostics;

namespace Candela.Mvr;

/// <summary>
/// Stable <see cref="Issue.Code"/> values reported by the MVR reader, importer and writer (the issues panel filters and
/// silences by code).
/// </summary>
public static class MvrIssueCodes
{
    /// <summary>The scene description is not at the archive root.</summary>
    public const string ArchiveLayout = "mvr.archive-layout";

    /// <summary>The file declares a version other than 1.x, or none.</summary>
    public const string VersionUnsupported = "mvr.version";

    /// <summary>Elements nested deeper than <see cref="MvrReader.MaxXmlDepth"/> were dropped with their content.</summary>
    public const string NestingTooDeep = "mvr.nesting-too-deep";

    /// <summary>An element this reader does not know was ignored (kept verbatim in the document).</summary>
    public const string UnknownElement = "mvr.element-unknown";

    /// <summary>An object or definition has no (valid) uuid; a new one was generated.</summary>
    public const string UuidMissing = "mvr.uuid-missing";

    /// <summary>A uuid is used twice; the later object got a new one.</summary>
    public const string UuidDuplicate = "mvr.uuid-duplicate";

    /// <summary>A matrix could not be parsed; identity was used.</summary>
    public const string MatrixInvalid = "mvr.matrix-invalid";

    /// <summary>A matrix has zero-length or coplanar axes; only its translation was kept.</summary>
    public const string MatrixDegenerate = "mvr.matrix-degenerate";

    /// <summary>A matrix mirrors (determinant &lt; 0); kept as a negative scale.</summary>
    public const string MatrixMirrored = "mvr.matrix-mirrored";

    /// <summary>A matrix has non-orthogonal axes; the shear was dropped.</summary>
    public const string MatrixSheared = "mvr.matrix-sheared";

    /// <summary>A value could not be parsed (number, boolean, colour, enumeration); the default was used.</summary>
    public const string ValueInvalid = "mvr.value-invalid";

    /// <summary>A DMX address could not be parsed and was skipped.</summary>
    public const string AddressInvalid = "mvr.address-invalid";

    /// <summary>An embedded file could not be read from the archive.</summary>
    public const string FileUnreadable = "mvr.file-unreadable";

    /// <summary>A referenced file is not in the archive.</summary>
    public const string FileMissing = "mvr.file-missing";

    /// <summary>An embedded GDTF could not be parsed.</summary>
    public const string GdtfUnreadable = "mvr.gdtf-unreadable";

    /// <summary>An embedded GDTF is a stub (no geometry or no DMX modes) and was replaced from the library.</summary>
    public const string GdtfStub = "mvr.gdtf-stub";

    /// <summary>
    /// An embedded GDTF without drawable geometry (or without DMX channels) is used as is: the library does not have the
    /// model, and a substitute would replace its real DMX layout.
    /// </summary>
    public const string GdtfNoGeometry = "mvr.gdtf-no-geometry";

    /// <summary>A GDTF was resolved to a different model of the same brand (the exact model is not in the library).</summary>
    public const string GdtfSubstitute = "mvr.gdtf-substitute";

    /// <summary>A GDTF reference could not be resolved at all.</summary>
    public const string GdtfUnresolved = "mvr.gdtf-unresolved";

    /// <summary>The requested DMX mode does not exist in the resolved GDTF; the closest mode was used.</summary>
    public const string ModeUnknown = "mvr.mode-unknown";

    /// <summary>
    /// The DMX mode chosen for a substitute GDTF (or for a mode name the GDTF lacks) needs more channels than the patch
    /// of the MVR leaves between consecutive fixtures, so their DMX ranges overlap.
    /// </summary>
    public const string ModeFootprint = "mvr.mode-footprint";

    /// <summary>
    /// A substitute GDTF has a mode of the requested name, but it is wider than the patch spacing of the fixtures (the
    /// name belongs to the missing model): the stand-in's most complete mode that fits is used instead.
    /// </summary>
    public const string SubstituteMode = "mvr.substitute-mode";

    /// <summary>A 3D file could not be decoded.</summary>
    public const string GeometryUnreadable = "mvr.geometry-unreadable";

    /// <summary>A 3D file has a format MVR does not support.</summary>
    public const string GeometryUnsupported = "mvr.geometry-unsupported";

    /// <summary>Symbol expansion reached the import's geometry budget; the remaining geometry was not imported.</summary>
    public const string GeometryLimit = "mvr.geometry-limit";

    /// <summary>An object could not be converted (a program error); its children were attached to its parent.</summary>
    public const string ObjectFailed = "mvr.object-failed";

    /// <summary>A symbol references a symbol definition that does not exist.</summary>
    public const string SymdefMissing = "mvr.symdef-missing";

    /// <summary>A symbol definition (indirectly) instances itself; the recursion was cut.</summary>
    public const string SymdefCycle = "mvr.symdef-cycle";

    /// <summary>A fixture's focus references a focus point that does not exist.</summary>
    public const string FocusMissing = "mvr.focus-missing";

    /// <summary>A connection references an object that does not exist.</summary>
    public const string ConnectionTargetMissing = "mvr.connection-target-missing";

    /// <summary>An object id already exists in the target scene and was replaced on merge.</summary>
    public const string IdRemapped = "mvr.id-remapped";

    /// <summary>Content was dropped because MVR or Core cannot represent it.</summary>
    public const string NotRepresentable = "mvr.not-representable";

    /// <summary>A GDTF or model the writer needed was not supplied by the caller.</summary>
    public const string ExportResourceMissing = "mvr.export-resource-missing";

    /// <summary>Too many issues; the rest were suppressed.</summary>
    public const string Truncated = "mvr.issues-truncated";
}

/// <summary>
/// Collects issues with a cap and aggregation of repetitive ones ("unknown element X ignored 91 times" is reported
/// once with a count instead of 91 times).
/// </summary>
internal sealed class IssueSink
{
    private readonly List<Issue> _issues;
    private readonly int _limit;
    private readonly Dictionary<(string Code, string Key), AggregateEntry> _aggregates = new();
    private readonly List<(string Code, string Key)> _aggregateOrder = new();
    private int _suppressed;

    public IssueSink(List<Issue> target, int limit = 2000)
    {
        _issues = target;
        _limit = limit;
    }

    public int Count => _issues.Count + _suppressed + _aggregates.Count;

    public void Add(IssueSeverity severity, string category, string code, string message, params Guid[] entities)
    {
        if (_issues.Count >= _limit)
        {
            _suppressed++;
            return;
        }
        _issues.Add(new Issue(severity, category, code, message, entities));
    }

    /// <summary>
    /// Counts an occurrence of a repetitive issue; one issue per (code, key) is emitted by <see cref="Complete"/> with
    /// the message built from the occurrence count. Up to 64 related entities are kept.
    /// </summary>
    public void Aggregate(IssueSeverity severity, string category, string code, string key, Func<int, string> message, Guid entity = default)
    {
        var k = (code, key);
        if (!_aggregates.TryGetValue(k, out var a))
        {
            a = new AggregateEntry(severity, category, message);
            _aggregates[k] = a;
            _aggregateOrder.Add(k);
        }
        a.Count++;
        if (entity != Guid.Empty && a.Entities.Count < 64) a.Entities.Add(entity);
    }

    /// <summary>Emits the aggregated issues and the truncation note. Idempotent.</summary>
    public void Complete()
    {
        foreach (var k in _aggregateOrder)
        {
            var a = _aggregates[k];
            Add(a.Severity, a.Category, k.Code, a.Message(a.Count), a.Entities.ToArray());
        }
        _aggregates.Clear();
        _aggregateOrder.Clear();
        if (_suppressed > 0)
        {
            int n = _suppressed;
            _suppressed = 0;
            _issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Scene, MvrIssueCodes.Truncated, $"{n} further issues were not listed."));
        }
    }

    private sealed class AggregateEntry
    {
        public AggregateEntry(IssueSeverity severity, string category, Func<int, string> message)
        {
            Severity = severity;
            Category = category;
            Message = message;
        }

        public IssueSeverity Severity { get; }
        public string Category { get; }
        public Func<int, string> Message { get; }
        public int Count { get; set; }
        public List<Guid> Entities { get; } = new();
    }
}
