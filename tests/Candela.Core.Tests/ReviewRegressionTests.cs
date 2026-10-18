using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;
using Candela.Core.Settings;

namespace Candela.Core.Tests;

/// <summary>Regression tests for defects found by the review of the Core change requests.</summary>
public class ReviewRegressionTests
{
    private static List<SceneChange> Record(Scene scene)
    {
        var changes = new List<SceneChange>();
        scene.Changed += changes.Add;
        return changes;
    }

    [Fact]
    public void Building_a_scene_from_the_record_of_a_live_scene_copies_it_and_leaves_the_original_intact()
    {
        // Before: FromFile(ToFile(scene)) moved the layers and settings of the original into the copy (their changes were
        // raised on the copy, the original went silent) and dropped every component as "already attached".
        var scene = new Scene("Live");
        var rig = new Layer { Name = "Rig" };
        scene.Layers.Add(rig);
        scene.Settings.GridSpacing = 0.5f;
        var wall = scene.Add(new Entity("Wall") { LayerId = rig.Id });
        var wallComponent = wall.Add(new WallComponent { Width = 7 });
        var spot = scene.Add(new Entity("Spot"), wall);
        spot.Add(new FixtureComponent { MountedOn = wall.Id, GdtfSpec = "x.gdtf" });

        var issues = new List<Issue>();
        var copy = SceneSerializer.FromFile(SceneSerializer.ToFile(scene), issues);

        Assert.Empty(issues);
        Assert.Equal(scene.Count, copy.Count);
        var wallCopy = copy.Find(wall.Id)!;
        Assert.NotSame(wall, wallCopy);
        Assert.Equal(7, Assert.IsType<WallComponent>(Assert.Single(wallCopy.Components)).Width);
        Assert.NotSame(wallComponent, wallCopy.Components[0]);
        Assert.Same(wall, wallComponent.Owner);
        Assert.Equal(wall.Id, copy.Find(spot.Id)!.Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(rig.Id, copy.Layers[1].Id);
        Assert.NotSame(rig, copy.Layers[1]);
        Assert.NotSame(scene.Settings, copy.Settings);
        Assert.Equal(0.5f, copy.Settings.GridSpacing);

        var original = Record(scene);
        var copied = Record(copy);
        rig.Visible = false;
        scene.Settings.GridSpacing = 2;
        Assert.Equal(new[] { SceneChangeKind.LayersChanged, SceneChangeKind.SettingsChanged }, original.Select(c => c.Kind));
        Assert.Empty(copied);
        Assert.True(copy.Layers[1].Visible);
        Assert.Equal(0.5f, copy.Settings.GridSpacing);

        copy.Layers[1].Locked = true;
        copy.Settings.HazeAnisotropy = 0.1f;
        Assert.Equal(2, copied.Count);
        Assert.Equal(2, original.Count);
    }

    [Fact]
    public void A_layer_belongs_to_one_scene_at_a_time()
    {
        // Before: adding a layer of scene A to scene B silently moved its notifications to B, and removing it from B
        // left A with a layer whose changes were never raised.
        var a = new Scene("A");
        var b = new Scene("B");
        var shared = new Layer { Name = "Shared" };
        a.Layers.Add(shared);
        long version = b.Version;

        Assert.Throws<InvalidOperationException>(() => b.Layers.Add(shared));
        Assert.Throws<InvalidOperationException>(() => b.Layers.Insert(0, shared));
        Assert.Throws<InvalidOperationException>(() => b.Layers[0] = shared);
        var fresh = new Layer { Name = "Fresh" };
        Assert.Throws<InvalidOperationException>(() => b.Layers.AddRange(new[] { fresh, shared }));
        Assert.Throws<ArgumentNullException>(() => b.Layers.AddRange(new[] { fresh, null! }));
        Assert.Single(b.Layers); // nothing was added by the refused calls
        Assert.Null(b.FindLayer(fresh.Id));
        Assert.Equal(version, b.Version);

        var changes = Record(a);
        shared.Visible = false;
        Assert.Equal(SceneChangeKind.LayersChanged, Assert.Single(changes).Kind);

        // Once removed from A, the layer may join B; its changes then go to B.
        a.Layers.Remove(shared);
        b.Layers.Add(shared);
        changes.Clear();
        var bChanges = Record(b);
        shared.Locked = true;
        Assert.Empty(changes);
        Assert.Single(bChanges);

        // Adding the same layer object twice to one scene stays possible (malformed files may repeat a layer).
        b.Layers.Add(shared);
        Assert.Equal(3, b.Layers.Count);
    }

    [Fact]
    public void Scene_settings_belong_to_one_scene_at_a_time()
    {
        var a = new Scene("A");
        var b = new Scene("B");
        Assert.Throws<InvalidOperationException>(() => b.Settings = a.Settings);

        var changes = Record(a);
        a.Settings.GridSpacing = 3;
        Assert.Single(changes);

        // Settings that were replaced in A are free again.
        var former = a.Settings;
        a.Settings = new SceneSettings();
        b.Settings = former;
        changes.Clear();
        var bChanges = Record(b);
        former.AmbientLuminance = 1;
        Assert.Empty(changes);
        Assert.Single(bChanges);

        // A copy can always be assigned.
        a.Settings = CandelaJson.DeepClone(b.Settings);
        Assert.Equal(1, a.Settings.AmbientLuminance);
    }

    [Fact]
    public void Adding_the_current_primary_again_is_not_a_selection_change()
    {
        var selection = new Selection();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        selection.Add(a);
        selection.Add(b);
        int changes = 0;
        selection.Changed += () => changes++;

        selection.Add(b);
        Assert.Equal(0, changes);
        selection.Add(a); // not the primary: moves to the end
        Assert.Equal(1, changes);
        Assert.Equal(new[] { b, a }, selection.Ids);
    }

    [Fact]
    public void Key_chords_compare_key_names_ignoring_case()
    {
        // Before: "ctrl+s" in a hand-edited preset parsed to key "s" and never matched the S key.
        Assert.Equal(new KeyChord("S", KeyModifiers.Ctrl), KeyChord.Parse("ctrl+s"));
        Assert.Equal(new KeyChord("PageDown").GetHashCode(), KeyChord.Parse("pagedown").GetHashCode());
        Assert.NotEqual(new KeyChord("S", KeyModifiers.Ctrl), new KeyChord("S", KeyModifiers.Ctrl | KeyModifiers.Shift));
        Assert.NotEqual(new KeyChord("S"), new KeyChord("D"));
        Assert.Equal(default, new KeyChord(null!, KeyModifiers.None));

        var set = KeyBindingSet.CreateDefault();
        Assert.Contains(EditorActions.FileSave, set.ActionsFor(KeyChord.Parse("CTRL+s")));
        Assert.Contains(EditorActions.ViewTop, set.ActionsFor(new KeyChord("numpad7")));
        Assert.Equal("Ctrl+s", KeyChord.Parse("ctrl+s").ToString()); // the key is still kept as written
        var bySet = new HashSet<KeyChord> { new("f12"), new("F12") };
        Assert.Single(bySet);
    }

    [Fact]
    public void Every_chord_of_the_default_preset_round_trips_through_its_text()
    {
        foreach (var (action, texts) in KeyBindingSet.CreateDefault().Bindings)
        {
            foreach (var text in texts)
            {
                var chord = KeyChord.Parse(text);
                Assert.Equal(text, chord.ToString());
                Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
                Assert.Contains(action, KeyBindingSet.CreateDefault().ActionsFor(chord));
            }
        }
    }

    [Fact]
    public void A_failed_multi_add_leaves_the_scene_as_it_was_and_records_nothing()
    {
        // Before: when the second root could not be added, the first stayed in the scene and the command was not on
        // the undo stack, so the half-applied paste could not be undone.
        var scene = new Scene();
        var existing = scene.Add(new Entity("Existing"));
        var first = new Entity("First");
        var clash = new Entity("Clash", existing.Id);
        var undo = new UndoStack();
        long version = scene.Version;
        var changes = Record(scene);

        Assert.Throws<InvalidOperationException>(() => undo.Execute(new AddEntitiesCommand(scene, new[] { first, clash })));

        Assert.Equal(1, scene.Count);
        Assert.Null(first.Scene);
        Assert.Null(clash.Scene);
        Assert.Same(existing, scene.Find(existing.Id));
        Assert.False(undo.CanUndo);
        Assert.Equal(new[] { SceneChangeKind.Added, SceneChangeKind.Removed }, changes.Select(c => c.Kind));
        Assert.True(scene.Version > version);

        // An entity that is already in the scene is not removed by the rollback.
        var second = new Entity("Second");
        Assert.Throws<InvalidOperationException>(() => new AddEntitiesCommand(scene, new[] { second, existing }).Do());
        Assert.Same(existing, scene.Find(existing.Id));
        Assert.Null(second.Scene);
    }

    [Fact]
    public void A_change_handler_that_throws_during_a_multi_add_rolls_the_whole_add_back()
    {
        var scene = new Scene();
        var a = new Entity("A");
        var b = new Entity("B");
        void Throw(SceneChange c)
        {
            if (c.Kind == SceneChangeKind.Added && c.Entity == b) throw new InvalidOperationException("view failed");
        }

        scene.Changed += Throw;
        Assert.Throws<InvalidOperationException>(() => new AddEntitiesCommand(scene, new[] { a, b }).Do());
        scene.Changed -= Throw;
        Assert.Equal(0, scene.Count);
        Assert.Null(a.Scene);
        Assert.Null(b.Scene);
    }

    [Fact]
    public void Reparent_raises_one_change_and_a_move_to_the_same_place_raises_none()
    {
        // Before: keeping the world placement raised TransformChanged and then Reparented for one move, and undo raised
        // Reparented and then TransformChanged.
        var scene = new Scene();
        var truss = scene.Add(new Entity("Truss") { Local = new Transform(new Vector3(0, 0, 6)) });
        var spot = scene.Add(new Entity("Spot") { Local = new Transform(new Vector3(1, 2, 3)) });
        var other = scene.Add(new Entity("Other"));
        var changes = Record(scene);

        scene.Reparent(spot, truss);
        var change = Assert.Single(changes);
        Assert.Equal(SceneChangeKind.Reparented, change.Kind);
        Assert.Null(change.FormerParent);
        Assert.Equal(new Vector3(1, 2, -3), spot.Local.Position);
        Assert.Equal(new Vector3(1, 2, 3), spot.WorldPosition);

        changes.Clear();
        scene.Reparent(spot, truss); // already the last child of the truss
        scene.Reparent(other, null); // already the last root
        Assert.Empty(changes);

        scene.Reparent(truss, null, index: 0); // already the first root
        Assert.Empty(changes);
        scene.Reparent(other, null, index: 0); // a real move among the roots
        Assert.Equal(SceneChangeKind.Reparented, Assert.Single(changes).Kind);
        Assert.Same(other, scene.Roots[0]);

        // Through the command: one change for do, one for undo, one for redo.
        changes.Clear();
        var undo = new UndoStack();
        undo.Execute(new ReparentCommand(scene, spot, other));
        undo.Undo();
        Assert.Same(truss, spot.Parent);
        Assert.Equal(new Vector3(1, 2, -3), spot.Local.Position);
        undo.Redo();
        Assert.Same(other, spot.Parent);
        Assert.Equal(new[] { SceneChangeKind.Reparented, SceneChangeKind.Reparented, SceneChangeKind.Reparented }, changes.Select(c => c.Kind));
        Assert.True(Vector3.Distance(new Vector3(1, 2, 3), spot.WorldPosition) < 1e-5f);
    }

    [Fact]
    public void Building_a_deep_detached_chain_is_linear()
    {
        // Before: every AddChild on a detached entity walked all its ancestors to rule out a cycle (quadratic).
        const int depth = 200_000;
        var top = new Entity("top");
        var leaf = top;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < depth; i++) leaf = leaf.AddChild(new Entity("n"));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{depth} appends took {sw.Elapsed}.");

        // Cycles are still refused, from either end.
        Assert.Throws<InvalidOperationException>(() => leaf.AddChild(top));
        var middle = top.Children[0].Children[0];
        var detached = middle.Parent!;
        detached.RemoveChild(middle);
        Assert.Throws<InvalidOperationException>(() => leaf.AddChild(middle));
        Assert.Throws<InvalidOperationException>(() => middle.AddChild(middle));
        top.Children[0].AddChild(middle);
        Assert.Same(top.Children[0], middle.Parent);
    }

