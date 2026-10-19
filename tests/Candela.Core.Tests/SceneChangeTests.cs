using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Core.Tests;

/// <summary>Every mutable piece of scene state raises a change and bumps the version, also through undo/redo.</summary>
public class SceneChangeTests
{
    private sealed class Recorder
    {
        public Recorder(Scene scene) => scene.Changed += Changes.Add;

        public List<SceneChange> Changes { get; } = new();

        public SceneChange Single(SceneChangeKind kind)
        {
            var c = Assert.Single(Changes);
            Assert.Equal(kind, c.Kind);
            Changes.Clear();
            return c;
        }
    }

    [Fact]
    public void A_new_scene_starts_at_version_zero_with_a_default_layer()
    {
        var scene = new Scene();
        Assert.Equal(0, scene.Version);
        Assert.Equal("Default", Assert.Single(scene.Layers).Name);
        Assert.Same(scene.Layers[0], scene.DefaultLayer);
    }

    [Theory]
    [InlineData(SceneChangeKind.VisibilityChanged)]
    [InlineData(SceneChangeKind.LockedChanged)]
    [InlineData(SceneChangeKind.LayerChanged)]
    public void Entity_flags_and_layer_raise_their_change_once_and_bump_the_version(SceneChangeKind kind)
    {
        var scene = new Scene();
        var other = new Layer { Name = "Rig" };
        scene.Layers.Add(other);
        var e = scene.Add(new Entity("Spot"));
        var originalLayer = e.LayerId;
        var rec = new Recorder(scene);
        long v = scene.Version;

        Set(e, kind, on: true, other.Id);
        var change = rec.Single(kind);
        Assert.Same(e, change.Entity);
        Assert.Null(change.Component);
        Assert.Equal(v + 1, scene.Version);
        if (kind == SceneChangeKind.LayerChanged) Assert.Equal(originalLayer, change.FormerLayerId);

        // Setting the same value again is not a change.
        Set(e, kind, on: true, other.Id);
        Assert.Empty(rec.Changes);
        Assert.Equal(v + 1, scene.Version);

        Set(e, kind, on: false, originalLayer);
        rec.Single(kind);
        Assert.Equal(v + 2, scene.Version);
    }

    [Fact]
    public void Detached_entities_change_silently()
    {
        var scene = new Scene();
        var rec = new Recorder(scene);
        var e = new Entity("Loose") { Visible = false, Locked = true, LayerId = Guid.NewGuid() };
        e.Name = "Renamed";
        Assert.Empty(rec.Changes);
        Assert.Equal(0, scene.Version);

        // Once removed from the scene, the entity is detached again.
        scene.Add(e);
        scene.Remove(e);
        rec.Changes.Clear();
        e.Visible = true;
        e.Locked = false;
        Assert.Empty(rec.Changes);
    }

    [Theory]
    [InlineData(SceneChangeKind.VisibilityChanged)]
    [InlineData(SceneChangeKind.LockedChanged)]
    [InlineData(SceneChangeKind.LayerChanged)]
    public void Undo_and_redo_of_flag_edits_raise_changes(SceneChangeKind kind)
    {
        var scene = new Scene();
        var rig = new Layer { Name = "Rig" };
        scene.Layers.Add(rig);
        var e = scene.Add(new Entity("Spot"));
        var undo = new UndoStack();
        var rec = new Recorder(scene);
        var command = kind switch
        {
            SceneChangeKind.VisibilityChanged => (IUndoableCommand)new SetValueCommand<bool>("Hide", () => e.Visible, x => e.Visible = x, false),
            SceneChangeKind.LockedChanged => new SetValueCommand<bool>("Lock", () => e.Locked, x => e.Locked = x, true),
            _ => new SetValueCommand<Guid>("Move to layer", () => e.LayerId, x => e.LayerId = x, rig.Id),
        };

        long v0 = scene.Version;
        undo.Execute(command);
        rec.Single(kind);
        undo.Undo();
        rec.Single(kind);
        undo.Redo();
        rec.Single(kind);
        Assert.Equal(v0 + 3, scene.Version);
    }

