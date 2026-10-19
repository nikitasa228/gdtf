using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Editing;
using Candela.App.Settings;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;

namespace Candela.App.Tests;

/// <summary>
/// The cable tool driven by scripted input in the real editor under a virtual X server: the DMX type chosen in
/// Tools → Cable, the console clicked (the port picker opens; key 1 takes DMX A), a route point on the wall, the
/// fixtures under the truss clicked one after the other (each cable continues from the fixture's DMX out), Escape to
/// finish, a branch from the middle of the first cable to the third fixture ended with a right click, a power cable
/// from the distro over the floor, and a floor drag cancelled with Escape. The saved project is then read back: the
/// cables, their anchors and plugs, and the fixtures the console drives. Skipped where xvfb-run or a Vulkan device is
/// missing.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class CableEndToEndTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [SkippableFact]
    public void Cables_drawn_with_scripted_input_are_routed_plugged_branched_and_saved()
    {
        var settings = new SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        string rig = _dir.File("rig.candela"), cabled = _dir.File("cabled.candela");
        var wallPoint = new Vector3(-3f, 3.95f, 2.5f);
        Vector3 branchAt;
        using (var session = EngineTestSupport.CreateSession(_log, settings, _dir))
        {
            var console = CablingScene.Populate(session.Engine!).Console;
            Assert.True(session.SaveAs(rig).Succeeded);
            // The middle of the run from the console's DMX A to the point on the wall (where the branch starts).
            var (port, index, count) = CablePorts.Find(session.Engine!, console, "DMX A");
            branchAt = (CablePorts.WorldPosition(console, port!, index, count) + wallPoint - new Vector3(0f, CableTool.SurfaceOffset, 0f)) * 0.5f;
        }

        string picker = _dir.File("picker.png"), drawn = _dir.File("drawn.png");
        string script = _dir.Write("cables.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 6 },
                { "type": "menu", "path": "Tools/Cable/dmx-xlr5" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Console 1" },
                { "type": "wait", "frames": 3 },
                { "type": "screenshot", "path": "{{Json(picker)}}" },
                { "type": "key", "chord": "1" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "world": {{Point(wallPoint)}} },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 1" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 2" },
                { "type": "wait", "frames": 2 },
                { "type": "key", "chord": "Escape" },
                { "type": "wait", "frames": 3 },
                { "type": "click", "world": {{Point(branchAt)}} },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 3" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "world": [3.5, 1, 0], "button": "right" },
                { "type": "wait", "frames": 3 },
                { "type": "menu", "path": "Tools/Cable/powercon-true1" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Distro 1" },
                { "type": "wait", "frames": 3 },
                { "type": "key", "chord": "2" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "world": [4, -1, 0] },
                { "type": "wait", "frames": 2 },
                { "type": "screenshot", "path": "{{Json(drawn)}}" },
                { "type": "click", "world": [3, 1, 0], "button": "right" },
                { "type": "wait", "frames": 3 },
                { "type": "action", "id": "tool.floor" },
                { "type": "press", "world": [-4, -3, 0] },
                { "type": "move", "world": [-1, -1, 0] },
                { "type": "wait", "frames": 2 },
                { "type": "move", "world": [1, 1, 0] },
                { "type": "wait", "frames": 2 },
                { "type": "key", "chord": "Escape" },
                { "type": "wait", "frames": 2 },
                { "type": "release" },
                { "type": "wait", "frames": 2 },
                { "type": "action", "id": "tool.select" },
                { "type": "save", "path": "{{Json(cabled)}}" },
                { "type": "wait", "frames": 3 }
            ] }
            """);
        var run = EditorProcess.Run(_dir, new[] { "--open", rig, "--screenshot", _dir.File("end.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        Assert.DoesNotContain("ERR [", run.Output);
        Assert.Contains("Saved 'cabled.candela'", run.Output);
        Assert.Equal(1600, EditorProcess.LoadPng(picker).Width);
        Assert.Equal(1600, EditorProcess.LoadPng(drawn).Width);

        using var reopened = EngineTestSupport.CreateSession(_log, settings, _dir);
        var opened = reopened.Open(cabled);
        Assert.True(opened.Succeeded, opened.Message);
        var scene = reopened.Scene;
        Entity Named(string name) => scene.All().Single(e => e.Name == name);
        var cables = scene.Query<CableComponent>().OrderBy(c => c.Entity.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "DMX 1", "DMX 2", "Power 1" }, cables.Select(c => c.Entity.Name));

        // DMX 1: console DMX A → a point on the wall → Spot 1, with a branch from the wall run to Spot 3.
        var dmx1 = cables[0].Component;
        Assert.Equal(CableCatalog.Ids.DmxXlr5, dmx1.CableTypeId);
        var plugs = dmx1.Endpoints.Select(p => $"{scene.Find(p.EntityId)?.Name}:{p.PortName}").OrderBy(p => p, StringComparer.Ordinal);
        Assert.Equal(new[] { "Console 1:DMX A", "Spot 1:DMX IN", "Spot 3:DMX IN" }, plugs);
        Assert.Contains(dmx1.Nodes, n => n.Anchor is { } a && a.EntityId == Named("Wall 1").Id);
        Assert.Contains(dmx1.Nodes, n => dmx1.Segments.Count(s => s.FromNode == n.Id || s.ToNode == n.Id) == 3);

        // DMX 2 continues the line from Spot 1 to Spot 2; the power cable runs from TRUE1 2 over the floor, unplugged.
        var dmx2 = cables[1].Component;
        Assert.Equal(new[] { "Spot 1:DMX THRU", "Spot 2:DMX IN" }, dmx2.Endpoints.Select(p => $"{scene.Find(p.EntityId)?.Name}:{p.PortName}").OrderBy(p => p, StringComparer.Ordinal));
        var power = cables[2].Component;
        Assert.Equal(CableCatalog.Ids.PowerconTrue1, power.CableTypeId);
        Assert.Equal("Distro 1:TRUE1 2", Assert.Single(power.Endpoints, p => p.EntityId != Guid.Empty) is var e ? $"{scene.Find(e.EntityId)?.Name}:{e.PortName}" : "");
        Assert.True(power.Nodes.Count >= 2);

        // The console drives all three fixtures (Spot 3 through the Y), and the cancelled floor drag left no floor.
        var solution = reopened.Engine!.NetworkSolution;
        Assert.All(new[] { "Spot 1", "Spot 2", "Spot 3" }, n => Assert.True(solution.IsConsoleDriven(Named(n).Id), n));
        Assert.DoesNotContain(scene.Query<WallComponent>(), w => w.Component.Kind == WallKind.Floor);
        Assert.True(Vector3.Distance(Named("Spot 1").WorldPosition, new Vector3(-2f, 0f, 5f)) < 1e-4f);
    }

    private static string Json(string path) => path.Replace("\\", "/");

    private static string Point(Vector3 p) => FormattableString.Invariant($"[{p.X:0.####}, {p.Y:0.####}, {p.Z:0.####}]");
}
