using Candela.Core.Diagnostics;
using Candela.Core.Settings;
using Candela.Gdtf;

namespace Candela.Engine.Tests;

/// <summary>
/// The installed layout (docs/install.md): resources found relative to the program folder — also one with spaces and
/// Cyrillic letters, outside the repository —, the lookup order of library folders and researched specs, the starter
/// fixtures that let the sample scene open without a library, and library folders added while the program runs.
/// </summary>
public sealed class InstallLayoutTests : IDisposable
{
    /// <summary>The GDTF files the sample scene resolves to (packaging/windows/starter-fixtures.txt).</summary>
    private static readonly string[] StarterFixtures =
    [
        "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf",
        "Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf",
        "Ayrton@Rivale_Wash@V1.8_Correct_Gobo_Value.gdtf",
        "Martin_Professional@VDO_Atomic_Bold@20260620.gdtf",
    ];

    // Outside the repository on purpose: walking up from these folders must not find the repository's _specs.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "candela-install-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>A program folder like the installer's, at a path with spaces and Cyrillic letters.</summary>
    private string MakeInstalledProgram(bool withStarters = true)
    {
        string program = Path.Combine(_root, "Program Files", "Свет тест", "Candela");
        string specs = Path.Combine(program, "data", "specs");
        Directory.CreateDirectory(specs);
        File.Copy(Path.Combine(TestEnvironment.Root, FixtureSpecsIndex.DefaultRelativePath), Path.Combine(specs, "fixtures_index.json"));
        string samples = Path.Combine(program, "samples");
        Directory.CreateDirectory(Path.Combine(samples, "fixtures"));
        File.Copy(TestEnvironment.Fokina, Path.Combine(samples, InstallLayout.SampleSceneFileName));
        if (withStarters)
        {
            foreach (var name in StarterFixtures)
                File.Copy(Path.Combine(TestEnvironment.Root, name), Path.Combine(samples, "fixtures", name));
        }

        Directory.CreateDirectory(Path.Combine(program, "docs"));
        return program;
    }

