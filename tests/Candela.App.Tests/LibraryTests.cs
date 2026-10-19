using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Editing;
using Candela.App.Panels;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Thumbnails;
using Candela.App.Ui;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Gdtf;

namespace Candela.App.Tests;

/// <summary>Auto-patching of placed fixtures (first free addresses, one universe per break).</summary>
public sealed class AutoPatchTests
{
    private static DmxBreakLayout[] Breaks(params int[] footprints) => footprints.Select((f, i) => new DmxBreakLayout(i + 1, f)).ToArray();

    [Fact]
    public void An_empty_patch_starts_at_universe_1_address_1()
    {
        var a = AutoPatch.FindFree(Array.Empty<PatchRange>(), Breaks(38))!;
        Assert.Equal(new[] { new DmxAddress(1, 1, 1) }, a);
    }

    [Fact]
    public void The_first_gap_that_fits_is_used()
    {
        var used = new[]
        {
            new PatchRange(1, 1, 20, 1),
            new PatchRange(1, 31, 60, 1), // gap 21..30 is too small for 16 channels
            new PatchRange(1, 61, 100, 1),
        };
        Assert.Equal(new DmxAddress(1, 101, 1), AutoPatch.FindFree(used, Breaks(16))![0]);
        Assert.Equal(new DmxAddress(1, 21, 1), AutoPatch.FindFree(used, Breaks(10))![0]);
        // Unsorted and overlapping ranges are fine.
        var messy = new[] { new PatchRange(1, 50, 80, 1), new PatchRange(1, 1, 60, 1), new PatchRange(1, 70, 90, 1) };
        Assert.Equal(new DmxAddress(1, 91, 1), AutoPatch.FindFree(messy, Breaks(5))![0]);
    }

    [Fact]
    public void A_break_never_straddles_two_universes()
    {
        var used = new[] { new PatchRange(1, 1, 500, 1) };
        Assert.Equal(new DmxAddress(2, 1, 1), AutoPatch.FindFree(used, Breaks(20))![0]);
        Assert.Equal(new DmxAddress(1, 501, 1), AutoPatch.FindFree(used, Breaks(12))![0]);
        Assert.Null(AutoPatch.FindFree(used, Breaks(513)));
    }

    [Fact]
    public void Several_breaks_follow_each_other_and_modes_without_channels_stay_unpatched()
    {
        var a = AutoPatch.FindFree(new[] { new PatchRange(1, 1, 10, 1) }, Breaks(4, 510))!;
        Assert.Equal(new[] { new DmxAddress(1, 11, 1), new DmxAddress(2, 1, 2) }, a);
        Assert.Empty(AutoPatch.FindFree(Array.Empty<PatchRange>(), Array.Empty<DmxBreakLayout>())!);
        Assert.Empty(AutoPatch.FindFree(Array.Empty<PatchRange>(), Breaks(0))!);
    }

    [Fact]
    public void Thousands_of_ranges_are_scanned_quickly()
    {
        var used = Enumerable.Range(0, 20_000).Select(i => new PatchRange(i / 25 + 1, i % 25 * 20 + 1, i % 25 * 20 + 20, 1)).Reverse().ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var a = AutoPatch.FindFree(used, Breaks(30))!;
        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(new DmxAddress(801, 1, 1), a[0]);
    }

    [Fact]
    public void Next_fixture_id()
    {
        var scene = new Scene();
        Assert.Equal(1, AutoPatch.NextFixtureId(scene));
        var e = new Entity("f");
        e.Add(new FixtureComponent { FixtureId = 41 });
        scene.Add(e);
        Assert.Equal(42, AutoPatch.NextFixtureId(scene));
    }
}

