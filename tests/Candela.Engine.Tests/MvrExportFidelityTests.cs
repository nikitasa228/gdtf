using System.Numerics;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Mvr;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;

namespace Candela.Engine.Tests;

/// <summary>
/// File → Export → MVR keeps what the scene holds: the fixture types and DMX modes the scene asks for (not the stand-ins
/// the library draws missing models with), model textures, and the layer of every object.
/// </summary>
public sealed class MvrExportFidelityTests
{
    private static string NewMvrPath(string name) => Path.Combine(TestEnvironment.NewTempDirectory("export-" + name), name + ".mvr");

    private static Dictionary<Guid, MvrFixture> SourceFixtures() =>
        MvrReader.ReadFile(TestEnvironment.Fokina).AllObjects().OfType<MvrFixture>().ToDictionary(f => f.Uuid);

    [Fact]
    public void Fokina_exported_without_edits_names_the_fixture_types_and_modes_of_the_source()
    {
        var missing = FokinaFixtureTypes.MissingFrom(TestEnvironment.Library);
        using var engine = TestEnvironment.CreateEngine("export-fokina");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        int standIns = engine.Settings.Substitutes.Count;
        Assert.Equal(missing.Sum(t => t.Objects), standIns);

        string path = NewMvrPath("fokina");
        var result = engine.ExportMvr(path);
        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain(result.Issues, i => i.Code is MvrIssueCodes.SubstituteExported or MvrIssueCodes.ExportResourceMissing);
        Assert.Contains($"{standIns} stand-in fixture(s) written as the models they stand in for", result.Summary, StringComparison.Ordinal);

        var source = MvrReader.ReadFile(TestEnvironment.Fokina);
        var sourceFixtures = SourceFixtures();
        var written = MvrReader.ReadFile(path);
        var fixtures = written.AllObjects().OfType<MvrFixture>().ToList();
        Assert.Equal(91, fixtures.Count);
        int literal = 0;
        foreach (var f in fixtures)
        {
            var original = sourceFixtures[f.Uuid];
            Assert.Equal(original.GdtfMode, f.GdtfMode);
            if (f.GdtfSpec == original.GdtfSpec)
            {
                // the requested model, embedded with the file the source had for it
                literal++;
                Assert.Equal(source.FindFile(original.GdtfSpec)!.Bytes, written.FindFile(f.GdtfSpec)!.Bytes);
            }
            else
            {
                // the source's stub of a model the library has: the complete GDTF of that same model
                var match = TestEnvironment.Library.FindBySpec(original.GdtfSpec!)!;
                Assert.True(match.IsExactModel, $"{original.GdtfSpec} became {f.GdtfSpec}");
                Assert.Equal(match.Entry.FileName, f.GdtfSpec);
                Assert.NotNull(written.FindFile(f.GdtfSpec));
            }
        }
        Assert.Equal(standIns, literal);

        // opening the export resolves every fixture as opening the source did, stand-ins included
        using var reopened = TestEnvironment.CreateEngine("export-fokina-reopen");
        Assert.True(reopened.Open(path).Success);
        foreach (var (entity, fixture) in engine.Scene.Query<FixtureComponent>())
        {
            var other = reopened.Scene.Find(entity.Id)!.Get<FixtureComponent>()!;
            Assert.Equal((fixture.GdtfSpec, fixture.GdtfMode, fixture.Addresses.Single()), (other.GdtfSpec, other.GdtfMode, other.Addresses.Single()));
        }
        Assert.Equal(standIns, reopened.Settings.Substitutes.Count);
    }

    [SkippableFact]
    public void Write_stand_ins_is_off_by_default_and_reports_every_stand_in_when_on()
    {
        Assert.False(EngineExportOptions.Default.WriteStandIns);
        using var engine = TestEnvironment.CreateEngine("export-standins");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        var substitutes = engine.Settings.Substitutes.ToDictionary(s => s.EntityId);
        Skip.If(substitutes.Count == 0, "the library has every model of the scene");

        string path = NewMvrPath("standins");
        var result = engine.ExportMvr(path, new EngineExportOptions { WriteStandIns = true });
        Assert.True(result.Success, result.Summary);
        var warnings = result.Issues.Where(i => i.Code == MvrIssueCodes.SubstituteExported).ToList();
        Assert.Equal(substitutes.Count, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(IssueSeverity.Warning, w.Severity));
        Assert.Equal(substitutes.Keys.Order(), warnings.Select(w => Assert.Single(w.Entities)).Order());
        // in the Issues panel and named in the log line
        Assert.Equal(substitutes.Count, engine.Issues.Current.Count(i => i.Code == MvrIssueCodes.SubstituteExported));
        Assert.Contains(warnings[0].Message.TrimEnd('.'), result.Summary, StringComparison.Ordinal);
        Assert.Contains($"{substitutes.Count - 3} more {MvrIssueCodes.SubstituteExported}", result.Summary, StringComparison.Ordinal);

        foreach (var f in MvrReader.ReadFile(path).AllObjects().OfType<MvrFixture>())
        {
            var fixture = engine.Scene.Find(f.Uuid)!.Get<FixtureComponent>()!;
            Assert.Equal((fixture.GdtfSpec, fixture.GdtfMode), (f.GdtfSpec, f.GdtfMode));
        }
    }

