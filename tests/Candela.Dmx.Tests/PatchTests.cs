using System.Diagnostics;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Dmx.Tests;

/// <summary>The patch of a scene and its issue checks.</summary>
public class PatchTests
{
    private const string Viper = "Martin_Professional@MAC_Viper_Profile"; // Basic: 26 slots
    private const string Zonda = "Ayrton@Zonda_3_FX"; // "Extended Pixel 2": break 1 = 51 slots, break 2 = 28 slots

    private static Entity Add(Scene scene, string name, int fixtureId, string gdtfSpec, string? mode, params DmxAddress[] addresses)
    {
        var entity = new Entity(name);
        var fixture = entity.Add(new FixtureComponent { GdtfSpec = gdtfSpec, GdtfMode = mode, FixtureId = fixtureId });
        fixture.Addresses.AddRange(addresses);
        scene.Add(entity);
        return entity;
    }

    private static Entity AddViper(Scene scene, string name, int fixtureId, int universe, int address) =>
        Add(scene, name, fixtureId, TestData.Entry(Viper).FileName, "Basic", new DmxAddress(universe, address));

    private static IReadOnlyList<Issue> Issues(Scene scene) => new PatchIssueProvider(TestData.Profiles).Analyze(scene).ToList();

    [Fact]
    public void Ranges_follow_the_footprint_of_every_break()
    {
        var scene = new Scene();
        var spot = AddViper(scene, "Spot 1", 101, 2, 10);
        var zonda = Add(scene, "Zonda", 5, TestData.Entry(Zonda).FileName, "Extended Pixel 2", new DmxAddress(3, 1, 1), new DmxAddress(4, 101, 2));
        var patch = Patch.Build(scene, TestData.Profiles);
        Assert.Equal(new[] { 2, 3, 4 }, patch.Universes);
        Assert.Equal([new PatchRange(2, 10, 35, 1)], patch.Find(spot.Id)!.Ranges);
        Assert.Equal([new PatchRange(3, 1, 51, 1), new PatchRange(4, 101, 128, 2)], patch.Find(zonda.Id)!.Ranges);
        Assert.Equal([spot.Id], patch.At(2, 35).Select(e => e.Entity.Id));
        Assert.Empty(patch.At(2, 36));
        Assert.Equal("Fixture 101 'Spot 1'", patch.Find(spot.Id)!.Label);
        Assert.Empty(Issues(scene));
    }

