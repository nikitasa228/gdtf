using System.IO.Compression;
using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Gdtf;
using Candela.Project;

namespace Candela.Engine.Tests;

/// <summary>
/// Follow-ups requested by the editor: validated GDTF import with the added library entry and a change event, autosave
/// reconfiguration of a running session, and view/engine settings counted as unsaved changes.
/// </summary>
public sealed class EngineFollowUpTests
{
    private const string NoModes = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="Empty" ShortName="E" LongName="Empty" Manufacturer="Test" Description="" Thumbnail=""
                       FixtureTypeID="33333333-3333-4444-5555-666666666666">
            <Models><Model Name="Body" Length="0.3" Width="0.3" Height="0.4" PrimitiveType="Cube"/></Models>
            <Geometries><Geometry Name="Body" Model="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/></Geometries>
            <DMXModes/>
          </FixtureType>
        </GDTF>
        """;

    private static StageEngine EngineWithEmptyLibrary(string name) => new(new StageEngineOptions
    {
        Library = new FixtureLibrary(TestEnvironment.Library.Specs),
        CacheDirectory = TestEnvironment.NewTempDirectory(name + "-cache"),
        DmxParallelism = 2,
        SimulationParallelism = 2,
    });

    [Fact]
    public void Importing_a_gdtf_returns_its_library_entry_and_raises_the_library_event()
    {
        string folder = TestEnvironment.NewTempDirectory("gdtf-import");
        string source = TestEnvironment.Entry(SceneBuilder.MacViper).FilePath;
        string path = Path.Combine(folder, Path.GetFileName(source));
        File.Copy(source, path);
        using var engine = EngineWithEmptyLibrary("gdtf-import");
        var changes = new List<FixtureLibraryChange>();
        engine.FixtureLibraryChanged += changes.Add;

        var result = engine.Import(path);

        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.LibraryEntry);
        Assert.Equal(Path.GetFullPath(path), result.LibraryEntry!.FilePath);
        Assert.Same(result.LibraryEntry, engine.Library.FindByFileName(Path.GetFileName(path)));
        var change = Assert.Single(changes);
        Assert.Equal(FixtureLibraryChangeSource.Import, change.Source);
        Assert.Same(result.LibraryEntry, Assert.Single(change.Added));

        // importing it again finds the same entry and does not raise the event again
        var again = engine.Import(path);
        Assert.True(again.Success);
        Assert.Same(result.LibraryEntry, again.LibraryEntry);
        Assert.Single(changes);
    }

    [Fact]
    public void A_broken_gdtf_is_refused_with_an_issue_and_never_enters_the_library()
    {
        string folder = TestEnvironment.NewTempDirectory("gdtf-broken");
        string garbage = Path.Combine(folder, "Test@Garbage@Rev1.gdtf");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray());
        string truncated = Path.Combine(folder, "Test@Truncated@Rev1.gdtf");
        var real = File.ReadAllBytes(TestEnvironment.Entry(SceneBuilder.MacViper).FilePath);
        File.WriteAllBytes(truncated, real.AsSpan(0, real.Length / 3).ToArray());
        string badXml = Path.Combine(folder, "Test@BadXml@Rev1.gdtf");
        WriteGdtf(badXml, "<GDTF><FixtureType Name=\"x\"><DMXModes>");
        string noModes = Path.Combine(folder, "Test@Empty@Rev1.gdtf");
        WriteGdtf(noModes, NoModes);

        using var engine = EngineWithEmptyLibrary("gdtf-broken");
        int events = 0;
        engine.FixtureLibraryChanged += _ => events++;
        foreach (var path in new[] { garbage, truncated, badXml, noModes })
        {
            var result = engine.Import(path);
            Assert.False(result.Success, Path.GetFileName(path));
            Assert.Null(result.LibraryEntry);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(EngineIssueCodes.ImportFailed, issue.Code);
            Assert.Equal(IssueCategory.Fixture, issue.Category);
            Assert.Contains(Path.GetFileName(path), issue.Message, StringComparison.Ordinal);
            Assert.Contains(engine.Issues.Current, i => ReferenceEquals(i, issue) || i == issue);
        }
        Assert.Equal(0, engine.Library.Count);
        Assert.Equal(0, events);
    }

    [Fact]
    public void Autosave_interval_and_pause_change_without_a_new_session()
    {
        string dir = TestEnvironment.NewTempDirectory("autosave-config");
        using var engine = TestEnvironment.CreateEngine("autosave-config");
        Assert.False(engine.IsAutosaveEnabled);
        Assert.Null(engine.AutosaveInterval);

        engine.EnableAutosave(new AutosaveOptions { Directory = dir, Interval = TimeSpan.FromMinutes(5) });
        var service = engine.Autosave!;
        string key = service.SessionKey;
        Assert.True(engine.IsAutosaveEnabled);

        engine.ConfigureAutosave(enabled: true, TimeSpan.FromMinutes(1));
        Assert.Same(service, engine.Autosave);
        Assert.Equal(TimeSpan.FromMinutes(1), service.Interval);
        Assert.Equal(TimeSpan.FromMinutes(1), engine.AutosaveInterval);

        engine.ConfigureAutosave(enabled: false, TimeSpan.FromMinutes(1));
        Assert.Same(service, engine.Autosave);
        Assert.False(service.Enabled);
        Assert.False(engine.IsAutosaveEnabled);
        Assert.Equal(key, engine.Autosave!.SessionKey);

        // a non-positive interval pauses and keeps the interval
        engine.ConfigureAutosave(enabled: true, TimeSpan.Zero);
        Assert.False(service.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), service.Interval);

        engine.ConfigureAutosave(enabled: true, TimeSpan.FromMinutes(2));
        Assert.True(service.Enabled);
        Assert.Equal(key, engine.Autosave!.SessionKey);
        Assert.Equal(TimeSpan.FromMinutes(2), service.Interval);

        // a project opened later gets a session of its own with the current values (paused stays paused)
        engine.ConfigureAutosave(enabled: false, TimeSpan.FromMinutes(3));
        engine.New("Next");
        Assert.NotNull(engine.Autosave);
        Assert.NotSame(service, engine.Autosave);
        Assert.False(engine.Autosave!.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(3), engine.Autosave.Interval);
        Assert.Equal(dir, Path.GetDirectoryName(engine.Autosave.FilePath));
        engine.DisableAutosave();
    }

    [Fact]
    public void Configuring_autosave_before_it_was_enabled_starts_a_session_only_when_enabled()
    {
        using var engine = TestEnvironment.CreateEngine("autosave-late");
        engine.ConfigureAutosave(enabled: false, TimeSpan.FromMinutes(4));
        Assert.Null(engine.Autosave);
        Assert.Equal(TimeSpan.FromMinutes(4), engine.AutosaveInterval);
        string dir = TestEnvironment.NewTempDirectory("autosave-late");
        engine.EnableAutosave(new AutosaveOptions { Directory = dir, Interval = TimeSpan.FromMinutes(5) });
        engine.DisableAutosave();
        Assert.Null(engine.Autosave);
        engine.ConfigureAutosave(enabled: true, TimeSpan.FromMinutes(6));
        Assert.NotNull(engine.Autosave);
        Assert.Equal(TimeSpan.FromMinutes(6), engine.Autosave!.Interval);
        Assert.Equal(dir, Path.GetDirectoryName(engine.Autosave.FilePath)); // the folder given to EnableAutosave
        engine.DisableAutosave();
    }

    [Fact]
    public async Task View_settings_changes_are_unsaved_changes_and_reach_the_autosave()
    {
        string dir = TestEnvironment.NewTempDirectory("view-dirty");
        using var engine = TestEnvironment.CreateEngine("view-dirty");
        string project = Path.Combine(TestEnvironment.NewTempDirectory("view-dirty-project"), "show.candela");
        Assert.True(engine.SaveAs(project).Success);
        Assert.False(engine.IsModified);
        long version = engine.SettingsVersion;

        engine.View.Preview.Bloom = false;
        engine.MarkViewSettingsChanged();
        Assert.True(engine.IsModified);
        Assert.True(engine.SettingsVersion > version);
        Assert.True(engine.Save().Success);
        Assert.False(engine.IsModified);

        engine.Settings.MotorDynamics = false;
        engine.MarkSettingsChanged();
        Assert.True(engine.IsModified);
        Assert.True(engine.Save().Success);

        // assigning another settings object counts too
        engine.View = new ViewSettings { WorkLight = WorkLightMode.Off };
        Assert.True(engine.IsModified);
        Assert.True(engine.Save().Success);

        using (var reopened = TestEnvironment.CreateEngine("view-dirty-reopen"))
        {
            Assert.True(reopened.Open(project).Success);
            Assert.Equal(WorkLightMode.Off, reopened.View.WorkLight);
            Assert.False(reopened.Settings.MotorDynamics);
            Assert.False(reopened.IsModified);
        }

        // the autosave picks up a marked view change
        engine.EnableAutosave(new AutosaveOptions { Directory = dir, Interval = TimeSpan.FromMilliseconds(10) });
        engine.View.Preview.Bloom = true;
        engine.MarkViewSettingsChanged();
        for (int i = 0; i < 100 && engine.Autosave!.LastSavedUtc is null; i++)
        {
            Thread.Sleep(15);
            engine.Tick(0.015);
            await engine.Autosave.FlushAsync();
        }
        engine.Tick(0);
        Assert.NotNull(engine.Autosave!.LastSavedUtc);
        engine.DisableAutosave();
    }

    [Fact]
    public void Opening_new_and_saved_projects_are_not_modified_by_their_settings()
    {
        using var engine = TestEnvironment.CreateEngine("settings-open");
        engine.New("A");
        Assert.False(engine.IsModified);
        Assert.Throws<ArgumentNullException>(() => engine.View = null!);
        Assert.Throws<ArgumentNullException>(() => engine.Settings = null!);
        SceneBuilder.Floor(engine.Scene, 4f);
        Assert.True(engine.IsModified);
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.False(engine.IsModified);
        _ = new Vector3(0);
    }

    private static void WriteGdtf(string path, string description)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("description.xml").Open());
        writer.Write(description);
    }
}