    [Fact]
    public void Layer_edits_raise_layers_changed_with_the_layer()
    {
        var scene = new Scene();
        var rec = new Recorder(scene);
        var layer = new Layer { Name = "Set" };

        scene.Layers.Add(layer);
        Assert.Same(layer, rec.Single(SceneChangeKind.LayersChanged).Layer);

        layer.Name = "Set pieces";
        Assert.Same(layer, rec.Single(SceneChangeKind.LayersChanged).Layer);
        layer.Visible = false;
        rec.Single(SceneChangeKind.LayersChanged);
        layer.Locked = true;
        rec.Single(SceneChangeKind.LayersChanged);
        layer.Locked = true; // unchanged
        Assert.Empty(rec.Changes);

        // Undo of a layer edit through a plain setter is seen too.
        var undo = new UndoStack();
        undo.Execute(new SetValueCommand<bool>("Show layer", () => layer.Visible, x => layer.Visible = x, true));
        undo.Undo();
        undo.Redo();
        Assert.Equal(3, rec.Changes.Count);
        Assert.All(rec.Changes, c => Assert.Equal(SceneChangeKind.LayersChanged, c.Kind));
        rec.Changes.Clear();

        Assert.True(scene.Layers.Remove(layer));
        Assert.Same(layer, rec.Single(SceneChangeKind.LayersChanged).Layer);
        layer.Name = "Gone";
        Assert.Empty(rec.Changes); // no longer part of the scene
    }

    [Fact]
    public void Layer_list_operations_keep_the_index_in_sync()
    {
        var scene = new Scene();
        var rec = new Recorder(scene);
        var a = new Layer { Name = "A" };
        var b = new Layer { Name = "B" };
        var c = new Layer { Name = "C" };

        scene.Layers.AddRange(new[] { a, b });
        Assert.Null(rec.Single(SceneChangeKind.LayersChanged).Layer); // bulk
        scene.Layers.Insert(1, c);
        rec.Single(SceneChangeKind.LayersChanged);
        Assert.Equal(new[] { "Default", "C", "A", "B" }, scene.Layers.Select(l => l.Name));
        Assert.Same(c, scene.FindLayer(c.Id));

        scene.Layers.Move(3, 0);
        rec.Single(SceneChangeKind.LayersChanged);
        Assert.Same(b, scene.DefaultLayer);

        var replacement = new Layer { Name = "C2" };
        scene.Layers[scene.Layers.IndexOf(c)] = replacement;
        rec.Single(SceneChangeKind.LayersChanged);
        Assert.Null(scene.FindLayer(c.Id));
        Assert.Same(replacement, scene.FindLayer(replacement.Id));
        c.Name = "detached";
        Assert.Empty(rec.Changes);

        scene.Layers.RemoveAt(0);
        rec.Single(SceneChangeKind.LayersChanged);
        Assert.Null(scene.FindLayer(b.Id));
        Assert.True(scene.Layers.Contains(a.Id));

        scene.Layers.Clear();
        rec.Single(SceneChangeKind.LayersChanged);
        Assert.Null(scene.FindLayer(a.Id));
        Assert.Empty(scene.Layers);

        // A scene always has a default layer: it is recreated on demand.
        var added = scene.Add(new Entity("E"));
        Assert.Single(scene.Layers);
        Assert.Equal(scene.Layers[0].Id, added.LayerId);
    }

    [Fact]
    public void Repeated_layer_ids_resolve_to_the_first_layer_in_list_order()
    {
        var scene = new Scene();
        var id = Guid.NewGuid();
        var first = new Layer { Id = id, Name = "first" };
        var second = new Layer { Id = id, Name = "second" };
        scene.Layers.Add(first);
        scene.Layers.Add(second);
        Assert.Same(first, scene.FindLayer(id));

        scene.Layers.Move(2, 0); // "second" is now first in list order
        Assert.Same(second, scene.FindLayer(id));
        scene.Layers.Remove(second);
        Assert.Same(first, scene.FindLayer(id));
        scene.Layers.Remove(first);
        Assert.Null(scene.FindLayer(id));
    }

