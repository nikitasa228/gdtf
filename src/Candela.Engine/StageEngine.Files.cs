using System.Numerics;
using System.Security.Cryptography;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
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

public sealed partial class StageEngine
{
    /// <summary>File extension of MVR scenes.</summary>
    public const string MvrExtension = ".mvr";

    /// <summary>The project file the scene was opened from or last saved to, or null (new or imported scene).</summary>
    public string? ProjectPath { get; private set; }

    /// <summary>True when the scene changed since it was created, opened or saved.</summary>
    public bool IsModified { get; private set; }

    /// <summary>Replaces the scene with an empty one (assets, settings, undo and selection are cleared).</summary>
    public EngineResult New(string name = "Untitled")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Issues.ClearProgramMessages();
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

        Issues.ClearProgramMessages();
        Settings = new EngineSettings();
        View = new ViewSettings();
        ProjectPath = null;
        ReplaceProject(new ProjectData(result.Scene));
        var extra = new List<Issue>();
        RegisterMvr(result, extra);
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
            if (Assets.Count > before) Assets.Remove(entry.Id);
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

    private EngineResult ImportGdtf(string path)
    {
        string name = Path.GetFileName(path);
        if (Library.FindByFileName(name) is not null) return EngineResult.Ok($"'{name}' is already in the fixture library.");
        int added = Library.AddFiles(new[] { path });
        if (added == 0) return Fail(EngineIssueCodes.ImportFailed, $"'{name}' could not be added to the fixture library.", IssueCategory.Fixture);
        ReloadFixtureTypes();
        RefreshIssues();
        return EngineResult.Ok($"Added '{name}' to the fixture library.");
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
        if (paths.Count > 0 && Library.AddFiles(paths) > 0) ReloadFixtureTypes();

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