    [Fact]
    public void Overlapping_addresses_name_both_fixtures()
    {
        var scene = new Scene();
        var a = AddViper(scene, "Spot 1", 101, 1, 1);
        var b = AddViper(scene, "Spot 2", 102, 1, 20); // 20 … 45 overlaps 1 … 26 on 20 … 26
        AddViper(scene, "Spot 3", 103, 1, 46);
        AddViper(scene, "Spot 4", 104, 2, 20); // same slots, other universe: fine
        var issue = Assert.Single(Issues(scene));
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(IssueCategory.Patch, issue.Category);
        Assert.Equal("patch.overlap", issue.Code);
        Assert.Contains("Fixture 101 'Spot 1'", issue.Message);
        Assert.Contains("Fixture 102 'Spot 2'", issue.Message);
        Assert.Contains("1.020–1.026", issue.Message);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), issue.Entities.Order());
    }

    [Fact]
    public void Identical_fixtures_sharing_an_address_are_a_warning()
    {
        var scene = new Scene();
        AddViper(scene, "Left", 1, 1, 1);
        AddViper(scene, "Right", 2, 1, 1);
        var issue = Assert.Single(Issues(scene));
        Assert.Equal("patch.shared-address", issue.Code);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void A_footprint_past_slot_512_is_an_error()
    {
        var scene = new Scene();
        AddViper(scene, "Edge", 9, 1, 500);
        var issue = Assert.Single(Issues(scene));
        Assert.Equal("patch.out-of-range", issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("Fixture 9 'Edge'", issue.Message);
        Assert.Contains("525", issue.Message);
    }

    [Fact]
    public void Unknown_files_modes_universes_and_addresses_are_reported()
    {
        var scene = new Scene();
        Add(scene, "Ghost", 1, "Nobody@Nothing_9000.gdtf", null, new DmxAddress(1, 1));
        Add(scene, "Wrong mode", 2, TestData.Entry(Viper).FileName, "Turbo 99", new DmxAddress(1, 100));
        Add(scene, "Universe zero", 3, TestData.Entry(Viper).FileName, "Basic", new DmxAddress(0, 1));
        Add(scene, "Address 600", 4, TestData.Entry(Viper).FileName, "Basic", new DmxAddress(1, 600));
        Add(scene, "No file", 5, "", null);
        var issues = Issues(scene);
        Issue Single(string code) => Assert.Single(issues, i => i.Code == code);
        Assert.Equal(2, issues.Count(i => i.Code == "patch.unknown-gdtf"));
        Assert.Contains(issues, i => i.Code == "patch.unknown-gdtf" && i.Message.Contains("Fixture 1 'Ghost'"));
        var mode = Single("patch.unknown-mode");
        Assert.Contains("Fixture 2 'Wrong mode'", mode.Message);
        Assert.Contains("Turbo 99", mode.Message);
        Assert.Contains("Basic", mode.Message); // lists the modes that exist
        Assert.Contains("Fixture 3 'Universe zero'", Single("patch.invalid-universe").Message);
        Assert.Contains("Fixture 4 'Address 600'", Single("patch.invalid-address").Message);
        Assert.Contains(issues, i => i.Code == "patch.unknown-gdtf" && i.Message.Contains("Fixture 5 'No file'"));
        Assert.All(issues, i => Assert.Equal(IssueCategory.Patch, i.Category));
    }

    [Fact]
    public void A_multi_break_mode_needs_an_address_for_every_break()
    {
        var scene = new Scene();
        Add(scene, "Zonda", 5, TestData.Entry(Zonda).FileName, "Extended Pixel 2", new DmxAddress(3, 1, 1));
        var issue = Assert.Single(Issues(scene));
        Assert.Equal("patch.missing-break", issue.Code);
        Assert.Contains("break 2", issue.Message);
    }

    [Fact]
    public void Mvr_stub_names_resolve_to_the_library_model()
    {
        var scene = new Scene();
        var e = Add(scene, "Stub", 1, "Martin_Professional@MAC_Viper_Profile.gdtf", "Basic", new DmxAddress(1, 1));
        var entry = Patch.Build(scene, TestData.Profiles).Find(e.Id)!;
        Assert.NotNull(entry.Profile);
        Assert.Equal(26, entry.Ranges[0].Length);
        var note = Assert.Single(Issues(scene));
        Assert.Equal("patch.matched-gdtf", note.Code);
        Assert.Equal(IssueSeverity.Note, note.Severity);
    }

    [Fact]
    public void Thousands_of_fixtures_are_checked_quickly()
    {
        var scene = new Scene();
        var viper = TestData.Entry(Viper).FileName;
        int n = 0;
        for (int u = 1; u <= 250; u++)
            for (int k = 0; k < 19; k++) // 19 × 26 = 494 slots per universe
                Add(scene, $"Spot {++n}", n, viper, "Basic", new DmxAddress(u, 1 + k * 26));
        Add(scene, "Intruder", 99999, viper, "Basic", new DmxAddress(125, 30)); // overlaps two fixtures
        var sw = Stopwatch.StartNew();
        var issues = Issues(scene);
        sw.Stop();
        Assert.Equal(2, issues.Count(i => i.Code == "patch.overlap"));
        Assert.All(issues, i => Assert.Contains("Fixture 99999 'Intruder'", i.Message));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"{n} fixtures took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }
}