    [Fact]
    public void Scene_name_and_settings_raise_changes()
    {
        var scene = new Scene("Stage");
        var rec = new Recorder(scene);

        scene.Name = "Main stage";
        var renamed = rec.Single(SceneChangeKind.SceneRenamed);
        Assert.Null(renamed.Entity);
        scene.Name = "Main stage";
        Assert.Empty(rec.Changes);

        scene.Settings.BaseHazeScattering = 0.1f;
        rec.Single(SceneChangeKind.SettingsChanged);
        scene.Settings.BaseHazeScattering = 0.1f;
        Assert.Empty(rec.Changes);
        scene.Settings.DisplayUnits = UnitSystem.Imperial;
        rec.Single(SceneChangeKind.SettingsChanged);
        scene.Settings.HazeAnisotropy = float.NaN;
        rec.Single(SceneChangeKind.SettingsChanged);
        scene.Settings.HazeAnisotropy = float.NaN; // NaN equals NaN here: no change
        Assert.Empty(rec.Changes);

        var old = scene.Settings;
        var replacement = new SceneSettings { GridSpacing = 0.5f };
        scene.Settings = replacement;
        rec.Single(SceneChangeKind.SettingsChanged);
        old.GridSpacing = 2f;
        Assert.Empty(rec.Changes); // the replaced object no longer belongs to the scene
        replacement.GridSpacing = 2f;
        rec.Single(SceneChangeKind.SettingsChanged);

        scene.Settings = null!;
        rec.Single(SceneChangeKind.SettingsChanged);
        Assert.NotNull(scene.Settings);

        var undo = new UndoStack();
        undo.Execute(new SetValueCommand<float>("Haze", () => scene.Settings.BaseHazeScattering, x => scene.Settings.BaseHazeScattering = x, 0.3f));
        undo.Undo();
        undo.Redo();
        Assert.Equal(3, rec.Changes.Count(c => c.Kind == SceneChangeKind.SettingsChanged));
    }

    [Fact]
    public void Loading_a_scene_file_keeps_layers_settings_and_notifications_working()
    {
        var scene = new Scene("Saved");
        var rig = new Layer { Name = "Rig", Locked = true };
        scene.Layers.Add(rig);
        scene.Settings.GridSpacing = 0.25f;
        var e = scene.Add(new Entity("Truss") { LayerId = rig.Id, Visible = false });

        var back = SceneSerializer.FromJson(SceneSerializer.ToJson(scene));
        Assert.Equal(new[] { "Default", "Rig" }, back.Layers.Select(l => l.Name));
        Assert.True(back.FindLayer(rig.Id)!.Locked);
        Assert.Equal(0.25f, back.Settings.GridSpacing);
        Assert.False(back.Find(e.Id)!.Visible);

        var rec = new Recorder(back);
        back.FindLayer(rig.Id)!.Visible = false;
        rec.Single(SceneChangeKind.LayersChanged);
        back.Settings.GridSpacing = 1f;
        rec.Single(SceneChangeKind.SettingsChanged);
    }

    [Fact]
    public void Adding_a_subtree_assigns_layers_to_every_entity_without_one()
    {
        var scene = new Scene();
        var rig = new Layer { Name = "Rig" };
        scene.Layers.Add(rig);
        var truss = new Entity("Truss") { LayerId = rig.Id };
        var spot = truss.AddChild(new Entity("Spot"));
        var lens = spot.AddChild(new Entity("Lens"));
        var root = scene.Add(new Entity("Group"));

        scene.Add(truss, root);
        Assert.Equal(rig.Id, spot.LayerId);
        Assert.Equal(rig.Id, lens.LayerId);
        Assert.Equal(scene.DefaultLayer.Id, root.LayerId);
        Assert.Same(scene, lens.Scene);
        Assert.Equal(4, scene.Count);
    }

