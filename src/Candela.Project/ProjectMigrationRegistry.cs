using System.Globalization;
using System.Text.Json.Nodes;

namespace Candela.Project;

/// <summary>
/// The documents of an archive written in an older format version, opened for conversion. A migration step changes
/// them in place from <see cref="FromVersion"/> to <c>FromVersion + 1</c>; after the last step the archive is read as
/// the current format. Archive entries can be read and replaced (for example when files move to another folder).
/// </summary>
public sealed class ProjectMigrationContext
{
    private readonly Func<string, byte[]?> _readEntry;
    private readonly Dictionary<string, byte[]?> _replaced;
    private readonly List<string> _notes = new();

    internal ProjectMigrationContext(JsonObject manifest, JsonNode? scene, JsonNode? settings, JsonNode? assetIndex, JsonNode? fixtureIndex,
        IReadOnlyCollection<string> entryNames, Func<string, byte[]?> readEntry, Dictionary<string, byte[]?> replaced)
    {
        Manifest = manifest;
        Scene = scene;
        Settings = settings;
        AssetIndex = assetIndex;
        FixtureIndex = fixtureIndex;
        EntryNames = entryNames;
        _readEntry = readEntry;
        _replaced = replaced;
    }

    /// <summary>Version the current step migrates from (the step produces <c>FromVersion + 1</c>).</summary>
    public int FromVersion { get; internal set; }

    /// <summary><c>manifest.json</c>. Its <c>formatVersion</c> is updated by the registry after each step.</summary>
    public JsonObject Manifest { get; }

    /// <summary><c>scene.json</c> (Core <c>SceneFile</c> layout in the current version), or null when absent.</summary>
    public JsonNode? Scene { get; set; }

    /// <summary><c>settings.json</c> (an object of named sections in the current version), or null when absent.</summary>
    public JsonNode? Settings { get; set; }

    /// <summary><c>assets/index.json</c>, or null when absent.</summary>
    public JsonNode? AssetIndex { get; set; }

    /// <summary><c>fixtures/index.json</c>, or null when absent.</summary>
    public JsonNode? FixtureIndex { get; set; }

    /// <summary>Names of all entries of the archive as stored.</summary>
    public IReadOnlyCollection<string> EntryNames { get; }

    /// <summary>Notes describing what the migrations changed (reported to the user as load notes).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Content of an archive entry (after earlier replacements), or null when it does not exist.</summary>
    public byte[]? ReadEntry(string name) => _replaced.TryGetValue(name, out var b) ? b : _readEntry(name);

    /// <summary>Replaces (or with null removes) an archive entry for the rest of the load. The file itself is not changed.</summary>
    public void ReplaceEntry(string name, byte[]? content)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _replaced[name] = content;
    }

    /// <summary>Adds a note for the user ("Gradient presets were converted to the new colour space").</summary>
    public void Note(string message)
    {
        if (!string.IsNullOrWhiteSpace(message)) _notes.Add(message);
    }
}

/// <summary>
/// Converts projects written in older format versions to the current one (<see cref="ProjectFile.FormatVersion"/>).
/// Each step converts one version to the next; loading a file of version <c>v</c> runs the steps
/// <c>v → v+1 → … → current</c>, and a file for which a step is missing is refused with a clear message. Files of a
/// newer version are never migrated: they are refused (an older program cannot know what they contain).
/// <para>
/// <see cref="Default"/> holds the built-in migrations and is read-only. Format version 1 is the first published format,
/// so it has no steps yet; when the format changes, <see cref="ProjectFile.FormatVersion"/> is incremented and a step
/// from the previous version is added to <see cref="CreateBuiltIn"/>.
/// </para>
/// </summary>
public sealed class ProjectMigrationRegistry
{
    private readonly SortedDictionary<int, Step> _steps = new();
    private readonly bool _readOnly;

    /// <summary>An empty registry (no migrations). Use <see cref="CreateBuiltIn"/> to start from the built-in steps.</summary>
    public ProjectMigrationRegistry()
    {
    }

    private ProjectMigrationRegistry(bool readOnly)
    {
        _readOnly = readOnly;
    }

    /// <summary>The built-in migrations (read-only; used when <see cref="ProjectLoadOptions.Migrations"/> is null).</summary>
    public static ProjectMigrationRegistry Default { get; } = Freeze(CreateBuiltIn());

    /// <summary>The version every migration chain ends at.</summary>
    public int TargetVersion => ProjectFile.FormatVersion;

    /// <summary>Registered steps as (from version, description), in version order.</summary>
    public IReadOnlyList<(int FromVersion, string Description)> Steps => _steps.Select(kv => (kv.Key, kv.Value.Description)).ToArray();

    /// <summary>A new, writable registry with the built-in migrations.</summary>
    public static ProjectMigrationRegistry CreateBuiltIn()
    {
        var registry = new ProjectMigrationRegistry();
        // Format 1 is the first format; steps "1 → 2", "2 → 3", … are registered here when the format changes.
        return registry;
    }

    /// <summary>
    /// Registers the step <paramref name="fromVersion"/> → <paramref name="fromVersion"/> + 1. Throws when the registry
    /// is read-only, when the step would not lead towards <see cref="TargetVersion"/>, or when it is already registered.
    /// </summary>
    public ProjectMigrationRegistry Register(int fromVersion, string description, Action<ProjectMigrationContext> migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentException.ThrowIfNullOrEmpty(description);
        if (_readOnly) throw new InvalidOperationException("The default migration registry is read-only; create one with CreateBuiltIn().");
        if (fromVersion < 0 || fromVersion >= TargetVersion)
            throw new ArgumentOutOfRangeException(nameof(fromVersion), $"A migration must start below the current format version {TargetVersion}.");
        if (!_steps.TryAdd(fromVersion, new Step(description, migrate)))
            throw new InvalidOperationException($"A migration from format version {fromVersion} is already registered.");
        return this;
    }

    /// <summary>True when a file of <paramref name="version"/> can be brought to the current version.</summary>
    public bool CanMigrate(int version)
    {
        if (version == TargetVersion) return true;
        if (version > TargetVersion || version < 0) return false;
        for (int v = version; v < TargetVersion; v++)
            if (!_steps.ContainsKey(v)) return false;
        return true;
    }

    /// <summary>Runs the steps from <paramref name="version"/> to the current version; returns their descriptions.</summary>
    internal IReadOnlyList<string> Migrate(int version, ProjectMigrationContext context, string? writtenBy)
    {
        if (!CanMigrate(version))
        {
            throw new ProjectVersionException(
                string.Create(CultureInfo.InvariantCulture,
                    $"This project uses format version {version}, which this version of Candela cannot convert (it reads format {TargetVersion}{(_steps.Count > 0 ? $" and converts from {_steps.Keys.Min()}" : "")})."),
                version, TargetVersion, writtenBy);
        }
        var done = new List<string>();
        for (int v = version; v < TargetVersion; v++)
        {
            var step = _steps[v];
            context.FromVersion = v;
            step.Migrate(context);
            context.Manifest["formatVersion"] = v + 1;
            done.Add(string.Create(CultureInfo.InvariantCulture, $"{v} → {v + 1}: {step.Description}"));
        }
        return done;
    }

    private static ProjectMigrationRegistry Freeze(ProjectMigrationRegistry source)
    {
        var frozen = new ProjectMigrationRegistry(readOnly: true);
        foreach (var (v, s) in source._steps) frozen._steps.Add(v, s);
        return frozen;
    }

    private sealed record Step(string Description, Action<ProjectMigrationContext> Migrate);
}
