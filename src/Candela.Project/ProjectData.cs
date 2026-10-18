using System.Text.Json;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project;

/// <summary>
/// An open project in memory: the scene, the asset store, the project settings sections, the GDTF files kept with the
/// project and the thumbnail. <see cref="ProjectFile.Save(string, ProjectData, ProjectSaveOptions?)"/> writes it as a
/// <c>.candela</c> archive and <see cref="ProjectFile.Load(string, ProjectLoadOptions?)"/> reads it back.
/// <para>
/// Like the scene, a project belongs to the main thread. <see cref="ProjectFile.Load(string, ProjectLoadOptions?)"/> may
/// run on a background thread because it builds a new, unshared instance; saving in the background goes through a
/// <see cref="ProjectSnapshot"/> captured on the main thread.
/// </para>
/// </summary>
public sealed class ProjectData
{
    /// <summary>A new, empty project ("Untitled" scene, empty asset store).</summary>
    public ProjectData()
        : this(new Scene())
    {
    }

    /// <summary>A project around an existing scene and (optionally) an existing asset store.</summary>
    public ProjectData(Scene scene, AssetStore? assets = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Scene = scene;
        Assets = assets ?? new AssetStore();
        Id = scene.Id;
        CreatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Stable identity of the project, stored in the manifest. It names the autosave files and survives Save as; a merge
    /// import does not change it. Defaults to the scene id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>The scene (entities, layers, scene settings).</summary>
    public Scene Scene { get; }

    /// <summary>Imported models, images, textures and GDTF assets with their original bytes.</summary>
    public AssetStore Assets { get; }

    /// <summary>Named JSON sections of project settings owned by higher layers (view, preview, render, overlays, layout…).</summary>
    public ProjectSettings Settings { get; } = new();

    /// <summary>
    /// GDTF files kept with the project, keyed exactly by the name <see cref="FixtureComponent.GdtfSpec"/> uses. Filled by
    /// loading (the archive's <c>fixtures/</c> folder) and by merge imports; consulted by saving when the GDTF provider
    /// callback cannot supply a file, so a project opened without the fixture library keeps its fixtures when saved
    /// again. Byte arrays are shared and must not be modified.
    /// </summary>
    public Dictionary<string, byte[]> GdtfFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Components of types this version does not know (written by a newer version), as raw JSON by entity id. Loading keeps
    /// them and saving writes them back for every entity that still exists, so opening and saving a newer project here
    /// does not destroy them; this version cannot show, change or merge them. Entities without such components are absent.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<JsonElement>> UnknownComponents => UnknownComponentsMutable;

    internal Dictionary<Guid, IReadOnlyList<JsonElement>> UnknownComponentsMutable { get; } = new();

    /// <summary>PNG preview of the project (shown by Open dialogs), or null.</summary>
    public byte[]? Thumbnail { get; set; }

    /// <summary>When the project was created (UTC); kept in the manifest across saves.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>The file the project was loaded from or last saved to (full path), or null for an unsaved project.</summary>
    public string? FilePath { get; set; }

    /// <summary>The manifest read from the file, or null for a project that was never loaded.</summary>
    public ProjectManifest? Manifest { get; internal set; }

    /// <summary>Problems found while loading (damaged assets, missing GDTF files, repaired values, migrations).</summary>
    public IReadOnlyList<Issue> LoadIssues { get; internal set; } = Array.Empty<Issue>();

    /// <summary>
    /// The GDTF bytes kept for <paramref name="gdtfSpec"/>: an exact match of the name first, then a case-insensitive one.
    /// Null when the project holds no copy.
    /// </summary>
    public byte[]? FindGdtf(string gdtfSpec)
    {
        if (string.IsNullOrEmpty(gdtfSpec)) return null;
        if (GdtfFiles.TryGetValue(gdtfSpec, out var bytes)) return bytes;
        foreach (var (name, data) in GdtfFiles)
            if (string.Equals(name, gdtfSpec, StringComparison.OrdinalIgnoreCase)) return data;
        return null;
    }

    /// <summary>Distinct non-empty GDTF names used by the scene's fixtures, in scene order.</summary>
    public IReadOnlyList<string> UsedGdtfSpecs() => CollectGdtfSpecs(Scene).Keys;

    internal static GdtfUsage CollectGdtfSpecs(Scene scene)
    {
        var usage = new GdtfUsage();
        foreach (var (entity, fixture) in scene.Query<FixtureComponent>())
            if (!string.IsNullOrEmpty(fixture.GdtfSpec)) usage.Add(fixture.GdtfSpec, entity.Id);
        return usage;
    }

    /// <summary>Distinct GDTF names in order of first use, with the entities that use each.</summary>
    internal sealed class GdtfUsage
    {
        private readonly Dictionary<string, List<Guid>> _byName = new(StringComparer.Ordinal);
        private readonly List<string> _order = new();

        public IReadOnlyList<string> Keys => _order;

        public IReadOnlyList<Guid> EntitiesOf(string name) => _byName.TryGetValue(name, out var l) ? l : Array.Empty<Guid>();

        public void Add(string name, Guid entity)
        {
            if (!_byName.TryGetValue(name, out var list))
            {
                list = new List<Guid>();
                _byName[name] = list;
                _order.Add(name);
            }
            list.Add(entity);
        }
    }
}