    [Fact]
    public void A_failed_add_leaves_the_scene_untouched()
    {
        var scene = new Scene();
        var existing = scene.Add(new Entity("Existing"));
        var newRoot = new Entity("New");
        newRoot.AddChild(new Entity("Clash", existing.Id));
        var rec = new Recorder(scene);

        Assert.Throws<InvalidOperationException>(() => scene.Add(newRoot));
        Assert.Single(scene.Roots);
        Assert.Equal(1, scene.Count);
        Assert.Null(newRoot.Scene);
        Assert.Empty(rec.Changes);

        var twice = new Entity("Twice");
        var id = Guid.NewGuid();
        twice.AddChild(new Entity("A", id));
        twice.AddChild(new Entity("B", id));
        Assert.Throws<InvalidOperationException>(() => scene.Add(twice));
        Assert.Equal(1, scene.Count);

        // Only a subtree root can be added; a child keeps belonging to its parent.
        var parent = new Entity("P");
        var child = parent.AddChild(new Entity("C"));
        Assert.Throws<InvalidOperationException>(() => scene.Add(child));
        Assert.Throws<InvalidOperationException>(() => child.AddChild(parent));
    }

    [Fact]
    public void Add_child_in_a_scene_is_a_scene_add()
    {
        var scene = new Scene();
        var truss = scene.Add(new Entity("Truss"));
        var rec = new Recorder(scene);
        var spot = truss.AddChild(new Entity("Spot"));
        Assert.Same(truss, spot.Parent);
        Assert.Same(scene, spot.Scene);
        Assert.Same(spot, rec.Single(SceneChangeKind.Added).Entity);
    }

    [Fact]
    public void Reparent_under_an_entity_of_another_scene_is_refused()
    {
        var a = new Scene();
        var b = new Scene();
        var e = a.Add(new Entity("E"));
        var foreign = b.Add(new Entity("F"));
        Assert.Throws<InvalidOperationException>(() => a.Reparent(e, foreign));
        Assert.Null(e.Parent);
        Assert.Contains(e, a.Roots);
    }

    [Fact]
    public void Traversal_is_in_tree_order_and_survives_very_deep_trees()
    {
        var scene = new Scene();
        var a = scene.Add(new Entity("a"));
        var a1 = scene.Add(new Entity("a1"), a);
        scene.Add(new Entity("a1x"), a1);
        scene.Add(new Entity("a2"), a);
        scene.Add(new Entity("b"));
        Assert.Equal(new[] { "a", "a1", "a1x", "a2", "b" }, scene.All().Select(e => e.Name));
        Assert.Equal(new[] { "a1", "a1x" }, a1.SelfAndDescendants().Select(e => e.Name));

        const int depth = 20_000;
        var top = new Entity("top");
        var leaf = top;
        for (int i = 0; i < depth; i++) leaf = leaf.AddChild(new Entity("n"));
        leaf.Visible = true;
        scene.Add(top);
        Assert.Equal(depth + 1, top.SelfAndDescendants().Count());
        Assert.Equal(5 + depth + 1, scene.All().Count());
        Assert.True(leaf.IsVisibleInHierarchy);
        top.Visible = false;
        Assert.False(leaf.IsVisibleInHierarchy);
    }

