using System.Text.Json;
using Candela.Assets;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Project;

/// <summary>
/// Everything needed to write a project, captured on the main thread so that the archive can be written on a background
/// thread while the user keeps editing. Capturing serialises the scene to JSON (the only part that must touch the live
/// scene) and copies references to immutable data: asset entries, settings sections, GDTF byte arrays and the
/// thumbnail. Writing calls the GDTF provider of the capture options, on the writing thread.
/// </summary>
public sealed class ProjectSnapshot
{
    private ProjectSnapshot(ProjectData project, ProjectSaveOptions options, TimeProvider clock)
    {
        var scene = project.Scene;
        SceneJson = JsonSerializer.SerializeToUtf8Bytes(SceneSerializer.ToFile(scene), CandelaJson.Options);
        ProjectId = project.Id;
        SceneName = scene.Name;
        EntityCount = scene.Count;
        SceneVersion = scene.Version;
        CreatedUtc = project.CreatedUtc == default ? clock.GetUtcNow().UtcDateTime : DateTime.SpecifyKind(project.CreatedUtc, DateTimeKind.Utc);
        CapturedUtc = clock.GetUtcNow();
        Options = options;
        Settings = project.Settings.Snapshot();
        Assets = project.Assets.Entries;
        Thumbnail = project.Thumbnail;

        Gdtfs = ProjectData.CollectGdtfSpecs(scene);
        var known = new HashSet<Guid>();
        foreach (var a in Assets) known.Add(a.Id);
        var missing = new Dictionary<Guid, List<Guid>>();
        int fixtures = 0;
        foreach (var entity in scene.All())
        {
            foreach (var c in entity.Components)
            {
                Guid? asset = c switch
                {
                    MeshComponent m => m.ModelAssetId,
                    ImageComponent i => i.ImageAssetId,
                    TrussComponent t => t.ModelAssetId,
                    _ => null,
                };
                if (c is FixtureComponent) fixtures++;
                if (asset is not { } id || id == Guid.Empty || known.Contains(id)) continue;
                if (!missing.TryGetValue(id, out var users)) missing[id] = users = new List<Guid>();
                users.Add(entity.Id);
            }
        }
        FixtureCount = fixtures;
        MissingAssets = missing;

        // The project's own GDTF copies are the fallback when the provider has no file; copy the references now (the
        // dictionary belongs to the main thread, the byte arrays are immutable).
        var copies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in Gdtfs.Keys)
            if (project.FindGdtf(name) is { } bytes) copies[name] = bytes;
        GdtfCopies = copies;
    }

    /// <summary>Project identity (manifest <c>projectId</c>).</summary>
    public Guid ProjectId { get; }

    /// <summary>Scene name at capture time.</summary>
    public string SceneName { get; }

    /// <summary>Number of entities at capture time.</summary>
    public int EntityCount { get; }

    /// <summary>Number of entities with a fixture component.</summary>
    public int FixtureCount { get; }

    /// <summary><see cref="Candela.Core.SceneGraph.Scene.Version"/> at capture time (tells whether the scene changed since).</summary>
    public long SceneVersion { get; }

    /// <summary>Project creation time (UTC).</summary>
    public DateTime CreatedUtc { get; }

    /// <summary>When the snapshot was taken.</summary>
    public DateTimeOffset CapturedUtc { get; }

    /// <summary>Number of assets captured.</summary>
    public int AssetCount => Assets.Count;

    /// <summary>GDTF names used by the scene, in order of first use.</summary>
    public IReadOnlyList<string> GdtfNames => Gdtfs.Keys;

    internal byte[] SceneJson { get; }

    internal ProjectSaveOptions Options { get; }

    internal IReadOnlyDictionary<string, JsonElement> Settings { get; }

    internal IReadOnlyList<AssetEntry> Assets { get; }

    internal byte[]? Thumbnail { get; }

    internal ProjectData.GdtfUsage Gdtfs { get; }

    internal IReadOnlyDictionary<string, byte[]> GdtfCopies { get; }

    internal IReadOnlyDictionary<Guid, List<Guid>> MissingAssets { get; }

    /// <summary>
    /// Captures <paramref name="project"/> for writing. Call on the main thread (it reads the live scene); the result is
    /// immutable and may be written on any thread with <see cref="ProjectFile.Save(string, ProjectSnapshot)"/>.
    /// </summary>
    public static ProjectSnapshot Capture(ProjectData project, ProjectSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= ProjectSaveOptions.Default;
        return new ProjectSnapshot(project, options, options.TimeProvider);
    }

    /// <summary>Resolves the bytes stored for a GDTF name: provider, then the project's copy, then a GDTF asset of that name.</summary>
    internal byte[]? ResolveGdtf(string name)
    {
        if (Options.GdtfProvider?.Invoke(name) is { Length: > 0 } provided) return provided;
        if (GdtfCopies.TryGetValue(name, out var copy)) return copy;
        AssetEntry? caseInsensitive = null;
        foreach (var a in Assets)
        {
            if (a.Kind != AssetKind.Gdtf) continue;
            if (string.Equals(a.OriginalFileName, name, StringComparison.Ordinal)) return a.Data;
            if (caseInsensitive is null && string.Equals(a.OriginalFileName, name, StringComparison.OrdinalIgnoreCase)) caseInsensitive = a;
        }
        return caseInsensitive?.Data;
    }
}
