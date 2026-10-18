using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Candela.Mvr;

namespace Candela.Project.Tests;

/// <summary>
/// The repository's real scene: <c>stages/Фокина.mvr</c> (Depence export, 91 fixtures) imported with the fixture library,
/// saved as a project with copies of the library GDTFs, loaded back and merged into itself.
/// </summary>
public sealed class FokinaProjectTests
{
    private static readonly Lazy<FixtureLibrary> Library = new(() => FixtureLibrary.Scan(TestData.RepoRoot));

    private static byte[]? FromLibrary(string name) => Library.Value.FindByFileName(name) is { } e ? File.ReadAllBytes(e.FilePath) : null;

    [Fact]
    public void Real_mvr_scene_round_trips_with_its_gdtf_copies_and_merges_into_itself()
    {
        var import = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = Library.Value });
        var project = new ProjectData(import.Scene);
        Assert.Equal(91, project.Scene.Query<FixtureComponent>().Count());
        var used = project.UsedGdtfSpecs();
        Assert.Equal(4, used.Count);

        using var dir = new TempDir();
        var path = dir.File("Фокина.candela");
        var saved = ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = FromLibrary });
        Assert.Empty(saved.Issues);
        Assert.Equal(used, saved.StoredGdtfs);
        Assert.Equal(91, saved.Manifest.FixtureCount);
        Assert.Equal("Фокина", saved.Manifest.SceneName);

        // Opened "on another machine": no library, the GDTFs come from the project.
        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));
        foreach (var name in used) Assert.Equal(FromLibrary(name), loaded.FindGdtf(name));
        Assert.Equal(new[] { "Default", "Light" }, loaded.Scene.Layers.Select(l => l.Name));

        // Merge into itself: a second copy of the rig with new ids, same GDTFs, no collisions.
        var result = ProjectMerge.Import(loaded, loaded);
        Assert.Empty(result.Issues);
        Assert.Equal(91 * 2 + 1, loaded.Scene.Count);
        Assert.Equal(loaded.Scene.Count, loaded.Scene.All().Select(e => e.Id).Distinct().Count());
        Assert.Empty(result.AddedGdtfs);
        Assert.Empty(result.AddedLayers);
        var copies = result.Group.SelfAndDescendants().Select(e => e.Get<FixtureComponent>()).OfType<FixtureComponent>().ToList();
        Assert.Equal(91, copies.Count);
        Assert.All(copies, f => Assert.Contains(f.GdtfSpec, used));

        var merged = dir.File("merged.candela");
        ProjectFile.Save(merged, loaded);
        var reloaded = ProjectFile.Load(merged);
        Assert.Empty(reloaded.LoadIssues);
        Assert.Equal(182, reloaded.Scene.Query<FixtureComponent>().Count());
        Assert.Equal(4, reloaded.GdtfFiles.Count);
    }
}
