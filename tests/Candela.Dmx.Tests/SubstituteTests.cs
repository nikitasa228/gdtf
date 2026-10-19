using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Candela.Gdtf;

namespace Candela.Dmx.Tests;

/// <summary>
/// Stand-in GDTFs for models the library lacks (an MVR stub such as <c>Ayrton@MagicBlade R</c> resolved to the similar
/// Ayrton MagicPanel 602): the mode fits the patch spacing, the substitution is reported once per type, and patch problems
/// that only the stand-in's footprint causes are warnings.
/// </summary>
public class SubstituteTests
{
    private const string MagicBladeStub = "Ayrton@MagicBlade R.gdtf"; // → MagicPanel 602: Extended 160, Standard 20, Basic 18

    private static List<Entity> Blades(Scene scene, int count, int spacing, string mode = "Extended", int first = 1, int universe = 1)
    {
        var list = new List<Entity>();
        for (int i = 0; i < count; i++)
        {
            var e = new Entity($"Blade {i + 1}");
            var f = e.Add(new FixtureComponent { GdtfSpec = MagicBladeStub, GdtfMode = mode, FixtureId = 100 + i });
            f.Addresses.Add(new DmxAddress(universe, first + i * spacing));
            scene.Add(e);
            list.Add(e);
        }
        return list;
    }

    [Fact]
    public void Patch_spacing_is_the_smallest_gap_between_start_addresses_of_one_universe()
    {
        Assert.Equal(44, PatchSpacing.Infer([new(1, 1), new(1, 45), new(1, 89), new(1, 177)]));
        Assert.Equal(26, PatchSpacing.Infer([new(1, 1), new(2, 1), new(1, 27), new(2, 27)]));
        Assert.Null(PatchSpacing.Infer([new(1, 1), new(2, 5)])); // one fixture per universe: unknown
        Assert.Null(PatchSpacing.Infer([new(1, 10), new(1, 10)])); // the same address twice says nothing
        Assert.Null(PatchSpacing.Infer([]));
        Assert.Null(PatchSpacing.Infer([new(0, 1), new(1, 600)])); // invalid addresses are ignored
    }

    [Fact]
    public void The_stand_in_takes_the_largest_mode_that_fits_the_spacing()
    {
        var match = TestData.Library.FindBySpec(MagicBladeStub);
        Assert.Equal(FixtureMatchKind.Substitute, match!.Kind);
        var panel = TestData.Library.GetFixtureType(match.Entry);
        Assert.Equal("Standard", panel.FitModeToSpacing("Extended", 44)!.Name); // 20 of the 44 channels
        Assert.Equal("Basic", panel.FitModeToSpacing("Extended", 19)!.Name);
        Assert.Equal("Basic", panel.FitModeToSpacing("Extended", 5)!.Name); // nothing fits: the smallest
        Assert.Equal("Extended", panel.FitModeToSpacing("Extended", 160)!.Name); // the requested one fits
        Assert.Equal("Basic", panel.FitModeToSpacing("Basic", 44)!.Name); // a requested mode that fits is kept
        Assert.Equal("Extended", panel.FitModeToSpacing("Extended", null)!.Name); // no spacing: by name
        Assert.Equal(panel.DmxModes[0].Name, panel.FitModeToSpacing("No such mode", null)!.Name);
        Assert.Equal(160, panel.FindMode("Extended")!.FirstBreakFootprint);
    }

    [Fact]
    public void A_substitute_resolves_to_the_fitting_mode_and_says_what_it_stands_in_for()
    {
        var cache = new FixtureProfileCache(TestData.Library);
        var plain = cache.Resolve(MagicBladeStub, "Extended");
        Assert.Equal("Extended", plain.Profile!.Mode.Name);
        var fitted = cache.Resolve(MagicBladeStub, "Extended", 44);
        Assert.Equal(FixtureMatchKind.Substitute, fitted.MatchKind);
        Assert.Equal("Standard", fitted.Profile!.Mode.Name);
        Assert.Equal(new FixtureSubstitution(MagicBladeStub, "Extended", match().Entry.FileName, 44), fitted.Substitution);
        // a mode the stand-in does not have no longer leaves the fixture without a profile
        Assert.NotNull(cache.Resolve(MagicBladeStub, "Mode 1").Profile);
        // exact files ignore the spacing
        var viper = TestData.Entry("Martin_Professional@MAC_Viper_Profile").FileName;
        Assert.Equal("Extended", cache.Resolve(viper, "Extended", 5).Profile!.Mode.Name);
        Assert.Null(cache.Resolve(viper, "Extended", 5).Substitution);

        static FixtureMatch match() => TestData.Library.FindBySpec(MagicBladeStub)!;
    }

    [Fact]
    public void A_stand_in_patched_at_its_spacing_has_no_patch_errors_and_one_substitute_warning()
    {
        var scene = new Scene();
        var blades = Blades(scene, 6, spacing: 44);
        var patch = Patch.Build(scene, TestData.Profiles);
        Assert.All(blades, b => Assert.Equal("Standard", patch.Find(b.Id)!.Profile!.Mode.Name));
        Assert.All(blades, b => Assert.Equal(44, patch.Find(b.Id)!.Substitution!.PatchSpacing));
        var issues = PatchIssueProvider.Analyze(patch);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        var sub = Assert.Single(issues, i => i.Code == "patch.substitute-gdtf");
        Assert.Equal(IssueSeverity.Warning, sub.Severity);
        Assert.Equal(blades.Select(b => b.Id).Order(), sub.Entities.Order());
        Assert.Contains("'Ayrton@MagicBlade R.gdtf'", sub.Message);
        Assert.Contains("mode 'Standard'", sub.Message);
        Assert.Contains("44-channel patch spacing", sub.Message);
        Assert.Contains("asks for 'Extended'", sub.Message);
    }

