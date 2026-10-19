using System.Numerics;
using Candela.App.Editing;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;

namespace Candela.App.Tests;

[Collection(UiStateCollection.Name)]
public sealed class CablingProbeTests
{
    [Fact]
    public void Probe_length_after_device_move()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var s = new CablingScene(dir);
        var tool = s.Tool;
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CablingScene.DeviceAt(s.Console));
        tool.ChoosePort("DMX A");
        tool.Click(CablingScene.DeviceAt(s.Spots[0]));
        tool.Cancel();
        var (cableEntity, _) = Assert.Single(s.Cables());
        float before = s.Engine.NetworkSolution.FindCable(cableEntity.Id)!.LengthM;
        s.Spots[0].Local = new Transform(new Vector3(-12f, 0f, 5f));
        s.Engine.Tick(0.02);
        float after = s.Engine.NetworkSolution.FindCable(cableEntity.Id)!.LengthM;
        float model = CableLinkModel.Build(s.Engine, cableEntity.Id)!.Links[0].Length is var l ? float.Parse(l.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) : 0;
        Assert.True(MathF.Abs(after - before) > 1f, $"before {before} after {after} model {model}");
    }
}

[Collection(UiStateCollection.Name)]
public sealed class CablingPerfProbe
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public CablingPerfProbe(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Fact]
    public void Probe_solve_cost_on_fokina_with_many_cables()
    {
        Loc.SetLanguage("en");
        using var dir = new TempDir();
        using var log = TestLog.Create();
        var settings = new Candela.App.Settings.SettingsStore(log, dir.File("settings.json"));
        settings.Load();
        using var session = EngineTestSupport.CreateSession(log, settings, dir);
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);
        var engine = session.Engine!;
        var tools = StageTestSupport.CreateTools();
        var stage = StagePanelContext.ForEngine(engine, tools);
        var tool = new CableTool(stage);
        var console = new Candela.Core.SceneGraph.Entity("Desk") { Local = new Transform(new Vector3(0, -10, 0)) };
        console.Add(DeviceCatalog.Find("console-dmx-8")!.CreateComponent());
        engine.Scene.Add(console);
        var fixtures = engine.Scene.Query<FixtureComponent>().Select(f => f.Entity).ToList();
        tool.CableTypeId = CableCatalog.Ids.DmxXlr5;
        tool.Click(CableTarget.Device(console.Id, console.WorldPosition));
        tool.ChooseUsable(0);
        int connected = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in fixtures)
        {
            tool.Click(CableTarget.Surface(f.WorldPosition + new Vector3(0, 0, 0.5f), Vector3.UnitZ, Guid.Empty));
            var r = tool.Click(CableTarget.Device(f.Id, f.WorldPosition));
            if (r == CableClickOutcome.PickPort) r = tool.ChooseUsable(0);
            if (r == CableClickOutcome.Connected) connected++;
            if (!tool.IsDrawing) { tool.Click(CableTarget.Device(console.Id, console.WorldPosition)); tool.ChooseUsable(0); }
        }
        _out.WriteLine($"connected {connected} of {fixtures.Count} in {sw.ElapsedMilliseconds} ms; cables {engine.Scene.Query<CableComponent>().Count()}");
        sw.Restart();
        for (int i = 0; i < 20; i++) engine.Network.Solve(engine.Scene);
        _out.WriteLine($"solve {sw.Elapsed.TotalMilliseconds / 20:0.00} ms");
        var sol = engine.NetworkSolution;
        _out.WriteLine($"driven {fixtures.Count(f => sol.IsConsoleDriven(f.Id))}");
        long v0 = engine.Scene.Version;
        for (int i = 0; i < 10; i++) engine.Tick(0.02);
        _out.WriteLine($"version {v0} -> {engine.Scene.Version}; solution version {engine.NetworkSolution.SceneVersion}");
        engine.RefreshIssues();
        foreach (var g in engine.Issues.Current.GroupBy(i => i.Code)) _out.WriteLine($"{g.Key}: {g.Count()} e.g. {g.First().Message}");
    }
}

