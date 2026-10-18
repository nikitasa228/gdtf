using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

public sealed class AtomicSaveTests
{
    [Fact]
    public void Failing_write_keeps_the_previous_file_untouched()
    {
        using var dir = new TempDir();
        var path = dir.File("show.candela");
        var project = TestData.RichProject();
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var before = File.ReadAllBytes(path);
        var savedManifest = project.Manifest;

        project.Scene.Add(new Entity("Change that must not reach the file"));
        var calls = 0;
        var e = Assert.Throws<IOException>(() => ProjectFile.Save(path, project, new ProjectSaveOptions
        {
            GdtfProvider = name => ++calls == 2 ? throw new IOException("library disk unplugged") : TestData.LibraryGdtf(name),
        }));
        Assert.Equal("library disk unplugged", e.Message);
        Assert.Equal(2, calls);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new[] { "show.candela" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName));
        Assert.Same(savedManifest, project.Manifest);
        var reloaded = ProjectFile.Load(path);
        Assert.Empty(reloaded.LoadIssues);
        Assert.DoesNotContain(reloaded.Scene.All(), x => x.Name.StartsWith("Change", StringComparison.Ordinal));
    }

    [Fact]
    public void Failing_first_save_creates_no_file()
    {
        using var dir = new TempDir();
        var project = new ProjectData(new Scene("New"));
        var fixture = new Entity("Fixture");
        fixture.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA });
        project.Scene.Add(fixture);
        Assert.Throws<InvalidOperationException>(() => ProjectFile.Save(dir.File("new.candela"), project,
            new ProjectSaveOptions { GdtfProvider = _ => throw new InvalidOperationException("boom") }));
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Null(project.FilePath);
    }

    [Fact]
    public void Save_creates_missing_folders_and_overwrites_atomically()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "nested", "deeper", "show.candela");
        var project = new ProjectData(new Scene("First"));
        ProjectFile.Save(path, project);
        project.Scene.Name = "Second";
        ProjectFile.Save(path, project);
        Assert.Equal("Second", ProjectFile.ReadManifest(path).SceneName);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Created_time_survives_saves_and_modified_time_follows_the_clock()
    {
        using var dir = new TempDir();
        var clock = new ManualClock();
        var project = new ProjectData(new Scene("Times")) { CreatedUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        var path = dir.File("t.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { TimeProvider = clock });
        clock.Advance(TimeSpan.FromHours(5));
        var second = ProjectFile.Save(path, ProjectFile.Load(path), new ProjectSaveOptions { TimeProvider = clock });
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), second.Manifest.CreatedUtc);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, second.Manifest.ModifiedUtc);
        Assert.Equal(second.Manifest, ProjectFile.ReadManifest(path));
    }
}