    private string MakeFolder(params string[] parts)
    {
        string folder = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void An_installed_copy_finds_its_files_next_to_the_program()
    {
        string program = MakeInstalledProgram();
        var layout = new InstallLayout(program, null);

        Assert.True(layout.IsInstalled);
        Assert.Equal(Path.Combine(program, "data", "specs", "fixtures_index.json"), layout.InstalledSpecsIndex);
        Assert.Equal(Path.Combine(program, "samples", "fixtures"), layout.StarterFixturesFolder);
        Assert.Equal(Path.Combine(program, "samples", "Фокина.mvr"), layout.SampleScene);
        Assert.Equal(Path.Combine(program, "docs"), layout.DocsFolder);
    }

    [Fact]
    public void The_development_layout_uses_the_repository()
    {
        var layout = new InstallLayout(MakeFolder("bin"), TestEnvironment.Root);

        Assert.False(layout.IsInstalled);
        Assert.Null(layout.StarterFixturesFolder);
        Assert.Equal(TestEnvironment.Fokina, layout.SampleScene);
        Assert.Equal(Path.Combine(TestEnvironment.Root, "docs"), layout.DocsFolder);
        Assert.Equal(new[] { TestEnvironment.Root }, layout.LibraryFoldersFor(null));
        Assert.Equal(Path.Combine(TestEnvironment.Root, FixtureSpecsIndex.DefaultRelativePath), layout.FindSpecsIndex(Array.Empty<string>()));

        // The test run itself is the development layout: the program is not installed and the repository is found.
        Assert.False(InstallLayout.Current.IsInstalled);
        Assert.Equal(TestEnvironment.Root, InstallLayout.Current.RepositoryRoot);
    }

    [Fact]
    public void Library_folders_are_the_configured_ones_then_the_starter_fixtures()
    {
        var layout = new InstallLayout(MakeInstalledProgram(), null);
        string starter = layout.StarterFixturesFolder!;
        string mine = MakeFolder("Мои приборы");

        Assert.Equal(new[] { starter }, layout.LibraryFoldersFor(null));
        Assert.Equal(new[] { starter }, layout.LibraryFoldersFor(new[] { "", "  " }));
        Assert.Equal(new[] { mine, starter }, layout.LibraryFoldersFor(new[] { mine }));
        // configured explicitly (another spelling of the same folder) it is not listed twice
        Assert.Equal(new[] { starter + Path.DirectorySeparatorChar }, layout.LibraryFoldersFor(new[] { starter + Path.DirectorySeparatorChar }));

        // without starter files the folder is not used
        var bare = new InstallLayout(Path.Combine(_root, "bare"), null);
        Directory.CreateDirectory(Path.Combine(_root, "bare", "samples", "fixtures"));
        Assert.Null(bare.StarterFixturesFolder);
        Assert.Empty(bare.LibraryFoldersFor(null));
    }

    [Fact]
    public void Specs_are_looked_up_next_to_a_library_then_in_the_installed_copy_then_in_the_repository()
    {
        string program = MakeInstalledProgram();
        var installed = new InstallLayout(program, null);
        var development = new InstallLayout(MakeFolder("dev-bin"), TestEnvironment.Root);

        // a library that ships its own _specs (like the repository) wins, also from a subfolder of it
        string ownSpecs = Path.Combine(MakeFolder("lib-with-specs", "_specs"), "fixtures_index.json");
        File.WriteAllText(ownSpecs, "[]");
        string sub = MakeFolder("lib-with-specs", "gdtf");
        Assert.Equal(ownSpecs, installed.FindSpecsIndex(new[] { sub }));

        // a plain folder of GDTF files: the installed copy, else the repository's
        string plain = MakeFolder("plain library");
        Assert.Equal(installed.InstalledSpecsIndex, installed.FindSpecsIndex(new[] { plain }));
        Assert.Equal(Path.Combine(TestEnvironment.Root, FixtureSpecsIndex.DefaultRelativePath), development.FindSpecsIndex(new[] { plain }));
        Assert.Null(new InstallLayout(MakeFolder("nothing"), null).FindSpecsIndex(new[] { plain }));

        // malformed folder names never throw
        Assert.Equal(installed.InstalledSpecsIndex, installed.FindSpecsIndex(new[] { "\0bad", "" }));
    }

    [Fact]
    public void An_installed_engine_without_a_library_opens_the_sample_scene_with_the_starter_fixtures()
    {
        var layout = new InstallLayout(MakeInstalledProgram(), null);
        var options = StageEngineOptions.FromSettings(new AppSettings(), null, layout);
        Assert.Equal(new[] { layout.StarterFixturesFolder! }, options.LibraryFolders);
        Assert.Equal(layout.InstalledSpecsIndex, options.SpecsIndexPath);

        using var engine = new StageEngine(options with { CacheDirectory = MakeFolder("cache"), DmxParallelism = 2, SimulationParallelism = 2 });
        Assert.Equal(StarterFixtures.Length, engine.Library.Count);
        Assert.True(engine.Specs.Count >= 1091, $"{engine.Specs.Count} researched records");
        Assert.DoesNotContain(engine.Issues.Current, i => i.Code == EngineIssueCodes.LibraryMissing);
        Assert.All(engine.Library.Entries, e => Assert.NotNull(e.Specs));

        var result = engine.Open(layout.SampleScene!);
        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
        var fixtures = engine.Scene.All().Select(e => e.Get<Core.SceneGraph.Components.FixtureComponent>()).OfType<Core.SceneGraph.Components.FixtureComponent>().ToList();
        Assert.Equal(91, fixtures.Count);
        // every fixture is drawn with one of the four starter GDTFs (no stub, no unresolved type)
        Assert.All(fixtures, f =>
        {
            var resolved = engine.Profiles.Resolve(f.GdtfSpec, f.GdtfMode);
            Assert.True(resolved.Success, resolved.Error);
            Assert.Equal(layout.StarterFixturesFolder, Path.GetDirectoryName(resolved.Entry!.FilePath));
        });
    }

    [Fact]
    public void The_command_line_library_replaces_the_settings_folders_and_finds_specs_for_them()
    {
        var layout = new InstallLayout(MakeInstalledProgram(), null);
        string mine = MakeFolder("cli library");
        var settings = new AppSettings { LibraryFolders = new List<string> { MakeFolder("from settings") } };

        var fromSettings = StageEngineOptions.FromSettings(settings, null, layout);
        Assert.Equal(new[] { settings.LibraryFolders[0], layout.StarterFixturesFolder! }, fromSettings.LibraryFolders);
        var fromCommandLine = StageEngineOptions.FromSettings(settings, new[] { mine }, layout);
        Assert.Equal(new[] { mine, layout.StarterFixturesFolder! }, fromCommandLine.LibraryFolders);
        Assert.Equal(layout.InstalledSpecsIndex, fromCommandLine.SpecsIndexPath);

        // a specs file chosen in the settings is used as it is
        var chosen = StageEngineOptions.FromSettings(new AppSettings { SpecsIndexPath = "/somewhere/specs.json" }, new[] { mine }, layout);
        Assert.Equal("/somewhere/specs.json", chosen.SpecsIndexPath);

        // the CLI helper (the same settings file as the editor) puts --library first
        var cli = Cli.CliArgs.EngineOptions(new[] { mine }, null);
        Assert.Equal(mine, cli.LibraryFolders[0]);
        Assert.NotNull(cli.SpecsIndexPath);
    }

    [Fact]
    public void A_library_folder_added_at_run_time_reaches_the_library_and_its_listeners()
    {
        string folder = MakeFolder("Новая библиотека");
        foreach (var name in StarterFixtures.Take(2))
            File.Copy(Path.Combine(TestEnvironment.Root, name), Path.Combine(folder, name));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a fixture");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.Copy(Path.Combine(TestEnvironment.Root, StarterFixtures[3]), Path.Combine(folder, "sub", StarterFixtures[3]));

        using var engine = new StageEngine(new StageEngineOptions
        {
            LibraryFolders = Array.Empty<string>(),
            CacheDirectory = MakeFolder("cache-add"),
            DmxParallelism = 2,
            SimulationParallelism = 2,
        });
        Assert.Contains(engine.Issues.Current, i => i.Code == EngineIssueCodes.LibraryMissing);
        var changes = new List<FixtureLibraryChange>();
        engine.FixtureLibraryChanged += changes.Add;

        var result = engine.AddLibraryFolder(folder);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, engine.Library.Count); // the subfolder is not read, like at start-up
        var change = Assert.Single(changes);
        Assert.Equal(FixtureLibraryChangeSource.Folder, change.Source);
        Assert.Equal(2, change.Added.Count);
        Assert.Contains(Path.GetFullPath(folder), engine.Library.Folders);
        Assert.DoesNotContain(engine.Issues.Current, i => i.Code == EngineIssueCodes.LibraryMissing);

        // again: nothing new, no event
        var again = engine.AddLibraryFolder(folder);
        Assert.True(again.Success);
        Assert.Contains("already", again.Summary, StringComparison.Ordinal);
        Assert.Single(changes);

        // an empty folder is fine and says so; a missing one fails with the reason
        var empty = engine.AddLibraryFolder(MakeFolder("empty"));
        Assert.True(empty.Success);
        Assert.Contains("no GDTF files", empty.Summary, StringComparison.Ordinal);
        var missing = engine.AddLibraryFolder(Path.Combine(_root, "missing"));
        Assert.False(missing.Success);
        Assert.Contains(missing.Issues, i => i.Code == EngineIssueCodes.LibraryMissing);
        Assert.False(engine.AddLibraryFolder("  ").Success);
    }