    [Fact]
    public void Overlaps_only_the_stand_in_causes_are_one_warning_but_real_collisions_stay_errors()
    {
        // 10 channels apart: every mode of the stand-in is wider (smallest: Basic, 18) — the overlaps come from the
        // stand-in, the real model fits the patch
        var scene = new Scene();
        var blades = Blades(scene, 4, spacing: 10);
        var issues = new PatchIssueProvider(TestData.Profiles).Analyze(scene).ToList();
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        var overlap = Assert.Single(issues, i => i.Code == "patch.overlap");
        Assert.Equal(IssueSeverity.Warning, overlap.Severity);
        Assert.Contains("3 overlap(s)", overlap.Message);
        Assert.Contains("10-channel patch spacing", overlap.Message);
        Assert.Equal(blades.Select(b => b.Id).Order(), overlap.Entities.Order());

        // a stand-in at the end of the universe runs past slot 512 only with its own footprint
        var tail = new Scene();
        Blades(tail, 3, spacing: 10, first: 480); // the last one: 500–517 as the stand-in, 500–509 at most for real
        var tailIssues = new PatchIssueProvider(TestData.Profiles).Analyze(tail).ToList();
        var range = Assert.Single(tailIssues, i => i.Code == "patch.out-of-range");
        Assert.Equal(IssueSeverity.Warning, range.Severity);
        Assert.Contains("the real model fits", range.Message);

        // a real fixture patched into a stand-in that fits its spacing collides whatever the real footprint
        var clash = new Scene();
        Blades(clash, 3, spacing: 20); // Basic (18 channels) fits: 1–18, 21–38, 41–58
        var viper = new Entity("Spot");
        viper.Add(new FixtureComponent
        {
            GdtfSpec = TestData.Entry("Martin_Professional@MAC_Viper_Profile").FileName,
            GdtfMode = "Basic",
            Addresses = { new DmxAddress(1, 50) },
        });
        clash.Add(viper);
        var clashIssues = new PatchIssueProvider(TestData.Profiles).Analyze(clash).ToList();
        var real = Assert.Single(clashIssues, i => i.Code == "patch.overlap");
        Assert.Equal(IssueSeverity.Error, real.Severity);
        Assert.Contains(viper.Id, real.Entities);
    }

    [Fact]
    public void A_known_substitution_of_an_exact_file_is_judged_like_a_stand_in()
    {
        // an MVR import stores the stand-in's own file name; the engine tells the patch which fixtures stand in
        var scene = new Scene();
        var file = TestData.Library.FindBySpec(MagicBladeStub)!.Entry.FileName;
        var list = new List<Entity>();
        for (int i = 0; i < 3; i++)
        {
            var e = new Entity($"Blade {i}");
            e.Add(new FixtureComponent { GdtfSpec = file, GdtfMode = "Extended", Addresses = { new DmxAddress(1, 1 + i * 44) } });
            scene.Add(e);
            list.Add(e);
        }
        Assert.Contains(new PatchIssueProvider(TestData.Profiles).Analyze(scene), i => i.Code == "patch.overlap" && i.Severity == IssueSeverity.Error);
        var provider = new PatchIssueProvider(TestData.Profiles)
        {
            Substitutions = (entity, fixture) => new FixtureSubstitution(MagicBladeStub, "Extended", file, 44),
        };
        var issues = provider.Analyze(scene).ToList();
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Code == "patch.overlap" && i.Severity == IssueSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "patch.substitute-gdtf" && i.Entities.Count == 3);
        // a substitution that names another stand-in than the fixture's GDTF does not apply
        var stale = new PatchIssueProvider(TestData.Profiles)
        {
            Substitutions = (entity, fixture) => new FixtureSubstitution(MagicBladeStub, "Extended", "Other.gdtf", 44),
        };
        Assert.Contains(stale.Analyze(scene), i => i.Code == "patch.overlap" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void The_dmx_engine_uses_the_fitting_mode_and_follows_spacing_changes()
    {
        var scene = new Scene();
        var blades = Blades(scene, 3, spacing: 44);
        using var engine = new DmxEngine(scene, new FixtureProfileCache(TestData.Library), new DmxInputHub());
        engine.Update(0);
        Assert.All(blades, b => Assert.Equal("Standard", engine.GetState(b.Id)!.Profile.Mode.Name));
        Assert.Equal(engine.BuildPatch().Find(blades[0].Id)!.Profile!.Mode.Name, engine.GetState(blades[0].Id)!.Profile.Mode.Name);

        // repatched 19 apart: the type's spacing shrinks, every stand-in of the type re-resolves to Basic (18 channels)
        for (int i = 0; i < blades.Count; i++)
        {
            var f = blades[i].Get<FixtureComponent>()!;
            f.Addresses[0] = new DmxAddress(1, 1 + i * 19);
            if (i == 1) f.NotifyChanged(); // only one fixture reports the edit
        }
        engine.Update(0.02);
        Assert.Equal("Basic", engine.GetState(blades[1].Id)!.Profile.Mode.Name);
        Assert.All(blades, b => Assert.Equal("Basic", engine.GetState(b.Id)!.Profile.Mode.Name));
    }
}
