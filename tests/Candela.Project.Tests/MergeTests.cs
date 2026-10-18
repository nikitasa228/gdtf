using System.Numerics;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

public sealed class MergeTests
{
    [Fact]
    public void Merging_a_project_into_itself_remaps_every_reference_without_collisions()
    {
        var project = TestData.RichProject();
        var originals = project.Scene.All().ToList();
        var originalIds = originals.Select(e => e.Id).ToHashSet();
        var originalNodeIds = project.Scene.Query<CableComponent>().SelectMany(c => c.Component.Nodes).Select(n => n.Id).ToHashSet();
        var assetCount = project.Assets.Count;
        var before = TestData.SceneJson(project.Scene);

        var result = ProjectMerge.Import(project, project);

        // One new group holding a full copy; all ids are new and unique.
        Assert.Equal(originals.Count * 2 + 1, project.Scene.Count);
        Assert.Contains(result.Group, project.Scene.Roots);
        Assert.Equal("Rich stage", result.Group.Name);
        Assert.NotNull(result.Group.Get<GroupComponent>());
        Assert.Equal(originals.Count, result.EntityIds.Count);
        var imported = result.Group.SelfAndDescendants().Skip(1).ToList();
        Assert.Equal(originals.Count, imported.Count);
        Assert.All(imported, e => Assert.DoesNotContain(e.Id, originalIds));
        Assert.Equal(project.Scene.Count, project.Scene.All().Select(e => e.Id).Distinct().Count());

        // Structure, names and transforms are copied; the originals are unchanged.
        foreach (var original in originals)
        {
            var copy = project.Scene.Find(result.EntityIds[original.Id])!;
            Assert.Equal(original.Name, copy.Name);
            Assert.Equal(original.Local, copy.Local);
            Assert.Equal(original.LayerId, copy.LayerId);
            Assert.Equal(original.Visible, copy.Visible);
            Assert.Equal(original.Locked, copy.Locked);
            var expectedParent = original.Parent is null ? result.Group.Id : result.EntityIds[original.Parent.Id];
            Assert.Equal(expectedParent, copy.Parent!.Id);
            Assert.Equal(original.Components.Select(c => c.GetType()), copy.Components.Select(c => c.GetType()));
        }
        Assert.Equal(before, TestData.SceneJson(SceneWithout(project.Scene, result.Group)));

        // Every entity reference of the copy points into the copy, at the copy of the original target.
        int checkedRefs = 0;
        foreach (var original in originals)
        {
            var copy = project.Scene.Find(result.EntityIds[original.Id])!;
            var a = EntityReferences(original).ToList();
            var b = EntityReferences(copy).ToList();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(result.EntityIds[a[i]], b[i]);
                Assert.Contains(project.Scene.Find(b[i])!, imported);
                checkedRefs++;
            }
        }
        Assert.Equal(7, checkedRefs); // mounted-on, carries, 2 cable anchors, 3 cable endpoints

        // Cable node ids are new, and segments/endpoints still connect the copied nodes.
        var cableCopy = imported.Single(e => e.Name == "DMX run").Get<CableComponent>()!;
        Assert.All(cableCopy.Nodes, n => Assert.DoesNotContain(n.Id, originalNodeIds));
        var nodeIds = cableCopy.Nodes.Select(n => n.Id).ToHashSet();
        Assert.Equal(3, cableCopy.Segments.Count);
        Assert.All(cableCopy.Segments, s => Assert.True(nodeIds.Contains(s.FromNode) && nodeIds.Contains(s.ToNode)));
        Assert.Equal(3, cableCopy.Endpoints.Count);
        Assert.All(cableCopy.Endpoints, ep => Assert.Contains(ep.NodeId, nodeIds));

        // Identical assets and GDTFs are reused, not duplicated; asset references are unchanged.
        Assert.Empty(result.AddedAssets);
        Assert.Empty(result.AddedGdtfs);
        Assert.Empty(result.AddedLayers);
        Assert.Equal(assetCount, project.Assets.Count);
        Assert.All(result.AssetIds, kv => Assert.Equal(kv.Key, kv.Value));
        Assert.Equal(TestData.GdtfB, result.GdtfNames[TestData.GdtfB]);
        Assert.Empty(result.Issues);