    [Fact]
    public void Counting_gdtf_files_reads_only_the_folder_itself_and_never_throws()
    {
        string folder = MakeFolder("count");
        File.WriteAllText(Path.Combine(folder, "a.gdtf"), "");
        File.WriteAllText(Path.Combine(folder, "B.GDTF"), "");
        File.WriteAllText(Path.Combine(folder, "c.txt"), "");
        Directory.CreateDirectory(Path.Combine(folder, "deeper"));
        File.WriteAllText(Path.Combine(folder, "deeper", "d.gdtf"), "");

        Assert.Equal(2, InstallLayout.CountGdtfFiles(folder));
        Assert.Equal(1, InstallLayout.CountGdtfFiles(folder, limit: 1));
        Assert.Equal(0, InstallLayout.CountGdtfFiles(Path.Combine(folder, "missing")));
        Assert.Equal(0, InstallLayout.CountGdtfFiles(null));
        Assert.Equal(0, InstallLayout.CountGdtfFiles("\0"));
        Assert.True(InstallLayout.AnyGdtfFiles(new[] { Path.Combine(folder, "missing"), folder }));
        Assert.False(InstallLayout.AnyGdtfFiles(new[] { Path.Combine(folder, "deeper", "nothing") }));
        Assert.False(InstallLayout.AnyGdtfFiles(null));
    }

    [Fact]
    public void The_default_user_library_is_in_the_documents_folder()
    {
        string folder = InstallLayout.DefaultUserLibraryFolder;
        Assert.True(Path.IsPathRooted(folder), folder);
        Assert.Equal("Fixture Library", Path.GetFileName(folder));
        Assert.Equal("Candela", Path.GetFileName(Path.GetDirectoryName(folder)));
        Assert.True(InstallLayout.SamePath(folder + Path.DirectorySeparatorChar, folder));
        Assert.False(InstallLayout.SamePath(folder, Path.GetDirectoryName(folder)!));
    }
}