    [SkippableFact]
    public void The_source_gdtfs_of_missing_models_are_kept_for_exports_after_saving_and_reopening()
    {
        string cache = TestEnvironment.NewTempDirectory("export-requested-cache");
        StageEngine Create(string cacheDirectory) => new(new StageEngineOptions
        {
            Library = TestEnvironment.Library,
            CacheDirectory = cacheDirectory,
            DmxParallelism = 2,
            SimulationParallelism = 2,
        });

        string project = Path.Combine(TestEnvironment.NewTempDirectory("export-requested"), "fokina" + StageEngine.ProjectExtension);
        int standIns;
        using (var engine = Create(cache))
        {
            Assert.True(engine.Open(TestEnvironment.Fokina).Success);
            standIns = engine.Settings.Substitutes.Count;
            Assert.True(engine.SaveAs(project).Success);
        }
        Skip.If(standIns == 0, "the library has every model of the scene");
        var source = MvrReader.ReadFile(TestEnvironment.Fokina);

        // the same machine: the stubs the MVR embedded come from the cache
        using (var engine = Create(cache))
        {
            Assert.True(engine.Open(project).Success);
            string path = NewMvrPath("requested-same-cache");
            var result = engine.ExportMvr(path);
            Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing);
            var written = MvrReader.ReadFile(path);
            foreach (var type in FokinaFixtureTypes.MissingFrom(TestEnvironment.Library))
                Assert.Equal(source.FindFile(type.Spec)!.Bytes, written.FindFile(type.Spec)!.Bytes);
        }