        // The merged project saves and loads (no duplicate ids anywhere).
        using var dir = new TempDir();
        var path = dir.File("merged.candela");
        ProjectFile.Save(path, project, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var loaded = ProjectFile.Load(path);
        Assert.Empty(loaded.LoadIssues);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(loaded.Scene));
    }

    [Fact]
    public void Merging_twice_never_collides()
    {
        var target = TestData.RichProject();
        var source = TestData.RichProject();
        var first = ProjectMerge.Import(target, source, new ProjectMergeOptions { ReuseIdenticalAssets = false });
        var second = ProjectMerge.Import(target, source, new ProjectMergeOptions { ReuseIdenticalAssets = false });
        Assert.Equal(target.Scene.Count, target.Scene.All().Select(e => e.Id).Distinct().Count());
        Assert.Empty(first.EntityIds.Values.Intersect(second.EntityIds.Values));
        // The source's asset ids are free in the target the first time and taken the second time.
        Assert.Equal(source.Assets.Entries.Select(a => a.Id), first.AddedAssets);
        Assert.Empty(second.AddedAssets.Intersect(first.AddedAssets));
        Assert.Equal(3 * source.Assets.Count, target.Assets.Count);
        // With reuse (the default) identical content is never stored again.
        Assert.Empty(ProjectMerge.Import(target, source).AddedAssets);
    }

    [Fact]
    public void Assets_without_reuse_get_new_ids_and_every_asset_reference_follows()
    {
        var project = TestData.RichProject();
        var result = ProjectMerge.Import(project, project, new ProjectMergeOptions { ReuseIdenticalAssets = false });
        Assert.Equal(4, result.AddedAssets.Count);
        Assert.Equal(8, project.Assets.Count);
        Assert.All(result.AssetIds, kv => Assert.NotEqual(kv.Key, kv.Value));

        var group = result.Group.SelfAndDescendants().ToList();
        var mesh = group.Select(e => e.Get<MeshComponent>()).OfType<MeshComponent>().Single();
        var image = group.Select(e => e.Get<ImageComponent>()).OfType<ImageComponent>().Single();
        var truss = group.Select(e => e.Get<TrussComponent>()).OfType<TrussComponent>().Single();
        Assert.Contains(mesh.ModelAssetId, result.AddedAssets);
        Assert.Equal(mesh.ModelAssetId, truss.ModelAssetId);
        Assert.Contains(image.ImageAssetId, result.AddedAssets);
        var texture = project.Assets.Entries.Single(a => a.Kind == AssetKind.Texture && result.AddedAssets.Contains(a.Id));
        Assert.Equal(texture.ImageKey, mesh.MaterialOverrides[1]);
        Assert.Equal(MaterialIds.WoodStage, mesh.MaterialOverrides[0]);
        Assert.Equal(project.Assets.Find(mesh.ModelAssetId)!.Data, project.Assets.Entries.First(a => a.Kind == AssetKind.Model).Data);
        Assert.NotNull(project.Assets.Load(mesh.MaterialOverrides[1]));
    }

    [Fact]
    public void Colliding_asset_id_and_gdtf_name_are_renamed()
    {
        var sharedId = Guid.NewGuid();
        var target = new ProjectData(new Scene("Target"));
        target.Assets.Add(AssetKind.Image, "logo.png", TestData.Png(seed: 1), id: sharedId);
        target.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfA)!;

        var source = new ProjectData(new Scene("Source"));
        source.Assets.Add(AssetKind.Image, "other.png", TestData.Png(seed: 2), id: sharedId);
        source.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfB)!; // same name, different file
        var poster = new Entity("Poster");
        poster.Add(new ImageComponent { ImageAssetId = sharedId });
        source.Scene.Add(poster);
        var fixture = new Entity("Fixture");
        fixture.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA });
        source.Scene.Add(fixture);

        var result = ProjectMerge.Import(target, source);
        var newAsset = Assert.Single(result.AddedAssets);
        Assert.NotEqual(sharedId, newAsset);
        Assert.Equal(TestData.Png(seed: 1), target.Assets.GetBytes(sharedId));
        Assert.Equal(TestData.Png(seed: 2), target.Assets.GetBytes(newAsset));
        Assert.Equal(newAsset, target.Scene.Find(result.EntityIds[poster.Id])!.Get<ImageComponent>()!.ImageAssetId);

        var renamed = "Briteq@BT-FOG1500@20240710 (2).gdtf";
        Assert.Equal(renamed, result.GdtfNames[TestData.GdtfA]);
        Assert.Equal(new[] { renamed }, result.AddedGdtfs);
        Assert.Equal(TestData.LibraryGdtf(TestData.GdtfB), target.GdtfFiles[renamed]);
        Assert.Equal(TestData.LibraryGdtf(TestData.GdtfA), target.GdtfFiles[TestData.GdtfA]);
        Assert.Equal(renamed, target.Scene.Find(result.EntityIds[fixture.Id])!.Get<FixtureComponent>()!.GdtfSpec);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ProjectIssueCodes.MergeGdtfRenamed, issue.Code);
        Assert.Equal(new[] { result.EntityIds[fixture.Id] }, issue.Entities);
    }

    [Fact]
    public void Library_file_of_the_same_name_counts_as_a_collision()
    {
        var target = new ProjectData(new Scene("Target"));
        var source = new ProjectData(new Scene("Source"));
        source.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfB)!;
        source.GdtfFiles[TestData.GdtfB] = TestData.LibraryGdtf(TestData.GdtfB)!;

        var result = ProjectMerge.Import(target, source, new ProjectMergeOptions { TargetGdtfProvider = TestData.LibraryGdtf });
        Assert.NotEqual(TestData.GdtfA, result.GdtfNames[TestData.GdtfA]);
        Assert.Equal(TestData.GdtfB, result.GdtfNames[TestData.GdtfB]);
        Assert.Equal(2, target.GdtfFiles.Count);
    }

    [Fact]
    public void Dangling_references_are_cleared_and_reported()
    {
        var source = new ProjectData(new Scene("Broken refs"));
        var fixture = new Entity("Floating fixture");
        fixture.Add(new FixtureComponent { MountedOn = Guid.NewGuid() });
        source.Scene.Add(fixture);
        var hoist = new Entity("Hoist");
        var real = new Entity("Truss");
        source.Scene.Add(real);
        hoist.Add(new RiggingPointComponent { Carries = { Guid.NewGuid(), real.Id } });
        source.Scene.Add(hoist);
        var cableEntity = new Entity("Cable");
        var n1 = new CableNode { Anchor = new CableAnchor(Guid.NewGuid(), Vector3.Zero), Position = new Vector3(1, 2, 3) };
        var n2 = new CableNode();
        cableEntity.Add(new CableComponent
        {
            Nodes = { n1, n2 },
            Segments = { new CableSegment(n1.Id, n2.Id), new CableSegment(n2.Id, Guid.NewGuid()) },
            Endpoints = { new CableEndpoint { NodeId = n2.Id, EntityId = Guid.NewGuid(), PortName = "In" } },
        });
        source.Scene.Add(cableEntity);
        var mesh = new Entity("Lost mesh");
        mesh.Add(new MeshComponent { ModelAssetId = Guid.NewGuid() });
        source.Scene.Add(mesh);

        var target = new ProjectData(new Scene("Target"));
        var result = ProjectMerge.Import(target, source);
        Assert.Equal(5, result.Issues.Count(i => i.Code == ProjectIssueCodes.MergeReferenceDangling));
        Assert.All(result.Issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));

        Entity Copy(Entity e) => target.Scene.Find(result.EntityIds[e.Id])!;
        Assert.Null(Copy(fixture).Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(new[] { result.EntityIds[real.Id] }, Copy(hoist).Get<RiggingPointComponent>()!.Carries);
        var cable = Copy(cableEntity).Get<CableComponent>()!;
        Assert.Null(cable.Nodes[0].Anchor);
        Assert.Equal(new Vector3(1, 2, 3), cable.Nodes[0].Position);
        Assert.Single(cable.Segments);
        Assert.Empty(cable.Endpoints);
        Assert.Equal(Guid.Empty, Copy(mesh).Get<MeshComponent>()!.ModelAssetId);
        Assert.Contains(result.Issues, i => i.Entities.SequenceEqual(new[] { Copy(fixture).Id }));
    }

    [Fact]
    public void Import_is_undoable_and_redoable()
    {
        var target = TestData.RichProject();
        var beforeScene = TestData.SceneJson(target.Scene);
        var beforeAssets = target.Assets.Entries.Select(a => a.Id).ToList();
        var beforeGdtfs = target.GdtfFiles.Keys.ToList();
        var beforeLayers = target.Scene.Layers.Select(l => l.Id).ToList();

        var source = TestData.RichProject();
        source.Scene.Layers.Add(new Layer { Name = "Pyro" });
        var pyro = new Entity("Flame") { LayerId = source.Scene.Layers[^1].Id };
        source.Scene.Add(pyro);
        source.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfA)!;

        var undo = new UndoStack();
        var countBefore = target.Scene.Count;
        var command = ProjectMerge.CreateCommand(target, source, new ProjectMergeOptions { ReuseIdenticalAssets = false });
        Assert.Equal("Import Rich stage", command.Description);
        undo.Execute(command);
        var afterScene = TestData.SceneJson(target.Scene);
        Assert.Equal(countBefore + source.Scene.Count + 1, target.Scene.Count);
        Assert.Single(command.Result.AddedLayers); // "Pyro"; "Light" and "Set" exist by name
        Assert.Equal(4, command.Result.AddedAssets.Count);
        Assert.Equal(new[] { TestData.GdtfA }, command.Result.AddedGdtfs);
        Assert.Equal(command.Result.AddedLayers[0].Id, target.Scene.Find(command.Result.EntityIds[pyro.Id])!.LayerId);
        var light = target.Scene.Layers.Single(l => l.Name == "Light");
        Assert.Equal(light.Id, target.Scene.Find(command.Result.EntityIds[source.Scene.All().Single(e => e.Name == "Spot 1").Id])!.LayerId);

        undo.Undo();
        Assert.Equal(beforeScene, TestData.SceneJson(target.Scene));
        Assert.Equal(beforeAssets, target.Assets.Entries.Select(a => a.Id));
        Assert.Equal(beforeGdtfs, target.GdtfFiles.Keys);
        Assert.Equal(beforeLayers, target.Scene.Layers.Select(l => l.Id));

        undo.Redo();
        Assert.Equal(afterScene, TestData.SceneJson(target.Scene));
        Assert.Equal(beforeAssets.Count + 4, target.Assets.Count);
    }

    [Fact]
    public void Placement_and_parent_move_world_space_points_with_the_group()
    {
        var target = new ProjectData(new Scene("Venue"));
        var stage = new Entity("Stage");
        stage.Local = new Transform(new Vector3(10, 0, 0));
        target.Scene.Add(stage);

        var source = new ProjectData(new Scene("Rig"));
        var spot = new Entity("Spot");
        spot.Add(new FixtureComponent { FocusPoint = new Vector3(1, 2, 0) });
        source.Scene.Add(spot);
        var cam = new Entity("Cam");
        cam.Add(new CameraComponent { Target = new Vector3(0, 0, 1) });
        source.Scene.Add(cam);

        var placement = new Transform(new Vector3(0, 5, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Vector3.One);
        var result = ProjectMerge.Import(target, source, new ProjectMergeOptions { ParentId = stage.Id, Placement = placement, GroupName = "Touring rig" });
        Assert.Equal("Touring rig", result.Group.Name);
        Assert.Same(stage, result.Group.Parent);
        Assert.Equal(placement, result.Group.Local);

        var focus = target.Scene.Find(result.EntityIds[spot.Id])!.Get<FixtureComponent>()!.FocusPoint!.Value;
        // (1,2,0) rotated 90° about Z → (−2,1,0), + (0,5,1) → (−2,6,1), + stage (10,0,0) → (8,6,1).
        AssertNear(new Vector3(8, 6, 1), focus);
        AssertNear(new Vector3(10, 5, 2), target.Scene.Find(result.EntityIds[cam.Id])!.Get<CameraComponent>()!.Target!.Value);

        Assert.Throws<ArgumentException>(() => ProjectMerge.Import(target, source, new ProjectMergeOptions { ParentId = Guid.NewGuid() }));
    }

    [Fact]
    public void Fixture_ids_can_be_renumbered_above_the_ones_in_use()
    {
        var project = TestData.RichProject();
        var kept = ProjectMerge.Import(project, TestData.RichProject());
        var keptIds = kept.Group.SelfAndDescendants().Select(e => e.Get<FixtureComponent>()).OfType<FixtureComponent>().Select(f => f.FixtureId);
        Assert.Equal(new[] { 101, 900 }, keptIds.Order());

        var renumbered = ProjectMerge.Import(project, TestData.RichProject(), new ProjectMergeOptions { RenumberFixtureIds = true });
        var ids = renumbered.Group.SelfAndDescendants().Select(e => e.Get<FixtureComponent>()).OfType<FixtureComponent>().Select(f => f.FixtureId);
        Assert.Equal(new[] { 901, 902 }, ids.Order());
    }

    [Fact]
    public void Import_from_a_file_carries_its_load_issues_with_translated_ids()
    {
        using var dir = new TempDir();
        var source = TestData.RichProject();
        var path = dir.File("Rig v2.candela");
        source.Scene.Name = "Untitled";
        ProjectFile.Save(path, source, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var image = source.Assets.Entries.Single(a => a.Kind == AssetKind.Image);
        TestData.RewriteArchive(path, (name, data) => name == $"assets/{image.Id:D}/plan.png" ? null : data);

        var target = new ProjectData(new Scene("Venue"));
        var result = ProjectMerge.Import(target, path);
        Assert.Equal("Rig v2", result.Group.Name);
        var poster = source.Scene.Query<ImageComponent>().Single().Entity;
        var missing = Assert.Single(result.Issues, i => i.Code == ProjectIssueCodes.AssetMissing);
        Assert.Equal(new[] { result.EntityIds[poster.Id] }, missing.Entities);
        Assert.Contains(result.Issues, i => i.Code == ProjectIssueCodes.MergeReferenceDangling);
        Assert.Equal(source.Scene.Count + 1, target.Scene.Count);
        Assert.Equal(2, target.GdtfFiles.Count);
    }

    [Fact]
    public void A_fixture_using_a_case_variant_name_follows_its_renamed_gdtf()
    {
        var target = new ProjectData(new Scene("Target"));
        target.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfA)!;
        var source = new ProjectData(new Scene("Source"));
        source.GdtfFiles[TestData.GdtfA] = TestData.LibraryGdtf(TestData.GdtfB)!; // same name, different file
        var lower = new Entity("Lower-case spec");
        lower.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA.ToLowerInvariant() });
        source.Scene.Add(lower);
        var exact = new Entity("Exact spec");
        exact.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA });
        source.Scene.Add(exact);
        Assert.Same(source.GdtfFiles[TestData.GdtfA], source.FindGdtf(TestData.GdtfA.ToLowerInvariant()));

        var result = ProjectMerge.Import(target, source);
        foreach (var e in new[] { lower, exact })
        {
            var spec = target.Scene.Find(result.EntityIds[e.Id])!.Get<FixtureComponent>()!.GdtfSpec;
            Assert.Equal(TestData.LibraryGdtf(TestData.GdtfB), target.FindGdtf(spec)); // the imported file, not the project's own
        }
        var issue = Assert.Single(result.Issues, i => i.Code == ProjectIssueCodes.MergeGdtfRenamed);
        Assert.Equal(new[] { result.EntityIds[lower.Id], result.EntityIds[exact.Id] }.Order(), issue.Entities.Order());
    }

    [Fact]
    public void Data_this_version_cannot_use_is_reported_and_not_imported()
    {
        using var dir = new TempDir();
        var source = TestData.RichProject();
        var path = dir.File("newer.candela");
        ProjectFile.Save(path, source, new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var image = source.Assets.Entries.Single(a => a.Kind == AssetKind.Image);
        var console = source.Scene.All().Single(e => e.Name == "Console");
        TestData.RewriteArchive(path, (name, data) =>
        {
            if (name == "assets/index.json") return TestData.Utf8(System.Text.Encoding.UTF8.GetString(data).Replace("\"kind\": \"Image\"", "\"kind\": \"Video\""));
            if (name != "scene.json") return data;
            var scene = System.Text.Json.Nodes.JsonNode.Parse(data)!.AsObject();
            var entity = scene["entities"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == console.Id.ToString("D"))!;
            entity["components"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""{"$type":"laserProjector"}"""));
            return TestData.Utf8(scene.ToJsonString());
        });
        var loaded = ProjectFile.Load(path);
        Assert.Single(loaded.KeptAssetIds);
        Assert.Single(loaded.UnknownComponents);

        // Into another project: the unknown component and the kept asset stay behind, with issues that say so.
        var target = new ProjectData(new Scene("Venue"));
        var result = ProjectMerge.Import(target, loaded);
        Assert.Empty(target.UnknownComponents);
        Assert.Empty(target.KeptAssetIds);
        var component = Assert.Single(result.Issues, i => i.Code == ProjectIssueCodes.SceneComponentUnknown);
        Assert.Contains("not imported", component.Message);
        Assert.Equal(new[] { result.EntityIds[console.Id] }, component.Entities);
        Assert.Contains("not imported", Assert.Single(result.Issues, i => i.Code == ProjectIssueCodes.AssetKept).Message);
        var poster = source.Scene.Query<ImageComponent>().Single().Entity;
        Assert.Equal(Guid.Empty, target.Scene.Find(result.EntityIds[poster.Id])!.Get<ImageComponent>()!.ImageAssetId);

        // Into itself: the project keeps the asset, so the copy keeps its reference; the file still saves everything.
        var self = ProjectMerge.Import(loaded, loaded);
        Assert.Equal(image.Id, loaded.Scene.Find(self.EntityIds[poster.Id])!.Get<ImageComponent>()!.ImageAssetId);
        Assert.DoesNotContain(self.Issues, i => i.Code == ProjectIssueCodes.AssetKept);
        var again = dir.File("again.candela");
        ProjectFile.Save(again, loaded);
        var reloaded = ProjectFile.Load(again);
        Assert.Equal(new[] { image.Id }, reloaded.KeptAssetIds);
        Assert.Single(reloaded.UnknownComponents);
    }

    [Fact]
    public void Merge_handles_every_component_type_of_core()
    {
        // When Core gains a component type, ProjectMerge's ReferenceRemapper must be reviewed for references it carries.
        var reviewed = new[]
        {
            typeof(FixtureComponent), typeof(MeshComponent), typeof(WallComponent), typeof(ImageComponent), typeof(TrussComponent),
            typeof(RiggingPointComponent), typeof(CableComponent), typeof(ConsoleComponent), typeof(PowerSourceComponent),
            typeof(AtmosphereVolumeComponent), typeof(CameraComponent), typeof(GroupComponent),
        };
        Assert.Equal(reviewed.Select(t => t.Name).Order(), ComponentTypes.All.Select(t => t.Name).Order());
    }

    private static IEnumerable<Guid> EntityReferences(Entity e)
    {
        foreach (var c in e.Components)
        {
            switch (c)
            {
                case FixtureComponent { MountedOn: { } m }:
                    yield return m;
                    break;
                case RiggingPointComponent r:
                    foreach (var id in r.Carries) yield return id;
                    break;
                case CableComponent cable:
                    foreach (var n in cable.Nodes)
                        if (n.Anchor is { } a) yield return a.EntityId;
                    foreach (var ep in cable.Endpoints) yield return ep.EntityId;
                    break;
            }
        }
    }

    /// <summary>A copy of the scene without one subtree (to compare the untouched originals).</summary>
    private static Scene SceneWithout(Scene scene, Entity subtree)
    {
        var json = TestData.SceneJson(scene);
        var copy = Core.Serialization.SceneSerializer.FromJson(json);
        copy.Remove(copy.Find(subtree.Id)!);
        return copy;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
}