/// <summary>Placing library fixtures into a running engine's scene.</summary>
[Collection(UiStateCollection.Name)]
public sealed class LibraryPlacementTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly EditorSession _session;

    public LibraryPlacementTests()
    {
        Loc.SetLanguage("en");
        var settings = new SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        _session = EngineTestSupport.CreateSession(_log, settings, _dir);
    }

    public void Dispose()
    {
        _session.Dispose();
        _log.Dispose();
        _dir.Dispose();
    }

    private static FixtureLibraryEntry Entry(string fileNamePart) =>
        EngineTestSupport.Library.Entries.First(e => e.FileName.Contains(fileNamePart, StringComparison.OrdinalIgnoreCase) && EngineTestSupport.Library.IsLatestRevision(e));

    [Fact]
    public void Fixture_info_lists_the_modes_with_footprints_and_a_plausible_size()
    {
        var entry = Entry("Robin_T1");
        var info = FixtureInfoCache.Read(EngineTestSupport.Library, entry);
        Assert.Null(info.Error);
        Assert.True(info.Modes.Count >= 1);
        Assert.All(info.Modes, m => Assert.True(m.Footprint > 0 && m.Breaks.Sum(b => b.Footprint) == m.Footprint));
        Assert.Equal(info.Modes[0], info.DefaultMode);
        var size = info.RestBounds.Size;
        Assert.InRange(size.Z, 0.2f, 1.5f); // a moving head is a few decimetres tall
        Assert.InRange(MathF.Max(size.X, size.Y), 0.15f, 1.5f);
        // The cache answers null first, then the same information.
        var cache = new FixtureInfoCache(EngineTestSupport.Library);
        var now = cache.GetNow(entry, TimeSpan.FromSeconds(30));
        Assert.Equal(info.Modes.Count, now.Modes.Count);
        Assert.NotNull(cache.Get(entry));
    }

    [Fact]
    public void A_placed_fixture_is_named_patched_selected_and_undoable()
    {
        var entry = Entry("Robin_T1");
        var info = FixtureInfoCache.Read(EngineTestSupport.Library, entry);
        var hit = SurfaceHit.Ground(new Vector3(1, 2, 0));
        var first = LibraryPlacement.PlaceFixture(_session, entry, info, hit);
        var fixture = first.Get<FixtureComponent>()!;
        Assert.Equal(entry.FileName, fixture.GdtfSpec);
        Assert.Equal(info.DefaultMode!.Name, fixture.GdtfMode);
        Assert.Equal(1, fixture.FixtureId);
        Assert.Equal(new DmxAddress(1, 1, 1), Assert.Single(fixture.Addresses));
        Assert.Equal(new[] { first.Id }, _session.Selection.Ids);
        Assert.StartsWith(entry.ModelName, first.Name);
        Assert.Equal(new Vector3(1, 2, 0), first.WorldPosition);
        // The engine resolves it (patch issues and DMX evaluation use the same GDTF and mode).
        Assert.True(_session.Engine!.Profiles.Resolve(fixture.GdtfSpec, fixture.GdtfMode).Success);

        var second = LibraryPlacement.PlaceFixture(_session, entry, info, hit);
        var f2 = second.Get<FixtureComponent>()!;
        Assert.Equal(2, f2.FixtureId);
        Assert.Equal(new DmxAddress(1, 1 + info.DefaultMode.Footprint, 1), Assert.Single(f2.Addresses));

        _session.Undo.Undo();
        Assert.Null(_session.Scene.Find(second.Id));
        Assert.NotNull(_session.Scene.Find(first.Id));
    }

    [Fact]
    public void Changing_the_mode_patches_again_in_one_undo_step()
    {
        var entry = EngineTestSupport.Library.Entries.First(e =>
        {
            if (!EngineTestSupport.Library.IsLatestRevision(e))
                return false;
            var i = FixtureInfoCache.Read(EngineTestSupport.Library, e);
            return i.Modes.Count >= 2 && i.Modes[0].Footprint != i.Modes[1].Footprint;
        });
        var info = FixtureInfoCache.Read(EngineTestSupport.Library, entry);
        var placed = LibraryPlacement.PlaceFixture(_session, entry, info, SurfaceHit.Ground(Vector3.Zero));
        var other = LibraryPlacement.PlaceFixture(_session, entry, info, SurfaceHit.Ground(Vector3.UnitX));
        var mode = info.Modes[1];
        LibraryPlacement.ChangeMode(_session, placed, mode);
        var fixture = placed.Get<FixtureComponent>()!;
        Assert.Equal(mode.Name, fixture.GdtfMode);
        // No overlap with the other fixture, and the fixture's own old channels count as free.
        var patch = Patch.Build(_session.Scene, _session.Engine!.Profiles);
        var ranges = patch.Entries.SelectMany(e => e.Ranges.Select(r => (e.Entity, r))).ToList();
        foreach (var (e, r) in ranges)
            Assert.DoesNotContain(ranges, x => !ReferenceEquals(x.Entity, e) && x.r.Overlaps(r));
        Assert.Equal($"Mode {mode.Name}", _session.Undo.UndoDescription);
        _session.Undo.Undo();
        Assert.Equal(info.Modes[0].Name, placed.Get<FixtureComponent>()!.GdtfMode);
        Assert.NotNull(_session.Scene.Find(other.Id));
    }

    [Fact]
    public void Placed_into_Fokina_the_fixture_takes_addresses_no_one_uses()
    {
        Assert.True(_session.Open(EngineTestSupport.Fokina).Succeeded);
        var before = AutoPatch.UsedRanges(_session.Scene, _session.Engine!.Profiles);
        var entry = Entry("Robin_T1");
        var info = FixtureInfoCache.Read(EngineTestSupport.Library, entry);
        var placed = LibraryPlacement.PlaceFixture(_session, entry, info, SurfaceHit.Ground(Vector3.Zero));
        var fixture = placed.Get<FixtureComponent>()!;
        var address = Assert.Single(fixture.Addresses);
        var range = new PatchRange(address.Universe, address.Address, address.Address + info.DefaultMode!.Footprint - 1, address.Break);
        Assert.DoesNotContain(before, r => r.Overlaps(range));
        Assert.True(fixture.FixtureId > _session.Scene.Query<FixtureComponent>().Where(q => q.Entity != placed).Max(q => q.Component.FixtureId));
    }
}

