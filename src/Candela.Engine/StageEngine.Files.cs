using System.Numerics;
using System.Security.Cryptography;
using System.Xml;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Candela.Mvr;
using Candela.Project;

namespace Candela.Engine;

/// <summary>How <see cref="StageEngine.Import"/> places what it brings in.</summary>
public sealed record EngineImportOptions
{
    /// <summary>Default options.</summary>
    public static EngineImportOptions Default { get; } = new();

    /// <summary>Axis and unit choices for 3D models (stored with the asset).</summary>
    public ImportOptions? Model { get; init; }

    /// <summary>How an image is shown: an unlit reference drawing (default) or a lit poster.</summary>
    public ImageMode ImageMode { get; init; } = ImageMode.Reference;

    /// <summary>Width of an imported image, m (its height follows the image's aspect ratio).</summary>
    public float ImageWidth { get; init; } = 4f;

    /// <summary>Where a model or image is placed (world transform of the new entity).</summary>
    public Transform Placement { get; init; } = Transform.Identity;

    /// <summary>Name of the group that holds a merged MVR or project (default: the file name).</summary>
    public string? GroupName { get; init; }
}

/// <summary>Where GDTF files added to the fixture library came from (see <see cref="StageEngine.FixtureLibraryChanged"/>).</summary>
public enum FixtureLibraryChangeSource
{
    /// <summary>The user imported a <c>.gdtf</c> file (<see cref="StageEngine.Import"/>).</summary>
    Import,

    /// <summary>GDTF files embedded in an MVR that was opened or imported.</summary>
    Mvr,

    /// <summary>GDTF copies stored in a Candela project that was opened, recovered or merged.</summary>
    Project,
}

/// <summary>GDTF files that were added to the engine's fixture library.</summary>
/// <param name="Added">The new library entries (at least one).</param>
/// <param name="Source">Where they came from.</param>
public sealed record FixtureLibraryChange(IReadOnlyList<FixtureLibraryEntry> Added, FixtureLibraryChangeSource Source);

public sealed partial class StageEngine
{
    /// <summary>File extension of MVR scenes.</summary>
    public const string MvrExtension = ".mvr";

    /// <summary>The project file the scene was opened from or last saved to, or null (new or imported scene).</summary>
    public string? ProjectPath { get; private set; }

    /// <summary>
    /// True when the project changed since it was created, opened or saved: the scene, an asset, or the engine/view
    /// settings (see <see cref="MarkViewSettingsChanged"/>).
    /// </summary>
    public bool IsModified { get; private set; }

    /// <summary>
    /// Raised on the main thread after GDTF files were added to <see cref="Library"/> (an imported <c>.gdtf</c>, GDTFs
    /// embedded in an MVR, a project's GDTF copies), after every fixture was re-resolved; the editor refreshes its library
    /// panel and selects the entry of an <see cref="FixtureLibraryChangeSource.Import"/>.
    /// </summary>
    public event Action<FixtureLibraryChange>? FixtureLibraryChanged;