        // another machine: the requested models are still named, without their GDTF, and each one is reported
        using (var engine = Create(TestEnvironment.NewTempDirectory("export-requested-other-cache")))
        {
            Assert.True(engine.Open(project).Success);
            string path = NewMvrPath("requested-other-cache");
            var result = engine.ExportMvr(path);
            var missing = FokinaFixtureTypes.MissingFrom(TestEnvironment.Library);
            var reported = result.Issues.Where(i => i.Code == MvrIssueCodes.ExportResourceMissing).ToList();
            Assert.Equal(missing.Count, reported.Count);
            Assert.All(missing, t => Assert.Contains(reported, i => i.Message.Contains($"'{t.Spec}'", StringComparison.Ordinal)));
            var written = MvrReader.ReadFile(path);
            var sourceFixtures = SourceFixtures();
            foreach (var f in written.AllObjects().OfType<MvrFixture>().Where(f => missing.Any(t => t.Spec == sourceFixtures[f.Uuid].GdtfSpec)))
            {
                Assert.Equal(sourceFixtures[f.Uuid].GdtfSpec, f.GdtfSpec);
                Assert.Equal(sourceFixtures[f.Uuid].GdtfMode, f.GdtfMode);
                Assert.Null(written.FindFile(f.GdtfSpec));
            }
        }
    }

    [Fact]
    public void A_textured_model_exported_to_mvr_comes_back_with_its_texture()
    {
        string obj = SceneBuilder.WriteCubeObj(TestEnvironment.NewTempDirectory("export-crate"), "crate");
        byte[] png = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(obj)!, "tex.png"));
        using var engine = TestEnvironment.CreateEngine("export-crate");
        Assert.True(engine.Import(obj).Success);
        string path = NewMvrPath("crate");
        var result = engine.ExportMvr(path);
        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing);
        Assert.Contains("1 texture(s)", result.Summary, StringComparison.Ordinal);

        // the GLB in the file holds the image
        var glb = Assert.Single(MvrReader.ReadFile(path).Files, f => f.Name.EndsWith(".glb", StringComparison.Ordinal));
        var root = ModelRoot.ParseGLB(glb.Bytes!);
        Assert.Equal(png, Assert.Single(root.LogicalImages).Content.Content.ToArray());
        Assert.NotNull(Assert.Single(root.LogicalMaterials).FindChannel("BaseColor")!.Value.Texture);

        // opened again, the model's material has the texture and it resolves to the same pixels
        using var reopened = TestEnvironment.CreateEngine("export-crate-reopen");
        Assert.True(reopened.Open(path).Success);
        var mesh = Assert.Single(reopened.Scene.Query<MeshComponent>()).Component;
        Assert.True(reopened.Models.TryGet(mesh.ModelAssetId, out var model, out var error), error);
        string key = Assert.Single(model.Model.Materials, m => m.BaseColorTexture is not null).BaseColorTexture!;
        var image = reopened.Images.Load(key);
        Assert.NotNull(image);
        Assert.Equal(ImageImporter.Decode(png).Pixels, image!.Pixels);
    }

    /// <summary>
    /// The audit's project: Фокина opened, imported twice more (each under a group on the default layer, its "Light"
    /// fixtures on "Light") and a textured crate — 237 entities on "Default", 39 on "Light".
    /// </summary>
    private static StageEngine RichProject()
    {
        var engine = TestEnvironment.CreateEngine("export-rich");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.True(engine.Import(TestEnvironment.Fokina).Success);
        Assert.True(engine.Import(TestEnvironment.Fokina).Success);
        Assert.True(engine.Import(SceneBuilder.WriteCubeObj(TestEnvironment.NewTempDirectory("export-rich-crate"), "crate")).Success);
        return engine;
    }

    private static Dictionary<string, int> LayerCounts(Scene scene) =>
        scene.All().GroupBy(e => scene.FindLayer(e.LayerId)!.Name).ToDictionary(g => g.Key, g => g.Count());

    [Fact]
    public void Layer_membership_survives_export_and_reopening()
    {
        using var engine = RichProject();
        var counts = LayerCounts(engine.Scene);
        Assert.Equal(new Dictionary<string, int> { ["Default"] = 237, ["Light"] = 39 }, counts);

        string path = NewMvrPath("rich");
        var result = engine.ExportMvr(path);
        Assert.True(result.Success, result.Summary);
        var split = Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.LayerSplit);
        Assert.Equal(2, split.Entities.Count); // the two merge groups have children on "Light"
        Assert.Contains(split.Message.TrimEnd('.'), result.Summary, StringComparison.Ordinal);

        using var reopened = TestEnvironment.CreateEngine("export-rich-reopen");
        Assert.True(reopened.Open(path).Success);
        Assert.Equal(counts, LayerCounts(reopened.Scene));
        // the same entities on the same layers under the same parents, children in their order (top-level objects come
        // back layer by layer, as MVR lists them); every fixture with the type and mode it had
        Assert.Equal(engine.Scene.All().Select(e => (e.Id, e.Parent?.Id, e.LayerId)).Order(),
            reopened.Scene.All().Select(e => (e.Id, e.Parent?.Id, e.LayerId)).Order());
        foreach (var e in engine.Scene.All().Where(e => e.Children.Count > 0))
            Assert.Equal(e.Children.Select(c => c.Id), reopened.Scene.Find(e.Id)!.Children.Select(c => c.Id));
        Assert.Equal(engine.Scene.Roots.GroupBy(r => r.LayerId).Select(g => (g.Key, g.Select(r => r.Id).ToList())).OrderBy(g => g.Key).Select(g => string.Join(",", g.Item2)),
            reopened.Scene.Roots.GroupBy(r => r.LayerId).Select(g => (g.Key, g.Select(r => r.Id).ToList())).OrderBy(g => g.Key).Select(g => string.Join(",", g.Item2)));
        foreach (var (entity, fixture) in engine.Scene.Query<FixtureComponent>())
        {
            var other = reopened.Scene.Find(entity.Id)!.Get<FixtureComponent>()!;
            Assert.Equal((fixture.GdtfSpec, fixture.GdtfMode), (other.GdtfSpec, other.GdtfMode));
            Assert.True(Vector3.Distance(entity.WorldPosition, reopened.Scene.Find(entity.Id)!.WorldPosition) < 1e-3f);
        }
        Assert.Equal(engine.Settings.Substitutes.Count, reopened.Settings.Substitutes.Count);
    }

    [Fact]
    public void Every_change_the_export_cannot_avoid_is_in_the_issues_and_in_the_log_line()
    {
        using var engine = TestEnvironment.CreateEngine("export-losses");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        // content MVR cannot hold: a reference drawing and a camera
        string folder = TestEnvironment.NewTempDirectory("export-losses-image");
        string poster = Path.Combine(folder, "poster.png");
        File.WriteAllBytes(poster, ImageImporter.EncodePng(new ImageData { Width = 2, Height = 1, Pixels = [255, 0, 0, 255, 0, 0, 255, 255] }));
        Assert.True(engine.Import(poster).Success);
        engine.Scene.Add(new Entity("Camera")).Add(new CameraComponent());

        var result = engine.ExportMvr(NewMvrPath("losses"), new EngineExportOptions { WriteStandIns = true });
        Assert.True(result.Success, result.Summary);
        var losses = result.Issues.Where(i => i.Severity >= IssueSeverity.Warning || i.Code is MvrIssueCodes.NotRepresentable or MvrIssueCodes.LayerSplit).ToList();
        Assert.Contains(losses, i => i.Message.Contains("images", StringComparison.Ordinal));
        Assert.Contains(losses, i => i.Message.Contains("cameras", StringComparison.Ordinal));
        foreach (var group in losses.GroupBy(i => i.Code))
        {
            // the Issues panel lists each of them; the log line names each kind
            Assert.All(group, i => Assert.Contains(engine.Issues.Current, c => c.Code == i.Code && c.Message == i.Message));
            Assert.Contains(group.First().Message.TrimEnd('.'), result.Summary, StringComparison.Ordinal);
        }
    }
}

