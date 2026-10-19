using System.Diagnostics;
using System.Numerics;
using System.Text;
using Candela.App.Diagnostics;
using Candela.App.Editing;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.App.Tests;

/// <summary>
/// The library under broken inputs (review of phase B1): an unreadable GDTF gives a fixture type without modes instead
/// of an exception (the drop still places it, unpatched), and a large model-library file loads in linear time.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class LibraryRobustnessTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();

    public LibraryRobustnessTests() => Loc.SetLanguage("en");

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void An_unreadable_fixture_type_has_no_modes_and_is_placed_unpatched()
    {
        string folder = _dir.File("gdtf");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Broken@Garbage@v1.gdtf"), Encoding.ASCII.GetBytes("PK\u0003\u0004 this is not a fixture"));
        File.WriteAllText(Path.Combine(folder, "Empty@Nothing@v1.gdtf"), "");
        var library = FixtureLibrary.Scan(folder);
        Assert.NotEmpty(library.Entries);

        var cache = new FixtureInfoCache(library);
        foreach (var entry in library.Entries)
        {
            var info = cache.GetNow(entry, TimeSpan.FromSeconds(30));
            Assert.Empty(info.Modes);
            Assert.Null(info.DefaultMode);
            Assert.False(string.IsNullOrEmpty(info.Error));
            Assert.Equal(FixtureInfoCache.DefaultBounds, info.RestBounds);
            Assert.NotNull(cache.Get(entry)); // answered, never thrown

            var settings = new SettingsStore(_log, _dir.File("settings.json"));
            settings.Load();
            using var session = EngineTestSupport.CreateSession(_log, settings, _dir);
            var placed = LibraryPlacement.PlaceFixture(session, entry, info, SurfaceHit.Ground(new Vector3(1, 1, 0)));
            var fixture = placed.Get<FixtureComponent>()!;
            Assert.Null(fixture.GdtfMode);
            Assert.Empty(fixture.Addresses);
            Assert.True(session.Undo.CanUndo);
        }
    }

    [Fact]
    public void Asking_for_known_fixture_information_every_frame_does_not_allocate()
    {
        // The hover card and the drop preview ask every frame.
        var entry = EngineTestSupport.Library.Entries.First(e => e.FileName.Contains("Robin_T1", StringComparison.OrdinalIgnoreCase));
        var cache = new FixtureInfoCache(EngineTestSupport.Library);
        var info = cache.GetNow(entry, TimeSpan.FromSeconds(30));
        Assert.Same(info, cache.Get(entry));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            _ = cache.Get(entry);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_model_library_of_thousands_of_entries_loads_quickly_without_duplicates()
    {
        var json = new StringBuilder("{ \"items\": [");
        for (int i = 0; i < 6000; i++)
        {
            if (i > 0)
                json.Append(',');
            // Every path twice: the second one is a duplicate.
            json.Append(System.Globalization.CultureInfo.InvariantCulture, $"{{ \"path\": \"/models/m{i / 2}.glb\" }}");
        }

        json.Append("] }");
        string file = _dir.Write("models.json", json.ToString());
        var library = new ModelLibrary(file, _log);
        var watch = Stopwatch.StartNew();
        library.Load();
        watch.Stop();
        Assert.Equal(ModelLibrary.MaxItems, library.Items.Count);
        Assert.Equal(library.Items.Count, library.Items.Select(i => i.Path).Distinct().Count());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"loading took {watch.Elapsed.TotalMilliseconds:0} ms");
    }
}