/// <summary>The user's model library file.</summary>
public sealed class ModelLibraryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Models_and_images_are_added_newest_first_persisted_and_removed()
    {
        string file = _dir.File("models.json");
        var lib = new ModelLibrary(file, _log);
        lib.Load();
        Assert.Empty(lib.Items);
        string obj = _dir.Write("chair.obj", "v 0 0 0");
        string png = _dir.Write("plan.png", "x");
        Assert.True(lib.Add(obj));
        Assert.True(lib.Add(png));
        Assert.False(lib.Add(_dir.Write("notes.txt", "x")));
        Assert.False(lib.Add(_dir.Write("house.skp", "x")));
        Assert.Equal(new[] { ModelItemKind.Image, ModelItemKind.Model }, lib.Items.Select(i => i.Kind));
        Assert.Equal("plan", lib.Items[0].Name);
        Assert.Equal("PNG", lib.Items[0].Format);
        // Adding again moves it to the top without a duplicate.
        Assert.False(lib.Add(obj));
        Assert.Equal(obj, lib.Items[0].Path);
        Assert.Equal(2, lib.Items.Count);

        var again = new ModelLibrary(file, _log);
        again.Load();
        Assert.Equal(lib.Items.Select(i => i.Path), again.Items.Select(i => i.Path));
        Assert.True(again.Remove(obj));
        Assert.False(again.Remove(obj));
        var third = new ModelLibrary(file, _log);
        third.Load();
        Assert.Single(third.Items);
    }

    [Fact]
    public void A_damaged_file_gives_an_empty_library_and_odd_entries_are_skipped()
    {
        string file = _dir.Write("models.json", "{ not json");
        var lib = new ModelLibrary(file, _log);
        lib.Load();
        Assert.Empty(lib.Items);
        File.WriteAllText(file, """{ "items": [ 3, null, { "path": 5 }, { "path": "/x/a.glb", "added": "yesterday" }, { "path": "/x/a.glb" }, { "path": "/x/b.exe" } ] }""");
        lib.Load();
        var item = Assert.Single(lib.Items);
        Assert.Equal(ModelItemKind.Model, item.Kind);
    }

    [Fact]
    public void Read_only_libraries_never_write()
    {
        string file = _dir.File("models.json");
        var lib = new ModelLibrary(file, _log) { ReadOnly = true };
        lib.Load();
        Assert.True(lib.Add(_dir.Write("a.glb", "x")));
        Assert.False(File.Exists(file));
    }
}