    [Fact]
    public void Visibility_and_lock_queries_combine_hierarchy_and_layer()
    {
        var scene = new Scene();
        var hidden = new Layer { Name = "Hidden", Visible = false };
        var locked = new Layer { Name = "Locked", Locked = true };
        scene.Layers.Add(hidden);
        scene.Layers.Add(locked);
        var parent = scene.Add(new Entity("Parent"));
        var child = scene.Add(new Entity("Child"), parent);

        Assert.True(scene.IsEntityVisible(child));
        Assert.False(scene.IsEntityLocked(child));

        parent.Visible = false;
        Assert.False(scene.IsEntityVisible(child));
        parent.Visible = true;

        child.LayerId = hidden.Id;
        Assert.False(scene.IsEntityVisible(child));
        child.LayerId = locked.Id;
        Assert.True(scene.IsEntityVisible(child));
        Assert.True(scene.IsEntityLocked(child));

        child.LayerId = Guid.NewGuid(); // unknown layer: visible and unlocked
        Assert.True(scene.IsEntityVisible(child));
        Assert.False(scene.IsEntityLocked(child));
        child.Locked = true;
        Assert.True(scene.IsEntityLocked(child));
    }

    [Fact]
    public void Per_frame_lookups_do_not_allocate()
    {
        var scene = new Scene();
        for (int i = 0; i < 20; i++) scene.Layers.Add(new Layer { Name = $"L{i}", Visible = i % 2 == 0 });
        var entities = new List<Entity>();
        for (int i = 0; i < 200; i++)
        {
            var e = scene.Add(new Entity($"E{i}") { LayerId = scene.Layers[i % scene.Layers.Count].Id });
            entities.Add(scene.Add(new Entity($"C{i}"), e));
        }
        var unknown = Guid.NewGuid();

        int found = 0;
        void Run()
        {
            foreach (var e in entities)
            {
                if (scene.FindLayer(e.LayerId) is { Visible: true }) found++;
                if (scene.IsEntityVisible(e)) found++;
                if (scene.IsEntityLocked(e)) found++;
                if (scene.Find(e.Id) is not null) found++;
                if (e.IsVisibleInHierarchy) found++;
                if (e.Get<FixtureComponent>() is not null) found++;
            }
            if (scene.FindLayer(unknown) is not null) found++;
            foreach (var layer in scene.Layers)
                if (layer.Visible) found++;
            _ = scene.DefaultLayer;
        }

        Run(); // warm up (JIT, dictionary buckets)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Run();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(found > 0);
    }

    [Fact]
    public void Component_changes_through_set_value_for_component_raise_changes()
    {
        var scene = new Scene();
        var e = scene.Add(new Entity("Wall"));
        var wall = e.Add(new WallComponent());
        var rec = new Recorder(scene);
        var undo = new UndoStack();

        undo.Execute(SetValueCommand.ForComponent(wall, "Width", () => wall.Width, w => wall.Width = w, 6f));
        Assert.Equal(6f, wall.Width);
        Assert.Same(wall, rec.Single(SceneChangeKind.ComponentChanged).Component);
        undo.Undo();
        Assert.Equal(4f, wall.Width);
        rec.Single(SceneChangeKind.ComponentChanged);
        undo.Redo();
        rec.Single(SceneChangeKind.ComponentChanged);
    }

    [Fact]
    public void Transforms_names_and_components_still_raise_their_changes()
    {
        var scene = new Scene();
        var e = scene.Add(new Entity("E"));
        var rec = new Recorder(scene);
        e.Name = "F";
        rec.Single(SceneChangeKind.Renamed);
        e.Local = new Transform(new Vector3(1, 2, 3));
        rec.Single(SceneChangeKind.TransformChanged);
        var c = e.Add(new GroupComponent());
        rec.Single(SceneChangeKind.ComponentAdded);
        c.NotifyChanged();
        rec.Single(SceneChangeKind.ComponentChanged);
        e.Remove(c);
        rec.Single(SceneChangeKind.ComponentRemoved);
    }

    private static void Set(Entity e, SceneChangeKind kind, bool on, Guid layer)
    {
        switch (kind)
        {
            case SceneChangeKind.VisibilityChanged:
                e.Visible = !on;
                break;
            case SceneChangeKind.LockedChanged:
                e.Locked = on;
                break;
            default:
                e.LayerId = layer;
                break;
        }
    }
}