[Collection(UiStateCollection.Name)]
public sealed class CablingUiProbe : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Candela.App.Diagnostics.AppLog _log = TestLog.Create();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [SkippableFact]
    public void Probe_picker_with_four_viewports()
    {
        var settings = new Candela.App.Settings.SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        string rig = _dir.File("rig.candela"), cabled = _dir.File("cabled.candela");
        using (var session = EngineTestSupport.CreateSession(_log, settings, _dir))
        {
            CablingScene.Populate(session.Engine!);
            Assert.True(session.SaveAs(rig).Succeeded);
        }

        string picker = "/tmp/claude-0/-home-user-gdtf/c6c26b94-54b1-5652-84a5-cb0740acac64/scratchpad/picker4.png";
        string script = _dir.Write("cables.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 6 },
                { "type": "layout", "name": "viewports-4" },
                { "type": "wait", "frames": 6 },
                { "type": "viewport", "viewport": "Viewport1", "view": "Top", "shading": "Colorless" },
                { "type": "wait", "frames": 3 },
                { "type": "menu", "path": "Tools/Cable/dmx-xlr5" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Console 1" },
                { "type": "wait", "frames": 3 },
                { "type": "screenshot", "path": "{{picker}}" },
                { "type": "key", "chord": "1" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 1" },
                { "type": "wait", "frames": 2 },
                { "type": "key", "chord": "Escape" },
                { "type": "wait", "frames": 3 },
                { "type": "action", "id": "tool.select" },
                { "type": "save", "path": "{{cabled.Replace("\\", "/")}}" },
                { "type": "wait", "frames": 3 }
            ] }
            """);
        var run = EditorProcess.Run(_dir, new[] { "--open", rig, "--screenshot", _dir.File("end.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
        using var reopened = EngineTestSupport.CreateSession(_log, settings, _dir);
        Assert.True(reopened.Open(cabled).Succeeded);
        var scene = reopened.Scene;
        var plugs = scene.Query<CableComponent>().SelectMany(c => c.Component.Endpoints).Select(p => $"{scene.Find(p.EntityId)?.Name}:{p.PortName}").ToList();
        Assert.Contains("Console 1:DMX A", plugs);
    }

    [SkippableFact]
    public void Probe_select_fixture_with_cables()
    {
        var settings = new Candela.App.Settings.SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        string rig = _dir.File("rig.candela");
        using (var session = EngineTestSupport.CreateSession(_log, settings, _dir))
        {
            CablingScene.Populate(session.Engine!);
            Assert.True(session.SaveAs(rig).Succeeded);
        }

        string shot = "/tmp/claude-0/-home-user-gdtf/c6c26b94-54b1-5652-84a5-cb0740acac64/scratchpad/select.png";
        string shot2 = "/tmp/claude-0/-home-user-gdtf/c6c26b94-54b1-5652-84a5-cb0740acac64/scratchpad/select2.png";
        string script = _dir.Write("cables.json", $$"""
            { "actions": [
                { "type": "wait", "frames": 6 },
                { "type": "menu", "path": "Tools/Cable/dmx-xlr5" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Console 1" },
                { "type": "wait", "frames": 3 },
                { "type": "key", "chord": "1" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 1" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 2" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 3" },
                { "type": "wait", "frames": 2 },
                { "type": "key", "chord": "Escape" },
                { "type": "wait", "frames": 3 },
                { "type": "action", "id": "tool.select" },
                { "type": "wait", "frames": 2 },
                { "type": "click", "entity": "Spot 2" },
                { "type": "wait", "frames": 4 },
                { "type": "screenshot", "path": "{{shot}}" },
                { "type": "click", "entity": "Console 1" },
                { "type": "wait", "frames": 4 },
                { "type": "screenshot", "path": "{{shot2}}" }
            ] }
            """);
        var run = EditorProcess.Run(_dir, new[] { "--open", rig, "--screenshot", _dir.File("end.png"), "--script", script });
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Output);
    }
}