public sealed class ImageResizeTests
{
    [Fact]
    public void Images_are_fitted_into_a_square_with_their_aspect_kept()
    {
        var pixels = new byte[400 * 100 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 3] = 255;
        }

        var image = new ImageData { Width = 400, Height = 100, Format = PixelFormat.Rgba8, Pixels = pixels };
        var thumb = ImageResize.Fit(image, 64);
        Assert.Equal((64, 64), (thumb.Width, thumb.Height));
        // 64 × 16 picture in the middle, transparent above and below.
        int Alpha(int x, int y) => thumb.Pixels[(y * 64 + x) * 4 + 3];
        Assert.Equal(0, Alpha(32, 2));
        Assert.Equal(255, Alpha(32, 32));
        Assert.Equal(0, Alpha(32, 61));
        Assert.Equal(200, thumb.Pixels[(32 * 64 + 32) * 4]);
        // Small images are not enlarged.
        var small = ImageResize.Fit(new ImageData { Width = 2, Height = 2, Format = PixelFormat.Rgba8, Pixels = new byte[16] }, 32);
        Assert.Equal(32, small.Width);
        Assert.Throws<ArgumentException>(() => ImageResize.Fit(new ImageData { Width = 0, Height = 2, Format = PixelFormat.Rgba8 }, 32));
    }
}

/// <summary>Search and categories of the library panel's fixture list.</summary>
public sealed class LibraryFilterTests
{
    [Fact]
    public void Search_matches_model_manufacturer_and_file_name_in_any_case()
    {
        var entry = EngineTestSupport.Library.Entries.First(e => e.FileName.Contains("Robin_T1", StringComparison.OrdinalIgnoreCase));
        Assert.True(LibraryPanel.Matches(entry, "robin"));
        Assert.True(LibraryPanel.Matches(entry, entry.Manufacturer.ToUpperInvariant()));
        Assert.True(LibraryPanel.Matches(entry, "t1"));
        Assert.False(LibraryPanel.Matches(entry, "no such fixture"));
        // Every category of the requirement is a sub-tab.
        Assert.Equal(18, LibraryPanel.Categories.Count);
        Assert.Contains(FixtureCategory.GeneratorFire, LibraryPanel.Categories);
        Assert.DoesNotContain(FixtureCategory.Other, LibraryPanel.Categories);
        Assert.Equal(new[] { "PROFILE", "WASH", "WASH BEAM", "BEAM", "SPOT", "FOLLOW SPOT", "LED BAR", "PAR", "HAZE", "FOG", "FAN", "SCANER", "LASER", "STROBE", "BLINDER", "UV", "BSW", "GENERATOR FIRE" },
            LibraryPanel.Categories.Select(c => c.DisplayName()));
    }

    [Fact]
    public void Thumbnail_keys_are_stable_and_distinct()
    {
        var a = EngineTestSupport.Library.Entries[0];
        var b = EngineTestSupport.Library.Entries[1];
        Assert.Equal(ThumbnailWorker.StillKey(a, 128), ThumbnailWorker.StillKey(a, 128));
        Assert.NotEqual(ThumbnailWorker.StillKey(a, 128), ThumbnailWorker.StillKey(a, 160));
        Assert.NotEqual(ThumbnailWorker.StillKey(a, 128), ThumbnailWorker.StillKey(b, 128));
        Assert.NotEqual(ThumbnailWorker.OrbitKey(a, 256, 0), ThumbnailWorker.OrbitKey(a, 256, 1));
    }
}