    [Fact]
    public void Copies_share_no_mutable_state_with_their_originals()
    {
        var scene = new Scene();
        var e = scene.Add(new Entity("Cable") { UnknownProperties = new() { ["future"] = JsonDocument.Parse("{\"a\":1}").RootElement.Clone() } });
        var cable = e.Add(new CableComponent
        {
            Nodes = { new CableNode { Position = new Vector3(1, 2, 3) }, new CableNode() },
            UnknownProperties = new() { ["later"] = JsonDocument.Parse("[1,2]").RootElement.Clone() },
        });
        cable.Segments.Add(new CableSegment(cable.Nodes[0].Id, cable.Nodes[1].Id));
        var fixture = e.Add(new FixtureComponent { ManualAttributes = { ["Pan"] = 10 }, Addresses = { new DmxAddress(1, 1) } });

        var copy = EntityCloner.Clone(e).Roots[0];
        var copiedCable = copy.Get<CableComponent>()!;
        var copiedFixture = copy.Get<FixtureComponent>()!;
        copiedCable.Nodes[0].Position = Vector3.Zero;
        copiedCable.Nodes.Add(new CableNode());
        copiedCable.UnknownProperties!.Clear();
        copiedFixture.ManualAttributes["Pan"] = 99;
        copiedFixture.Addresses.Clear();
        copy.UnknownProperties!.Clear();

        Assert.Equal(new Vector3(1, 2, 3), cable.Nodes[0].Position);
        Assert.Equal(2, cable.Nodes.Count);
        Assert.Single(cable.UnknownProperties!);
        Assert.Equal(10, fixture.ManualAttributes["Pan"]);
        Assert.Single(fixture.Addresses);
        Assert.Single(e.UnknownProperties!);
    }

    [Fact]
    public void Duplicating_after_undo_and_redo_keeps_parents_and_references()
    {
        var scene = new Scene();
        var truss = scene.Add(new Entity("Truss"));
        var spot = scene.Add(new Entity("Spot"), truss);
        spot.Add(new FixtureComponent { MountedOn = truss.Id });
        var undo = new UndoStack();
        var duplicate = new DuplicateEntitiesCommand(scene, new[] { spot });
        undo.Execute(duplicate);
        undo.Undo();
        Assert.Null(duplicate.Copies[0].Scene);
        undo.Redo();
        Assert.Same(truss, duplicate.Copies[0].Parent);
        Assert.Equal(truss.Id, duplicate.Copies[0].Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(3, scene.Count);
    }
}
