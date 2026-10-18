using System.Numerics;
using System.Text.Json;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;
using Candela.Core.Settings;
using Xunit.Abstractions;

namespace Candela.Core.Tests;

public class ReviewProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void P01_FromFile_of_ToFile_keeps_original_scene_notifications()
    {
        var scene = new Scene();
        var rig = new Layer { Name = "Rig" };
        scene.Layers.Add(rig);
        var copy = SceneSerializer.FromFile(SceneSerializer.ToFile(scene));
        var changes = new List<SceneChange>();
        scene.Changed += changes.Add;
        var copyChanges = new List<SceneChange>();
        copy.Changed += copyChanges.Add;
        rig.Visible = false;
        scene.Settings.GridSpacing = 2;
        output.WriteLine($"orig {changes.Count} copy {copyChanges.Count}");
        Assert.Equal(2, changes.Count);
        Assert.Empty(copyChanges);
    }

    [Fact]
    public void P02_FromFile_of_ToFile_keeps_components()
    {
        var scene = new Scene();
        var e = scene.Add(new Entity("W"));
        e.Add(new WallComponent());
        var issues = new List<Candela.Core.Diagnostics.Issue>();
        var copy = SceneSerializer.FromFile(SceneSerializer.ToFile(scene), issues);
        foreach (var i in issues) output.WriteLine(i.Message);
        Assert.Single(copy.Roots[0].Components);
        Assert.Single(e.Components);
    }

    [Fact]
    public void P03_Selection_add_of_primary_is_not_a_change()
    {
        var s = new Selection();
        var id = Guid.NewGuid();
        s.Add(id);
        int n = 0;
        s.Changed += () => n++;
        s.Add(id);
        Assert.Equal(0, n);
    }

    [Fact]
    public void P04_Key_chord_case()
    {
        var set = KeyBindingSet.CreateDefault();
        Assert.Contains(EditorActions.FileSave, set.ActionsFor(KeyChord.Parse("ctrl+s")));
    }

    [Fact]
    public void P05_Clone_is_independent()
    {
        var scene = new Scene();
        var e = scene.Add(new Entity("Cable") { UnknownProperties = new() { ["x"] = JsonDocument.Parse("1").RootElement } });
        var cable = e.Add(new CableComponent
        {
            Nodes = { new CableNode { Position = new Vector3(1, 2, 3) }, new CableNode() },
            UnknownProperties = new() { ["future"] = JsonDocument.Parse("{\"a\":[1,2]}").RootElement },
        });
        var r = EntityCloner.Clone(e);
        var copy = r.Roots[0].Get<CableComponent>()!;
        Assert.NotSame(cable.Nodes, copy.Nodes);
        Assert.NotSame(cable.Nodes[0], copy.Nodes[0]);
        copy.Nodes[0].Position = Vector3.Zero;
        Assert.Equal(new Vector3(1, 2, 3), cable.Nodes[0].Position);
        r.Roots[0].UnknownProperties!["y"] = default;
        Assert.False(e.UnknownProperties!.ContainsKey("y"));
        Assert.NotSame(cable.UnknownProperties, copy.UnknownProperties);
    }

    [Fact]
    public void P06_Settings_shared_between_scenes()
    {
        var a = new Scene();
        var b = new Scene();
        b.Settings = a.Settings;
        int na = 0;
        a.Changed += _ => na++;
        a.Settings.GridSpacing = 3;
        output.WriteLine($"a changes {na}");
    }

    [Fact]
    public void P07_Reparent_events()
    {
        var scene = new Scene();
        var p = scene.Add(new Entity("P") { Local = new Transform(new Vector3(1, 0, 0)) });
        var c = scene.Add(new Entity("C"));
        var changes = new List<SceneChange>();
        scene.Changed += changes.Add;
        scene.Reparent(c, p);
        output.WriteLine(string.Join(",", changes.Select(x => x.Kind)));
    }

    [Fact]
    public void P08_Console_outputs_null_clone()
    {
        var c = new ConsoleComponent { Outputs = null!, ProtocolId = null };
        var copy = (ConsoleComponent)c.Clone();
        Assert.Null(copy.Outputs);
        Assert.Null(copy.ProtocolId);
        var json = CandelaJson.Serialize<Component>(c);
        output.WriteLine(json);
    }

    [Fact]
    public void P09_ComponentWithNaN_clone()
    {
        var f = new FixtureComponent { ManualAttributes = { ["Pan"] = float.NaN }, FocusPoint = new Vector3(float.PositiveInfinity, 0, 0) };
        var copy = (FixtureComponent)f.Clone();
        Assert.True(float.IsNaN(copy.ManualAttributes["Pan"]));
        Assert.True(float.IsPositiveInfinity(copy.FocusPoint!.Value.X));
    }

    [Fact]
    public void P10_Remove_layer_while_entities_on_it()
    {
        var scene = new Scene();
        var l = new Layer { Name = "X", Visible = false };
        scene.Layers.Add(l);
        var e = scene.Add(new Entity("E") { LayerId = l.Id });
        Assert.False(scene.IsEntityVisible(e));
        scene.Layers.Remove(l);
        Assert.True(scene.IsEntityVisible(e));
        // the removed layer edits are silent now
        int n = 0;
        scene.Changed += _ => n++;
        l.Visible = true;
        Assert.Equal(0, n);
    }

    [Fact]
    public void P11_Layer_in_two_scenes()
    {
        var a = new Scene();
        var b = new Scene();
        var l = new Layer { Name = "Shared" };
        a.Layers.Add(l);
        b.Layers.Add(l);
        b.Layers.Remove(l);
        int n = 0;
        a.Changed += _ => n++;
        l.Visible = false;
        output.WriteLine($"a got {n}");
    }

    [Fact]
    public void P12_Duplicate_redo_after_parent_removed()
    {
        var scene = new Scene();
        var truss = scene.Add(new Entity("Truss"));
        var fix = scene.Add(new Entity("Spot"), truss);
        fix.Add(new FixtureComponent { MountedOn = truss.Id });
        var undo = new UndoStack();
        var dup = new DuplicateEntitiesCommand(scene, new[] { fix });
        undo.Execute(dup);
        Assert.Same(truss, dup.Copies[0].Parent);
        undo.Undo();
        undo.Redo();
        Assert.Same(truss, dup.Copies[0].Parent);
        Assert.Equal(truss.Id, dup.Copies[0].Get<FixtureComponent>()!.MountedOn);
    }

    [Fact]
    public void P13_Visible_change_through_reparent_hidden_parent()
    {
        var scene = new Scene();
        var hidden = scene.Add(new Entity("H") { Visible = false });
        var e = scene.Add(new Entity("E"));
        Assert.True(scene.IsEntityVisible(e));
        scene.Reparent(e, hidden);
        Assert.False(scene.IsEntityVisible(e));
    }

    [Fact]
    public void P14_Deserialize_settings_and_layer_independent()
    {
        var scene = new Scene();
        scene.Layers.Add(new Layer { Name = "A" });
        var json = SceneSerializer.ToJson(scene);
        var copy = SceneSerializer.FromJson(json);
        Assert.Equal(2, copy.Layers.Count);
        Assert.NotSame(scene.Layers[1], copy.Layers[1]);
        Assert.Equal(scene.Layers[1].Id, copy.Layers[1].Id);
    }

    [Fact]
    public void P15_KeyChord_roundtrip_all_known_keys_with_all_modifiers()
    {
        var keys = new List<string>();
        for (char c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        keys.AddRange(new[] { "F1", "F24", "Numpad5", "Comma", "Minus", "Equal", "Delete" });
        for (int m = 0; m < 16; m++)
            foreach (var k in keys)
            {
                var chord = new KeyChord(k, (KeyModifiers)m);
                Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
            }
    }

    [Fact]
    public void P16_Remap_cable_with_cleared_anchor_keeps_world_position()
    {
        var scene = new Scene();
        var wall = scene.Add(new Entity("Wall") { Local = new Transform(new Vector3(5, 0, 0)) });
        var cableE = scene.Add(new Entity("Cable") { Local = new Transform(new Vector3(0, 1, 0)) });
        var cable = cableE.Add(new CableComponent
        {
            Nodes = { new CableNode { Anchor = new CableAnchor(wall.Id, new Vector3(0, 0, 2)) } },
        });
        var target = new Scene();
        var r = EntityCloner.Clone(cableE, new EntityCloneOptions { ExternalReferences = ExternalReferenceHandling.Clear, TargetScene = target, RootsKeepWorldTransform = true });
        var node = r.Roots[0].Get<CableComponent>()!.Nodes[0];
        Assert.Null(node.Anchor);
        // world position: wall (5,0,0) + offset (0,0,2) = (5,0,2); cable at (0,1,0) => local (5,-1,2)
        Assert.Equal(new Vector3(5, -1, 2), node.Position);
    }

    [Fact]
    public void P17_Layer_index_insert_at_zero_replaces_default()
    {
        var scene = new Scene();
        var first = scene.Layers[0];
        var l = new Layer { Name = "New default" };
        scene.Layers.Insert(0, l);
        Assert.Same(l, scene.DefaultLayer);
        Assert.Same(first, scene.FindLayer(first.Id));
    }

    [Fact]
    public void P18_Scene_add_of_subtree_layer_inherit()
    {
        var scene = new Scene();
        var rig = new Layer { Name = "Rig" };
        scene.Layers.Add(rig);
        var root = new Entity("R") { LayerId = rig.Id };
        var child = new Entity("C");
        root.AddChild(child);
        var grand = new Entity("G");
        child.AddChild(grand);
        scene.Add(root);
        Assert.Equal(rig.Id, grand.LayerId);
    }

    [Fact]
    public void P19_Undo_group_of_flags_one_step()
    {
        var scene = new Scene();
        var a = scene.Add(new Entity("A"));
        var b = scene.Add(new Entity("B"));
        var undo = new UndoStack();
        var changes = new List<SceneChange>();
        scene.Changed += changes.Add;
        using (undo.Group("Hide"))
        {
            undo.Execute(new SetValueCommand<bool>("Hide A", () => a.Visible, v => a.Visible = v, false));
            undo.Execute(new SetValueCommand<bool>("Hide B", () => b.Visible, v => b.Visible = v, false));
        }
        Assert.Equal(2, changes.Count);
        changes.Clear();
        undo.Undo();
        Assert.Equal(2, changes.Count);
        Assert.True(a.Visible && b.Visible);
    }

    [Fact]
    public void P20_Modify_component_command_on_cable_restores_lists()
    {
        var scene = new Scene();
        var e = scene.Add(new Entity("C"));
        var cable = e.Add(new CableComponent { Nodes = { new CableNode(), new CableNode() } });
        var ids = cable.Nodes.Select(n => n.Id).ToList();
        var undo = new UndoStack();
        undo.Execute(ModifyComponentCommand.Create(cable, c => c.Nodes.RemoveAt(0)));
        Assert.Single(cable.Nodes);
        undo.Undo();
        Assert.Equal(ids, cable.Nodes.Select(n => n.Id));
    }
}