    /// <summary>Replaces the scene with an empty one (assets, settings, undo and selection are cleared).</summary>
    public EngineResult New(string name = "Untitled")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResetProgramMessages();
        Settings = new EngineSettings();
        View = new ViewSettings();
        ProjectPath = null;
        ReplaceProject(new ProjectData(new Scene(string.IsNullOrWhiteSpace(name) ? "Untitled" : name)));
        RefreshIssues();
        return EngineResult.Ok($"New scene '{_scene.Name}'.");
    }

    /// <summary>
    /// Opens a file as the new scene: a Candela project (<c>.candela</c>) or an MVR scene (<c>.mvr</c>, which then has no
    /// project path until it is saved). The current scene is kept when the file cannot be read at all.
    /// </summary>
    public EngineResult Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path)) return Fail(EngineIssueCodes.OpenFailed, "No file was given.");
        if (!File.Exists(path)) return Fail(EngineIssueCodes.OpenFailed, $"'{path}' does not exist.");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            MvrExtension => OpenMvr(path),
            ProjectExtension => OpenProject(path),
            _ => Fail(EngineIssueCodes.OpenFailed, $"'{Path.GetFileName(path)}' is not a Candela project or an MVR file."),
        };
    }

    /// <summary>
    /// Brings a file into the open scene: an MVR (merged under a new group), another Candela project (merged), a 3D
    /// model (OBJ, FBX, DAE, 3DS, STL, PLY, glTF/GLB — native formats imported in the isolated worker), an image
    /// (reference drawing or poster) or a GDTF file (added to the fixture library). Undoable.
    /// </summary>
    public EngineResult Import(string path, EngineImportOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= EngineImportOptions.Default;
        if (string.IsNullOrWhiteSpace(path)) return Fail(EngineIssueCodes.ImportFailed, "No file was given.");
        if (!File.Exists(path)) return Fail(EngineIssueCodes.ImportFailed, $"'{path}' does not exist.");
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == MvrExtension) return ImportMvr(path, options);
        if (ext == ProjectExtension) return MergeProject(path, options);
        if (ext == ".gdtf") return ImportGdtf(path);
        if (AssetFormats.GetModelFormat(path) == ModelFormat.SketchUp)
            return Fail(EngineIssueCodes.ImportFailed, AssetFormats.SketchUpMessage, IssueCategory.Scene);
        if (AssetFormats.IsModelFile(path)) return ImportModel(path, options);
        if (AssetFormats.IsImageFile(path)) return ImportImage(path, options);
        return Fail(EngineIssueCodes.ImportFailed, $"'{name}' is not a file Candela can import (MVR, Candela project, 3D model, image or GDTF).",
            IssueCategory.Scene);
    }

    /// <summary>
    /// Writes the scene as an MVR 1.6 file with the GDTF files of its fixtures (from the library) and its 3D models
    /// (as GLB).
    /// </summary>
    public EngineResult ExportMvr(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path)) return Fail(EngineIssueCodes.ExportFailed, "No file was given.");
        try
        {
            var options = new MvrExportOptions
            {
                GdtfProvider = MvrExportOptions.GdtfFromLibrary(Library),
                ModelProvider = Models.GetModelData,
            };
            var result = MvrWriter.WriteFile(_scene, path, options);
            var issues = Sorted(result.Issues);
            Report(issues);
            return new EngineResult(true, $"Exported '{Path.GetFileName(path)}': {result.ObjectCount} objects{Counts(issues)}.", issues, Array.Empty<Guid>());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(EngineIssueCodes.ExportFailed, $"'{Path.GetFileName(path)}' could not be written: {e.Message}");
        }
    }

    private EngineResult OpenMvr(string path)
    {
        MvrImportResult result;
        try
        {
            result = MvrImporter.ImportFile(path, new MvrImportOptions { Library = Library });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Fail(EngineIssueCodes.OpenFailed, $"'{Path.GetFileName(path)}' could not be opened: {e.Message}");
        }

        ResetProgramMessages();
        Settings = new EngineSettings();
        View = new ViewSettings();
        ProjectPath = null;
        ReplaceProject(new ProjectData(result.Scene));
        var extra = new List<Issue>();
        RegisterMvr(result, extra);
        RecordMvrSubstitutes(result);
        IsModified = false;
        var issues = Sorted(result.Issues.Concat(extra));
        Report(issues);
        RefreshIssues();
        int fixtures = result.AllEntities().Count(e => e.Has<FixtureComponent>());
        return new EngineResult(true, $"Opened '{Path.GetFileName(path)}': {fixtures} fixtures, {result.Assets.Count} models{Counts(issues)}.",
            issues, result.RootEntities.Select(e => e.Id).ToArray());
    }

    private EngineResult ImportMvr(string path, EngineImportOptions options)
    {
        MvrDocument document;
        try
        {
            document = MvrReader.ReadFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Fail(EngineIssueCodes.ImportFailed, $"'{Path.GetFileName(path)}' could not be imported: {e.Message}", IssueCategory.Scene);
        }
        var result = MvrImporter.Merge(_scene, document, new MvrImportOptions
        {
            Library = Library,
            GroupName = options.GroupName ?? Path.GetFileNameWithoutExtension(path),
        });
        var extra = new List<Issue>();
        RegisterMvr(result, extra);
        RecordMvrSubstitutes(result);
        var roots = result.RootEntities.ToList();
        if (roots.Count > 0) Undo.Execute(new ImportedEntitiesCommand(_scene, roots, $"Import {Path.GetFileName(path)}"));
        Selection.Set(roots.Select(r => r.Id));
        var issues = Sorted(result.Issues.Concat(extra));
        Report(issues);
        RefreshIssues();
        int fixtures = result.AllEntities().Count(e => e.Has<FixtureComponent>());
        return new EngineResult(true, $"Imported '{Path.GetFileName(path)}': {fixtures} fixtures, {result.Assets.Count} models{Counts(issues)}.",
            issues, roots.Select(r => r.Id).ToArray());
    }

    private EngineResult ImportModel(string path, EngineImportOptions options)
    {
        AssetEntry entry;
        IReadOnlyList<Issue> found;
        try
        {
            entry = Models.Import(path, options.Model, out found);
        }
        catch (AssetImportException e)
        {
            var code = e.Message.Contains("could not be imported safely", StringComparison.Ordinal) ? EngineIssueCodes.ImportUnsafe : EngineIssueCodes.ImportFailed;
            return Fail(code, e.Message.StartsWith('\'') ? e.Message : $"'{Path.GetFileName(path)}': {e.Message}", IssueCategory.Scene);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(EngineIssueCodes.ImportFailed, $"'{Path.GetFileName(path)}' could not be read: {e.Message}", IssueCategory.Scene);
        }

        var entity = new Entity(Path.GetFileNameWithoutExtension(path)) { Local = options.Placement };
        entity.Add(new MeshComponent { ModelAssetId = entry.Id });
        Undo.Execute(new AddEntityCommand(_scene, entity));
        Selection.Set(entity.Id);
        var issues = Sorted(found);
        Report(issues);
        RefreshIssues();
        return new EngineResult(true, $"Imported model '{entry.OriginalFileName}'{Counts(issues)}.", issues, new[] { entity.Id });
    }

    private EngineResult ImportImage(string path, EngineImportOptions options)
    {
        AssetEntry entry;
        int before = Assets.Count;
        try
        {
            entry = Assets.ImportImage(path);
        }
        catch (AssetImportException e)
        {
            return Fail(EngineIssueCodes.ImportFailed, $"'{Path.GetFileName(path)}': {e.Message}", IssueCategory.Scene);
        }
        float width = float.IsFinite(options.ImageWidth) && options.ImageWidth > 0 ? options.ImageWidth : 4f;
        var size = new Vector2(width, width * 0.75f);
        if (Assets.TryGetImage(entry.Id, out var image, out var error))
        {
            size = ImagePlaneMesh.SizeForAspect(image.Width, image.Height, width);
        }
        else
        {
            // take back the asset this failed import added (nothing else changed)
            bool modified = IsModified;
            if (Assets.Count > before) Assets.Remove(entry.Id);
            IsModified = modified;
            return Fail(EngineIssueCodes.ImportFailed, $"'{Path.GetFileName(path)}' cannot be decoded: {error}", IssueCategory.Scene);
        }
        var entity = new Entity(Path.GetFileNameWithoutExtension(path)) { Local = options.Placement };
        entity.Add(new ImageComponent
        {
            ImageAssetId = entry.Id,
            Mode = options.ImageMode,
            Width = size.X,
            Height = size.Y,
            Opacity = options.ImageMode == ImageMode.Reference ? 0.6f : 1f,
        });
        Undo.Execute(new AddEntityCommand(_scene, entity));
        Selection.Set(entity.Id);
        RefreshIssues();
        return new EngineResult(true, $"Imported image '{entry.OriginalFileName}' ({size.X:0.##} × {size.Y:0.##} m).", Array.Empty<Issue>(), new[] { entity.Id });
    }

    /// <summary>
    /// Adds a GDTF file to the fixture library after reading it completely: a file that is not a GDTF archive, has no
    /// readable <c>description.xml</c> or defines no DMX mode is refused with an issue, so the library never lists an
    /// entry that cannot be placed. The result carries the new (or already present) entry in
    /// <see cref="EngineResult.LibraryEntry"/>, and <see cref="FixtureLibraryChanged"/> is raised for a new one.
    /// </summary>
    private EngineResult ImportGdtf(string path)
    {
        string name = Path.GetFileName(path);
        if (Library.FindByFileName(name) is { } existing)
            return EngineResult.Ok($"'{name}' is already in the fixture library.") with { LibraryEntry = existing };

        FixtureType type;
        try
        {
            type = GdtfReader.ReadFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or XmlException
            or ArgumentException or NotSupportedException or FormatException or OverflowException or InvalidOperationException)
        {
            return Fail(EngineIssueCodes.ImportFailed, $"'{name}' is not a readable GDTF file: {e.Message}", IssueCategory.Fixture);
        }
        if (type.DmxModes.Count == 0)
            return Fail(EngineIssueCodes.ImportFailed, $"'{name}' defines no DMX mode, so its fixture cannot be patched; it was not added to the fixture library.",
                IssueCategory.Fixture);

        int added = Library.AddFiles(new[] { path });
        var entry = Library.FindByFileName(name);
        if (added == 0 || entry is null)
            return Fail(EngineIssueCodes.ImportFailed, $"'{name}' could not be added to the fixture library.", IssueCategory.Fixture);
        ReloadFixtureTypes();
        var issues = new List<Issue>();
        if (type.Warnings.Count > 0)
        {
            issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Fixture, EngineIssueCodes.GdtfWarnings,
                $"'{name}' was read with {type.Warnings.Count} warning{(type.Warnings.Count == 1 ? "" : "s")} (defaults used), e.g. {type.Warnings[0]}."));
        }
        Report(issues);
        RefreshIssues();
        FixtureLibraryChanged?.Invoke(new FixtureLibraryChange(new[] { entry }, FixtureLibraryChangeSource.Import));
        string modes = type.DmxModes.Count == 1 ? "1 mode" : $"{type.DmxModes.Count} modes";
        var result = new EngineResult(true, $"Added '{name}' ({type.Manufacturer} {type.Name}, {modes}) to the fixture library.", issues, Array.Empty<Guid>());
        return result with { LibraryEntry = entry };
    }

    /// <summary>
    /// Adds GDTF files extracted from an MVR or a project to the library, re-resolves every fixture when any was added and
    /// raises <see cref="FixtureLibraryChanged"/> with the new entries.
    /// </summary>
    private void AddGdtfsToLibrary(List<string> paths, FixtureLibraryChangeSource source)
    {
        if (paths.Count == 0 || Library.AddFiles(paths) == 0) return;
        ReloadFixtureTypes();
        if (FixtureLibraryChanged is not { } handler) return;
        var entries = new List<FixtureLibraryEntry>(paths.Count);
        foreach (var file in paths)
        {
            if (Library.FindByFileName(Path.GetFileName(file)) is { } entry
                && string.Equals(entry.FilePath, Path.GetFullPath(file), StringComparison.Ordinal))
                entries.Add(entry);
        }
        if (entries.Count > 0) handler(new FixtureLibraryChange(entries, source));
    }

    /// <summary>
    /// Makes the GDTF files and 3D models an MVR import brought in available: GDTFs embedded in the MVR that the library
    /// does not have are extracted to the cache and added to the library; models become assets (decoded with the MVR
    /// conventions: 3DS in millimetres, Z up; glTF Y up).
    /// </summary>
    private void RegisterMvr(MvrImportResult result, List<Issue> issues)
    {
        var paths = new List<string>();
        foreach (var gdtf in result.Gdtfs)
        {
            if (gdtf.Source is not (MvrGdtfSource.Embedded or MvrGdtfSource.EmbeddedStub) || gdtf.EmbeddedBytes is not { Length: > 0 } bytes) continue;
            if (Library.FindByFileName(gdtf.FileName) is not null) continue;
            if (ExtractGdtf(gdtf.FileName, bytes, issues) is { } file) paths.Add(file);
        }
        AddGdtfsToLibrary(paths, FixtureLibraryChangeSource.Mvr);

        foreach (var asset in result.Assets)
        {
            if (Assets.Contains(asset.Id)) continue;
            var options = asset.Format == ModelFileFormat.ThreeDs
                ? new ImportOptions { UpAxis = SourceUpAxis.Z, UnitScale = 0.001f, GuessUnits = false }
                : new ImportOptions { UpAxis = SourceUpAxis.Y, UnitScale = 1f, GuessUnits = false };
            AssetEntry entry;
            try
            {
                entry = Assets.Add(AssetKind.Model, asset.FileName, asset.Bytes, asset.AdditionalFiles, id: asset.Id, importOptions: options);
            }
            catch (Exception e) when (e is AssetImportException or InvalidOperationException)
            {
                issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, EngineIssueCodes.AssetUnreadable,
                    $"The MVR model '{asset.FileName}' could not be stored: {e.Message}"));
                continue;
            }
            try
            {
                Models.Seed(entry, asset.Decode(), asset.AdditionalFiles);
            }
            catch (InvalidDataException)
            {
                // The MVR importer already reported the undecodable geometry; the asset decodes again on demand.
            }
        }
    }

    /// <summary>Writes a GDTF into the engine's cache (content-addressed folder) and returns its path, or null.</summary>
    internal string? ExtractGdtf(string fileName, byte[] bytes, List<Issue> issues)
    {
        string name = Path.GetFileName(fileName.Replace('\\', '/'));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "fixture.gdtf";
        if (!name.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase)) name += ".gdtf";
        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        string path = Path.Combine(CacheDirectory, "gdtf", hash, name);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Fixture, EngineIssueCodes.GdtfMissing,
                $"The GDTF '{name}' could not be written to the cache ({e.Message}); its fixtures cannot be evaluated."));
            return null;
        }
    }

    private EngineResult Fail(string code, string message, string category = IssueCategory.Program)
    {
        var result = EngineResult.Fail(code, message, category);
        Report(result.Issues);
        return result;
    }

    private void Report(IEnumerable<Issue> issues)
    {
        foreach (var issue in issues) Issues.Report(issue);
    }

    private static IReadOnlyList<Issue> Sorted(IEnumerable<Issue> issues) =>
        issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Category, StringComparer.Ordinal).ToList();

    private static string Counts(IReadOnlyList<Issue> issues)
    {
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        var parts = new List<string>();
        if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
        if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
        return parts.Count > 0 ? ", " + string.Join(", ", parts) : "";
    }

    /// <summary>
    /// Undo record for entities an importer already added to the scene: the first <see cref="Do"/> is a no-op, undo
    /// removes them, redo adds them back.
    /// </summary>
    private sealed class ImportedEntitiesCommand(Scene scene, IReadOnlyList<Entity> roots, string description) : IUndoableCommand
    {
        private readonly (Entity? Parent, int Index)[] _places = new (Entity?, int)[roots.Count];
        private bool _done = true;

        public string Description => description;

        public void Do()
        {
            if (_done) return;
            for (int i = 0; i < roots.Count; i++)
            {
                var (parent, index) = _places[i];
                scene.Add(roots[i], parent?.Scene == scene ? parent : null, index);
            }
            _done = true;
        }

        public void Undo()
        {
            if (!_done) return;
            for (int i = roots.Count - 1; i >= 0; i--)
                if (roots[i].Scene == scene) _places[i] = scene.Remove(roots[i]);
            _done = false;
        }
    }
}
