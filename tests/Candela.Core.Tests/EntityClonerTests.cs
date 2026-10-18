using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Core.Tests;

/// <summary>
/// <see cref="EntityCloner"/>: deep copies of subtrees with new ids, references among the copies rewritten, references
/// to other entities kept, removed or kept when resolvable — the building block of Duplicate, Copy/Paste and merge import.
/// </summary>
public class EntityClonerTests
{
    /// <summary>
    /// Stage: a rigging point carrying a truss with two fixtures, a free-standing fixture mounted on the truss from outside,
    /// a cable from the first fixture to a console, anchored on the truss and on a wall.
    /// </summary>
    private sealed class Stage
    {
        public Stage()
        {
            Scene = new Scene("Stage");
            Upper = new Layer { Name = "Upper" };
            Scene.Layers.Add(Upper);
            Hoist = Scene.Add(new Entity("Hoist") { Local = new Transform(new Vector3(0, 4, 8)) });
            Truss = Scene.Add(new Entity("Truss") { Local = new Transform(new Vector3(0, 4, 6)), LayerId = Upper.Id });
            Truss.Add(new TrussComponent { Length = 6 });
            Hoist.Add(new RiggingPointComponent { Carries = { Truss.Id } });
            SpotA = Scene.Add(new Entity("Spot A") { Local = new Transform(new Vector3(1, 0, -0.3f)) }, Truss);
            SpotA.Add(new FixtureComponent { GdtfSpec = "a.gdtf", MountedOn = Truss.Id, FocusPoint = new Vector3(1, 2, 0) });
            SpotB = Scene.Add(new Entity("Spot B") { Local = new Transform(new Vector3(2, 0, -0.3f)), Visible = false, Locked = true }, Truss);
            SpotB.Add(new FixtureComponent { GdtfSpec = "b.gdtf", MountedOn = Truss.Id });
            Outside = Scene.Add(new Entity("Outside spot") { Local = new Transform(new Vector3(5, 4, 6)) });
            Outside.Add(new FixtureComponent { MountedOn = Truss.Id });
            Wall = Scene.Add(new Entity("Wall") { Local = new Transform(new Vector3(10, 0, 0)) });
            Wall.Add(new WallComponent());
            Console = Scene.Add(new Entity("Console") { Local = new Transform(new Vector3(0, -10, 0)) });
            Console.Add(new ConsoleComponent { ProtocolId = null });
            CableEntity = Scene.Add(new Entity("Cable") { Local = new Transform(new Vector3(1, 0, 0)) });
            var n1 = new CableNode { Position = new Vector3(0, 0, 0), Anchor = new CableAnchor(Truss.Id, new Vector3(1, 0, 0)) };
            var n2 = new CableNode { Position = new Vector3(3, 0, 0), Anchor = new CableAnchor(Wall.Id, new Vector3(0, 0, 1)) };
            var n3 = new CableNode { Position = new Vector3(0, -9, 0) };
            Cable = CableEntity.Add(new CableComponent
            {
                CableTypeId = "dmx-xlr5",
                Nodes = { n1, n2, n3 },
                Segments = { new CableSegment(n1.Id, n2.Id), new CableSegment(n2.Id, n3.Id) },
                Endpoints =
                {
                    new CableEndpoint { NodeId = n1.Id, EntityId = SpotA.Id, PortName = "DMX In" },
                    new CableEndpoint { NodeId = n3.Id, EntityId = Console.Id, PortName = "DMX A" },
                },
            });
        }

        public Scene Scene { get; }
        public Layer Upper { get; }
        public Entity Hoist { get; }
        public Entity Truss { get; }
        public Entity SpotA { get; }
        public Entity SpotB { get; }
        public Entity Outside { get; }
        public Entity Wall { get; }
        public Entity Console { get; }
        public Entity CableEntity { get; }
        public CableComponent Cable { get; }
    }

