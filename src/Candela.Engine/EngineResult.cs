using Candela.Core.Diagnostics;
using Candela.Gdtf;

namespace Candela.Engine;

/// <summary>
/// Outcome of a <see cref="StageEngine"/> operation (open, save, import, export…). Operations never throw for content
/// problems: a damaged file, a missing GDTF or an unsupported format is reported here and in the engine's
/// <see cref="StageEngine.Issues"/>. <see cref="Success"/> is false only when the operation did not happen at all
/// (nothing was loaded, saved or imported).
/// </summary>
/// <param name="Success">True when the operation took place (possibly with warnings).</param>
/// <param name="Summary">One line for the status bar or the CLI ("Imported 'show.mvr': 91 fixtures, 2 warnings").</param>
/// <param name="Issues">Problems found while doing it, most severe first.</param>
/// <param name="Entities">Entities created by the operation (import, open), for selection and undo.</param>
public sealed record EngineResult(bool Success, string Summary, IReadOnlyList<Issue> Issues, IReadOnlyList<Guid> Entities)
{
    /// <summary>
    /// The fixture-library entry the operation added or found (import of a <c>.gdtf</c> file), so the editor can select
    /// it in the library; null for other operations.
    /// </summary>
    public FixtureLibraryEntry? LibraryEntry { get; init; }

    /// <summary>A successful result without issues or entities.</summary>
    public static EngineResult Ok(string summary) => new(true, summary, Array.Empty<Issue>(), Array.Empty<Guid>());

    /// <summary>A failed result with one error issue.</summary>
    public static EngineResult Fail(string code, string message, string category = IssueCategory.Program) =>
        new(false, message, new[] { new Issue(IssueSeverity.Error, category, code, message) }, Array.Empty<Guid>());

    /// <summary>Number of issues of at least <paramref name="severity"/>.</summary>
    public int Count(IssueSeverity severity) => Issues.Count(i => i.Severity >= severity);

    /// <inheritdoc />
    public override string ToString() => Summary;
}

/// <summary>Stable issue codes raised by the engine itself (the modules have their own: <c>patch.*</c>, <c>power.*</c>, <c>mvr.*</c> …).</summary>
public static class EngineIssueCodes
{
    /// <summary>A file could not be opened or read at all.</summary>
    public const string OpenFailed = "engine.open-failed";

    /// <summary>A project could not be written.</summary>
    public const string SaveFailed = "engine.save-failed";

    /// <summary>A file could not be imported (unsupported or damaged).</summary>
    public const string ImportFailed = "engine.import-failed";

    /// <summary>A model import crashed or timed out in the isolated import worker.</summary>
    public const string ImportUnsafe = "engine.import-unsafe";

    /// <summary>The import worker was not found, so a model was imported inside the application process.</summary>
    public const string ImportInProcess = "engine.import-in-process";

    /// <summary>An export failed.</summary>
    public const string ExportFailed = "engine.export-failed";

    /// <summary>An entity refers to a model or image asset the project does not contain.</summary>
    public const string AssetMissing = "engine.asset-missing";

    /// <summary>A model or image asset exists but cannot be decoded.</summary>
    public const string AssetUnreadable = "engine.asset-unreadable";

    /// <summary>A fixture's GDTF is not available (neither in the library nor in the project).</summary>
    public const string GdtfMissing = "engine.gdtf-missing";

    /// <summary>A fixture's GDTF was found, but its 3D models could not be loaded (drawn as a placeholder).</summary>
    public const string FixtureModelFailed = "engine.fixture-model-failed";

    /// <summary>The scene holds several atmosphere volumes; only the first is simulated.</summary>
    public const string ExtraAtmosphere = "engine.extra-atmosphere";

    /// <summary>A part of a recording could not be read.</summary>
    public const string RecordingDamaged = "engine.recording-damaged";

    /// <summary>A section of the engine/view settings in a project could not be read (defaults used).</summary>
    public const string SettingsInvalid = "engine.settings-invalid";

    /// <summary>The fixture library has no folders or the folders do not exist.</summary>
    public const string LibraryMissing = "engine.library-missing";

    /// <summary>Fixtures whose field angle is wider than the renderer's spot cone (170°) are drawn with 170°.</summary>
    public const string FieldClamped = "engine.field-clamped";

    /// <summary>Fixtures that stood in for a missing model were switched to its GDTF, which the library now has.</summary>
    public const string SubstituteReplaced = "engine.substitute-replaced";

    /// <summary>An imported GDTF file was read with warnings (unknown values, dangling references; defaults used).</summary>
    public const string GdtfWarnings = "engine.gdtf-warnings";
}