/// <summary>The exported MVR of a textured model renders with its texture, like the project it came from (Vulkan; skipped without a device).</summary>
[Collection(VulkanCollection.Name)]
public sealed class MvrExportTextureRenderTests
{
    private static (double MeanDifference, int GreenPixels) Compare(string a, string b)
    {
        var first = ImageImporter.Load(a);
        var second = ImageImporter.Load(b);
        Assert.Equal((first.Width, first.Height), (second.Width, second.Height));
        double sum = 0;
        int green = 0;
        for (int i = 0; i < first.Width * first.Height; i++)
        {
            for (int c = 0; c < 3; c++) sum += Math.Abs(first.Pixels[4 * i + c] - second.Pixels[4 * i + c]) / 255.0;
            byte r = second.Pixels[4 * i], g = second.Pixels[4 * i + 1], bl = second.Pixels[4 * i + 2];
            if (g > 24 && g > r * 1.5 && g > bl * 1.5) green++;
        }
        return (sum / (3.0 * first.Width * first.Height), green);
    }

    [SkippableFact]
    public void The_exported_mvr_renders_like_the_project()
    {
        try
        {
            using var probe = Rendering.Vulkan.VulkanRenderer.Create(new Rendering.Vulkan.VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
                MinimumLogLevel = Rendering.Vulkan.VulkanLogLevel.Error,
            });
        }
        catch (Rendering.Vulkan.VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
        }

        string dir = TestEnvironment.NewTempDirectory("export-render");
        string obj = SceneBuilder.WriteCubeObj(Path.Combine(dir, "model"), "crate");
        string project = Path.Combine(dir, "crate" + StageEngine.ProjectExtension);
        string mvr = Path.Combine(dir, "crate.mvr");
        using (var engine = TestEnvironment.CreateEngine("export-render"))
        {
            Assert.True(engine.Import(obj).Success);
            Assert.True(engine.SaveAs(project).Success);
            Assert.True(engine.ExportMvr(mvr).Success);
        }

        string Render(string scene, string name)
        {
            string png = Path.Combine(dir, name + ".png");
            var output = new StringWriter();
            var error = new StringWriter();
            int code = Cli.Program.Run(
                ["render", scene, "--out", png, "--width", "96", "--height", "96", "--frames", "1", "--mode", "full", "--floor", "none",
                    "--work-light", "on", "--camera", "2.2,-2.6,2.0", "--target", "0.5,0.5,0.5",
                    "--cache", Path.Combine(dir, "cache"), "--library", TestEnvironment.Root],
                output, error);
            Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
            return png;
        }

        var (difference, green) = Compare(Render(project, "project"), Render(mvr, "mvr"));
        // the texture's green quarter is on the crate, and the MVR looks like the project it was exported from
        Assert.True(green > 20, $"only {green} green pixels: the texture is missing");
        Assert.True(difference < 0.02, $"mean difference {difference:0.0000} between the project and the MVR render");
    }
}
