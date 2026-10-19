using System.IO.Compression;
using Candela.App.Diagnostics;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Engine;
using Candela.Gdtf;

namespace Candela.App.Tests;

/// <summary>
/// GDTF files reaching the fixture library: an imported <c>.gdtf</c> comes back as the library entry to select, the
/// engine's <see cref="StageEngine.FixtureLibraryChanged"/> reaches the Library panel's watcher (also for the GDTFs an MVR
/// brings), and a refused file says why in the UI language. Each test has its own small library, so the shared one of the
/// other tests never changes.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class LibraryImportFollowUpTests : IDisposable
{
    private const string RobinT1 = "Robe_Lighting@Robin_T1_Profile@";
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public LibraryImportFollowUpTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
    }

    public void Dispose()
    {
        Loc.SetLanguage("en");
        _log.Dispose();
        _dir.Dispose();
    }

    /// <summary>A session over a private library holding the repository GDTFs whose names start with <paramref name="prefixes"/>.</summary>
    private EditorSession CreateSession(params string[] prefixes)
    {
        string folder = _dir.File("library");
        Directory.CreateDirectory(folder);
        foreach (string prefix in prefixes)
        {
            string source = Directory.EnumerateFiles(EngineTestSupport.Root, prefix + "*.gdtf").First();
            File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);
        }

        var options = EngineTestSupport.Options(_dir);
        return new EditorSession(_log, _settings, options with { Engine = options.Engine! with { Library = FixtureLibrary.Scan(folder) } });
    }

    /// <summary>A copy of a repository GDTF under a new name (so the library does not know it yet).</summary>
    private string CopyGdtf(string prefix, string newName)
    {
        string folder = _dir.File("imports");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, newName);
        File.Copy(Directory.EnumerateFiles(EngineTestSupport.Root, prefix + "*.gdtf").First(), target, overwrite: true);
        return target;
    }

    [Fact]
    public void An_imported_gdtf_comes_back_as_the_entry_to_select_and_the_watcher_hears_of_it_once()
    {
        using var session = CreateSession(RobinT1);
        var engine = session.Engine!;
        using var watcher = new FixtureLibraryWatcher();
        watcher.Follow(engine);
        Assert.True(watcher.TakeChanged()); // a new engine: the panel rebuilds once
        Assert.False(watcher.TakeChanged());
        int before = engine.Library.Count;

        // A file name with printf conversions in it: the messages show it literally.
        string path = CopyGdtf("ACME@ACME_AECO_15@", "Test 100% %s %n %% fixture.gdtf");
        var result = session.Import(path, ImportKind.Gdtf);
        Assert.True(result.Succeeded, result.Message);
        var entry = Assert.IsType<FixtureLibraryEntry>(result.LibraryEntry);
        Assert.Equal("Test 100% %s %n %% fixture.gdtf", entry.FileName);
        Assert.Same(entry, engine.Library.FindByFileName(entry.FileName));
        Assert.Equal(before + 1, engine.Library.Count);
        Assert.StartsWith("Added ", result.Message, StringComparison.Ordinal);
        Assert.Contains(entry.ModelName, result.Message, StringComparison.Ordinal);

        Assert.True(watcher.TakeChanged());
        Assert.Equal(1, watcher.Changes);
        var change = Assert.IsType<FixtureLibraryChange>(watcher.LastChange);
        Assert.Equal(FixtureLibraryChangeSource.Import, change.Source);
        Assert.Same(entry, Assert.Single(change.Added));
        // The newest revision of the model is its own tile.
        Assert.Same(entry, FixtureLibraryWatcher.TileFor(engine.Library, entry));

        // The same file again: already in the library, the same entry is selected, nothing is rebuilt.
        var again = session.Import(path, ImportKind.Gdtf);
        Assert.True(again.Succeeded, again.Message);
        Assert.Same(entry, again.LibraryEntry);
        Assert.False(watcher.TakeChanged());
        Assert.Equal(before + 1, engine.Library.Count);

        // Other imports carry no library entry.
        Assert.Null(SessionResult.Done("x").LibraryEntry);

        // Leaving the engine stops the notifications.
        watcher.Follow(null);
        Assert.True(watcher.TakeChanged());
        session.Import(CopyGdtf("BeamZ@BS_1200", "Another.gdtf"), ImportKind.Gdtf);
        Assert.False(watcher.TakeChanged());
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void Another_file_of_a_model_the_library_has_selects_the_tile_the_grid_shows()
    {
        using var session = CreateSession(RobinT1);
        var library = session.Engine!.Library;
        var result = session.Import(CopyGdtf(RobinT1, RobinT1 + "Imported_copy.gdtf"), ImportKind.Gdtf);
        Assert.True(result.Succeeded, result.Message);
        var imported = Assert.IsType<FixtureLibraryEntry>(result.LibraryEntry);
        Assert.Equal(2, library.GetRevisions(imported).Count);

        // The grid lists one tile per model (its newest revision); the selection is that tile.
        var tile = FixtureLibraryWatcher.TileFor(library, imported);
        Assert.True(library.IsLatestRevision(tile));
        Assert.Contains(imported, library.GetRevisions(tile));
        Assert.Single(library.Entries, e => library.IsLatestRevision(e) && e.ModelName == imported.ModelName);
        Assert.Same(imported, FixtureLibraryWatcher.TileFor(null, imported));
    }

    [Fact]
    public void A_refused_gdtf_says_why_in_the_ui_language_and_leaves_the_library_alone()
    {
        using var session = CreateSession(RobinT1);
        var engine = session.Engine!;
        using var watcher = new FixtureLibraryWatcher();
        watcher.Follow(engine);
        watcher.TakeChanged();
        int before = engine.Library.Count;

        // Not an archive at all.
        string broken = _dir.Write("broken %s.gdtf", "this is not a zip archive");
        var result = session.Import(broken, ImportKind.Gdtf);
        Assert.False(result.Succeeded);
        Assert.StartsWith("The file was not added to the fixture library. ", result.Message, StringComparison.Ordinal);
        Assert.Contains("'broken %s.gdtf' is not a readable GDTF file", result.Message, StringComparison.Ordinal);
        Assert.Null(result.LibraryEntry);

        // A readable fixture type without DMX modes cannot be patched: refused with the engine's reason, in Russian.
        string noModes = _dir.File("No modes.gdtf");
        using (var zip = ZipFile.Open(noModes, ZipArchiveMode.Create))
        {
            var description = zip.CreateEntry("description.xml");
            using var writer = new StreamWriter(description.Open());
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <GDTF DataVersion="1.2">
                  <FixtureType Name="Empty" ShortName="Empty" LongName="Empty" Manufacturer="Test" Description=""
                               FixtureTypeID="5F2A6D0B-1C3E-4A5B-9C7D-0E1F2A3B4C5D" RefFT="" Thumbnail="">
                    <AttributeDefinitions><ActivationGroups/><FeatureGroups/><Attributes/></AttributeDefinitions>
                    <Wheels/><PhysicalDescriptions/><Models/>
                    <Geometries><Geometry Name="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/></Geometries>
                    <DMXModes/>
                  </FixtureType>
                </GDTF>
                """);
        }

        Loc.SetLanguage("ru");
        var refused = session.Import(noModes, ImportKind.Gdtf);
        Assert.False(refused.Succeeded);
        Assert.StartsWith("Файл не добавлен в библиотеку приборов. ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("defines no DMX mode", refused.Message, StringComparison.Ordinal);

        Assert.Equal(before, engine.Library.Count);
        Assert.False(watcher.TakeChanged());
        Assert.Equal(0, watcher.Changes);
    }

    [Fact]
    public void The_gdtfs_an_mvr_brings_reach_the_watcher_with_their_source()
    {
        using var session = CreateSession(RobinT1);
        var engine = session.Engine!;
        using var watcher = new FixtureLibraryWatcher();
        watcher.Follow(engine);
        watcher.TakeChanged();
        int before = engine.Library.Count;

        var opened = session.Open(EngineTestSupport.Fokina);
        Assert.True(opened.Succeeded, opened.Message);
        Assert.Null(opened.LibraryEntry); // an MVR selects nothing in the library
        Assert.True(watcher.TakeChanged());
        var change = Assert.IsType<FixtureLibraryChange>(watcher.LastChange);
        Assert.Equal(FixtureLibraryChangeSource.Mvr, change.Source);
        Assert.NotEmpty(change.Added);
        Assert.Equal(before + change.Added.Count, engine.Library.Count);
        Assert.All(change.Added, e => Assert.Same(e, engine.Library.FindByFileName(e.FileName)));
    }
}