    [Fact]
    public void Duplicate_in_the_same_scene_points_internal_references_at_the_copies_and_keeps_the_others()
    {
        var s = new Stage();
        var result = EntityCloner.Clone(new[] { s.Truss, s.Outside }, new EntityCloneOptions { TargetScene = s.Scene });

        Assert.Equal(new[] { s.Truss, s.Outside }, result.Originals);
        Assert.Equal(4, result.Count);
        var truss = result.Roots[0];
        var outside = result.Roots[1];
        Assert.Null(truss.Scene);
        Assert.Equal(2, truss.Children.Count);
        Assert.All(result.IdMap, p => Assert.NotEqual(p.Key, p.Value));
        Assert.All(result.IdMap.Values, id => Assert.Null(s.Scene.Find(id)));

        // Internal references follow the copies.
        Assert.Equal(truss.Id, truss.Children[0].Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(truss.Id, outside.Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(3, result.ReferencesRemapped);
        Assert.Empty(result.ClearedReferences);
        Assert.Empty(result.Issues);
        Assert.Same(truss.Children[1], result.FindCopy(s.SpotB.Id));

        // Entity state is copied: names, flags, layers, local transforms, component values.
        var spotB = truss.Children[1];
        Assert.Equal("Spot B", spotB.Name);
        Assert.False(spotB.Visible);
        Assert.True(spotB.Locked);
        Assert.Equal(s.Upper.Id, truss.LayerId);
        Assert.Equal(s.SpotB.Local, spotB.Local);
        Assert.Equal(s.Truss.Local, truss.Local);
        Assert.Equal(new Vector3(1, 2, 0), truss.Children[0].Get<FixtureComponent>()!.FocusPoint);
        Assert.Equal("a.gdtf", truss.Children[0].Get<FixtureComponent>()!.GdtfSpec);

        // Deep copies: editing a copy leaves the original alone.
        truss.Get<TrussComponent>()!.Length = 1;
        Assert.Equal(6, s.Truss.Get<TrussComponent>()!.Length);
        Assert.Equal(s.Truss.Id, s.SpotA.Get<FixtureComponent>()!.MountedOn);

        // The copies can be added next to the originals; the scene then holds both.
        var count = s.Scene.Count;
        foreach (var root in result.Roots) s.Scene.Add(root);
        Assert.Equal(count + 4, s.Scene.Count);
    }

    [Fact]
    public void A_copy_of_a_fixture_alone_still_hangs_on_the_original_truss()
    {
        var s = new Stage();
        var result = EntityCloner.Clone(s.SpotA, new EntityCloneOptions { TargetScene = s.Scene });

        var copy = Assert.Single(result.Roots);
        Assert.Equal(s.Truss.Id, copy.Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(1, result.ReferencesKept);
        Assert.Equal(0, result.ReferencesRemapped);
    }

    [Fact]
    public void Paste_into_another_scene_removes_references_to_entities_that_were_not_copied()
    {
        var s = new Stage();
        var target = new Scene("Other");
        var result = EntityCloner.Clone(new[] { s.SpotA, s.Hoist, s.CableEntity },
            new EntityCloneOptions { TargetScene = target, ExternalReferences = ExternalReferenceHandling.Clear, RootsKeepWorldTransform = true });

        var spot = result.Roots[0];
        var hoist = result.Roots[1];
        var cableEntity = result.Roots[2];
        Assert.Null(spot.Get<FixtureComponent>()!.MountedOn);
        Assert.Empty(hoist.Get<RiggingPointComponent>()!.Carries);
        var cable = cableEntity.Get<CableComponent>()!;
        Assert.All(cable.Nodes, n => Assert.Null(n.Anchor));
        var end = Assert.Single(cable.Endpoints); // the spot was copied: its end stays plugged, the console end hangs free
        Assert.Equal(spot.Id, end.EntityId);
        Assert.Equal("DMX In", end.PortName);

        // Released anchors stay where they were: world position of the anchor point, in the cable's space.
        Assert.Equal(new Vector3(0, 4, 6) + new Vector3(1, 0, 0) - new Vector3(1, 0, 0), cable.Nodes[0].Position);
        Assert.Equal(new Vector3(10, 0, 1) - new Vector3(1, 0, 0), cable.Nodes[1].Position);

        // Layers the target does not have are left empty: Scene.Add puts the copies on the default layer.
        Assert.Equal(Guid.Empty, result.Roots[0].LayerId);
        foreach (var root in result.Roots) target.Add(root);
        Assert.All(target.All(), e => Assert.Equal(target.DefaultLayer.Id, e.LayerId));

        // The nested spot lands at its world position at the root.
        Assert.Equal(s.SpotA.WorldPosition, spot.WorldPosition);

        // What was removed is reported, with the copies that lost references.
        Assert.Equal(5, result.ClearedReferences.Count); // mount, carried truss, two anchors, console end
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EntityCloner.ReferencesRemovedCode, issue.Code);
        Assert.Contains(spot.Id, issue.Entities);
        Assert.Contains(hoist.Id, issue.Entities);
        Assert.Contains(cableEntity.Id, issue.Entities);
    }

    [Fact]
    public void Keep_if_resolvable_keeps_references_to_entities_the_target_has()
    {
        var s = new Stage();
        var clipboard = EntityCloner.Clone(new[] { s.SpotA, s.SpotB }, new EntityCloneOptions { RootsKeepWorldTransform = true });
        s.Scene.Remove(s.SpotB); // deleted after copying; the truss is still there

        var paste = EntityCloner.Clone(clipboard.Roots,
            new EntityCloneOptions { TargetScene = s.Scene, ExternalReferences = ExternalReferenceHandling.KeepIfResolvable });
        Assert.All(paste.Roots, r => Assert.Equal(s.Truss.Id, r.Get<FixtureComponent>()!.MountedOn));

        var other = new Scene();
        var elsewhere = EntityCloner.Clone(clipboard.Roots,
            new EntityCloneOptions { TargetScene = other, ExternalReferences = ExternalReferenceHandling.KeepIfResolvable });
        Assert.All(elsewhere.Roots, r => Assert.Null(r.Get<FixtureComponent>()!.MountedOn));
    }

    [Fact]
    public void Every_paste_of_a_clipboard_gets_fresh_ids()
    {
        var s = new Stage();
        var clipboard = EntityCloner.Clone(new[] { s.Truss }, new EntityCloneOptions { RootsKeepWorldTransform = true });
        var first = EntityCloner.Clone(clipboard.Roots, new EntityCloneOptions { TargetScene = s.Scene });
        foreach (var r in first.Roots) s.Scene.Add(r);
        var second = EntityCloner.Clone(clipboard.Roots, new EntityCloneOptions { TargetScene = s.Scene });
        foreach (var r in second.Roots) s.Scene.Add(r);

        var ids = first.IdMap.Values.Concat(second.IdMap.Values).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(second.Roots[0].Children, c => Assert.Equal(second.Roots[0].Id, c.Get<FixtureComponent>()!.MountedOn));
        Assert.Equal(s.Truss.WorldPosition, second.Roots[0].WorldPosition);
    }

    [Fact]
    public void Cable_node_ids_are_renewed_and_segments_and_endpoints_follow_them()
    {
        var s = new Stage();
        var result = EntityCloner.Clone(new[] { s.CableEntity, s.SpotA, s.Console, s.Truss, s.Wall }, new EntityCloneOptions { TargetScene = s.Scene });

        var cable = result.FindCopy(s.CableEntity.Id)!.Get<CableComponent>()!;
        var original = s.Cable;
        Assert.Equal(original.Nodes.Count, cable.Nodes.Count);
        Assert.Empty(cable.Nodes.Select(n => n.Id).Intersect(original.Nodes.Select(n => n.Id)));
        for (int i = 0; i < 2; i++)
        {
            int from = original.Nodes.FindIndex(n => n.Id == original.Segments[i].FromNode);
            int to = original.Nodes.FindIndex(n => n.Id == original.Segments[i].ToNode);
            Assert.Equal(cable.Nodes[from].Id, cable.Segments[i].FromNode);
            Assert.Equal(cable.Nodes[to].Id, cable.Segments[i].ToNode);
        }
        Assert.Equal(cable.Nodes[0].Id, cable.Endpoints[0].NodeId);
        Assert.Equal(cable.Nodes[2].Id, cable.Endpoints[1].NodeId);
        Assert.Equal(result.IdMap[s.SpotA.Id], cable.Endpoints[0].EntityId);
        Assert.Equal(result.IdMap[s.Console.Id], cable.Endpoints[1].EntityId);
        Assert.Equal(result.IdMap[s.Truss.Id], cable.Nodes[0].Anchor!.Value.EntityId);
        Assert.Equal(result.IdMap[s.Wall.Id], cable.Nodes[1].Anchor!.Value.EntityId);

        var kept = EntityCloner.Clone(s.CableEntity, new EntityCloneOptions { RegenerateLocalIds = false });
        Assert.Equal(original.Nodes.Select(n => n.Id), kept.Roots[0].Get<CableComponent>()!.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Merging_a_whole_scene_into_itself_translates_every_reference()
    {
        var s = new Stage();
        var before = s.Scene.Count;
        var result = EntityCloner.Clone(s.Scene.Roots, new EntityCloneOptions { TargetScene = s.Scene, ExternalReferences = ExternalReferenceHandling.Clear });
        Assert.Empty(result.ClearedReferences);
        Assert.Equal(before, result.Count);

        var group = new Entity("Imported");
        foreach (var root in result.Roots) group.AddChild(root);
        s.Scene.Add(group);
        Assert.Equal(2 * before + 1, s.Scene.Count);

        var copies = group.SelfAndDescendants().Skip(1).Select(e => e.Id).ToHashSet();
        foreach (var e in group.SelfAndDescendants())
        {
            if (e.Get<FixtureComponent>()?.MountedOn is { } mount) Assert.Contains(mount, copies);
            foreach (var carried in e.Get<RiggingPointComponent>()?.Carries ?? new List<Guid>()) Assert.Contains(carried, copies);
            if (e.Get<CableComponent>() is { } cable)
            {
                Assert.All(cable.Endpoints, ep => Assert.Contains(ep.EntityId, copies));
                Assert.All(cable.Nodes.Where(n => n.Anchor is not null), n => Assert.Contains(n.Anchor!.Value.EntityId, copies));
            }
        }
    }

    [Fact]
    public void Merge_options_map_layers_assets_and_let_the_caller_finish_each_component()
    {
        var source = new Scene("Source");
        var layer = new Layer { Name = "Imported" };
        source.Layers.Add(layer);
        var modelId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var model = source.Add(new Entity("Model") { LayerId = layer.Id });
        model.Add(new MeshComponent { ModelAssetId = modelId, MaterialOverrides = { [0] = $"asset:{modelId:D}/wood.png" } });
        var poster = source.Add(new Entity("Poster"));
        poster.Add(new ImageComponent { ImageAssetId = imageId });
        var truss = source.Add(new Entity("Truss"));
        truss.Add(new TrussComponent { ModelAssetId = modelId });

        var target = new Scene("Target");
        var targetLayer = new Layer { Name = "Imported" };
        target.Layers.Add(targetLayer);
        var newModel = Guid.NewGuid();
        var newImage = Guid.NewGuid();
        var calls = new List<(Component Original, Component Copy)>();
        var result = EntityCloner.Clone(source.Roots, new EntityCloneOptions
        {
            TargetScene = target,
            ExternalReferences = ExternalReferenceHandling.Clear,
            LayerMap = id => id == layer.Id ? targetLayer.Id : id,
            AssetIdMap = new Dictionary<Guid, Guid> { [modelId] = newModel, [imageId] = newImage },
            ComponentCopied = (original, copy) =>
            {
                calls.Add((original, copy));
                if (copy is MeshComponent mesh)
                {
                    foreach (var slot in mesh.MaterialOverrides.Keys.ToList())
                        mesh.MaterialOverrides[slot] = mesh.MaterialOverrides[slot].Replace(modelId.ToString("D"), newModel.ToString("D"));
                }
            },
        });

        Assert.Equal(targetLayer.Id, result.Roots[0].LayerId);
        var mesh = result.Roots[0].Get<MeshComponent>()!;
        Assert.Equal(newModel, mesh.ModelAssetId);
        Assert.Equal($"asset:{newModel:D}/wood.png", mesh.MaterialOverrides[0]);
        Assert.Equal(newImage, result.Roots[1].Get<ImageComponent>()!.ImageAssetId);
        Assert.Equal(newModel, result.Roots[2].Get<TrussComponent>()!.ModelAssetId);
        Assert.Equal(3, result.AssetReferencesRemapped);
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c =>
        {
            Assert.NotSame(c.Original, c.Copy);
            Assert.Equal(c.Original.GetType(), c.Copy.GetType());
            Assert.NotNull(c.Copy.Owner);
            Assert.Null(c.Copy.Owner!.Scene); // still detached while the caller finishes it
            Assert.Same(source, c.Original.Owner!.Scene);
        });
        Assert.Equal(modelId, model.Get<MeshComponent>()!.ModelAssetId); // the source is untouched
    }

    [Fact]
    public void A_failing_component_hook_leaves_only_that_component_out()
    {
        var s = new Stage();
        var result = EntityCloner.Clone(s.Truss, new EntityCloneOptions
        {
            ComponentCopied = (_, copy) =>
            {
                if (copy is FixtureComponent { GdtfSpec: "b.gdtf" }) throw new InvalidDataException("no such file");
            },
        });

        var truss = result.Roots[0];
        Assert.NotNull(truss.Get<TrussComponent>());
        Assert.NotNull(truss.Children[0].Get<FixtureComponent>());
        Assert.Null(truss.Children[1].Get<FixtureComponent>());
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EntityCloner.ComponentNotCopiedCode, issue.Code);
        Assert.Contains("no such file", issue.Message);
        Assert.Equal(new[] { truss.Children[1].Id }, issue.Entities);
    }

    [Fact]
    public void Ids_never_collide_with_the_target_and_the_id_factory_is_honoured_when_free()
    {
        var s = new Stage();
        var free = Guid.NewGuid();
        var result = EntityCloner.Clone(new[] { s.SpotA, s.SpotB }, new EntityCloneOptions
        {
            TargetScene = s.Scene,
            IdFactory = e => e == s.SpotA ? free : e.Id, // SpotB's own id is taken in the target
        });
        Assert.Equal(free, result.Roots[0].Id);
        Assert.NotEqual(s.SpotB.Id, result.Roots[1].Id);
        Assert.Null(s.Scene.Find(result.Roots[1].Id));

        var same = EntityCloner.Clone(new[] { s.SpotA, s.SpotB }, new EntityCloneOptions { IdFactory = _ => free });
        Assert.Equal(2, same.IdMap.Values.Distinct().Count());
    }

    [Fact]
    public void Ancestors_and_descendants_listed_together_are_copied_once_and_nulls_are_ignored()
    {
        var s = new Stage();
        var result = EntityCloner.Clone(new[] { s.SpotA, null!, s.Truss, s.SpotA });
        var root = Assert.Single(result.Roots);
        Assert.Equal("Truss", root.Name);
        Assert.Equal(3, result.Count);
        Assert.Empty(EntityCloner.Clone(Array.Empty<Entity>()).Roots);
    }

    [Fact]
    public void Exact_copies_keep_nulls_and_unknown_properties()
    {
        var s = new Stage();
        var json = JsonSerializer.Deserialize<JsonElement>("""{ "futureFlag": true, "futureList": [1, 2] }""");
        s.Console.UnknownProperties = new Dictionary<string, JsonElement> { ["entityFuture"] = json };
        s.Console.Get<ConsoleComponent>()!.UnknownProperties = new Dictionary<string, JsonElement> { ["componentFuture"] = json };

        var copy = EntityCloner.Clone(s.Console).Roots[0];
        Assert.Null(copy.Get<ConsoleComponent>()!.ProtocolId); // not turned back into "artnet"
        Assert.True(copy.UnknownProperties!["entityFuture"].GetProperty("futureFlag").GetBoolean());
        Assert.Equal(2, copy.Get<ConsoleComponent>()!.UnknownProperties!["componentFuture"].GetProperty("futureList").GetArrayLength());
        Assert.NotSame(s.Console.UnknownProperties, copy.UnknownProperties);
    }

    [Fact]
    public void Detached_subtrees_can_be_built_and_edited_before_joining_a_scene_in_one_step()
    {
        var group = new Entity("Group");
        var a = group.AddChild(new Entity("A"));
        var b = group.AddChild(new Entity("B"), 0);
        var c = a.AddChild(new Entity("C"));
        Assert.Equal(new[] { b, a }, group.Children);
        Assert.Equal(-1, group.RemoveChild(c));
        Assert.Equal(0, a.RemoveChild(c));
        Assert.Null(c.Parent);
        Assert.Throws<InvalidOperationException>(() => c.AddChild(group.Children[0]));
        a.AddChild(c);

        var scene = new Scene();
        var changes = new List<SceneChange>();
        scene.Changed += changes.Add;
        scene.Add(group);
        var added = Assert.Single(changes);
        Assert.Equal(SceneChangeKind.Added, added.Kind);
        Assert.Same(group, added.Entity);
        Assert.Equal(4, scene.Count);

        changes.Clear();
        Assert.Equal(1, group.RemoveChild(a)); // in a scene: a scene removal
        Assert.Equal(SceneChangeKind.Removed, Assert.Single(changes).Kind);
        Assert.Null(c.Scene);
        Assert.Equal(2, scene.Count);
    }

    [Fact]
    public void Duplicate_command_and_add_entities_command_use_the_cloner_with_undo()
    {
        var s = new Stage();
        var undo = new UndoStack();
        var count = s.Scene.Count;
        var duplicate = new DuplicateEntitiesCommand(s.Scene, new[] { s.Hoist, s.Truss });
        undo.Execute(duplicate);
        Assert.Equal(count + 4, s.Scene.Count);
        var hoistCopy = duplicate.Copies[0];
        var trussCopy = duplicate.Copies[1];
        Assert.Equal(new[] { trussCopy.Id }, hoistCopy.Get<RiggingPointComponent>()!.Carries);
        undo.Undo();
        Assert.Equal(count, s.Scene.Count);
        undo.Redo();
        Assert.Equal(count + 4, s.Scene.Count);

        var paste = EntityCloner.Clone(new[] { s.SpotA }, new EntityCloneOptions { TargetScene = s.Scene, RootsKeepWorldTransform = true });
        var add = new AddEntitiesCommand(s.Scene, paste.Roots, description: "Paste");
        undo.Execute(add);
        Assert.Equal("Paste", undo.UndoDescription);
        Assert.Equal(s.SpotA.WorldPosition, paste.Roots[0].WorldPosition);
        undo.Undo();
        Assert.Null(paste.Roots[0].Scene);
    }

    [Fact]
    public void Copying_thousands_of_entities_is_linear()
    {
        var scene = new Scene();
        var trusses = new List<Entity>();
        for (int t = 0; t < 200; t++)
        {
            var truss = scene.Add(new Entity($"Truss {t}"));
            truss.Add(new TrussComponent());
            trusses.Add(truss);
            for (int f = 0; f < 50; f++)
            {
                var spot = scene.Add(new Entity($"Spot {t}.{f}"), truss);
                spot.Add(new FixtureComponent { MountedOn = truss.Id, Addresses = { new DmxAddress(t + 1, f * 10 + 1) } });
            }
        }

        var watch = Stopwatch.StartNew();
        var result = EntityCloner.Clone(trusses, new EntityCloneOptions { TargetScene = scene });
        watch.Stop();
        Assert.Equal(200 * 51, result.Count);
        Assert.Equal(200 * 50, result.ReferencesRemapped);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Copying {result.Count} entities took {watch.Elapsed}.");
    }
}
