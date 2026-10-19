using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Rendering;

namespace Candela.App.Tests;

/// <summary>
/// The editor's side of the engine follow-ups: the work light of the preview (view setting, lux overlay, final renders),
/// autosave reconfigured on the running session, and view settings changed in place counting as unsaved changes that
/// reach the autosave — with the saved camera of a project. Headless: a memory renderer stands in for the GPU.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class ViewSettingsFollowUpTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public ViewSettingsFollowUpTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void The_work_light_follows_the_view_setting_leaves_the_lux_overlay_and_final_renders_alone()
    {
        using var engine = StageTestSupport.CreateEngine(_dir);
        StageTestSupport.AddWall(engine, "Floor", 8f, 6f, WallKind.Floor);
        StageTestSupport.AddFixture(engine, "Dark head", 1, new Vector3(0f, 0f, 5f));
        using var renderer = new MemoryRenderer();
        using var content = new EngineViewportContent(engine, renderer, loadFixtureModelsInBackground: false);
        var sync = content.Binding;

        // A dark rig in the default Auto mode gets the work light: the stage is not black.
        Assert.Equal(WorkLightMode.Auto, engine.View.WorkLight);
        engine.Tick(1 / 30.0);
        content.MarkTicked();
        content.Sync();
        Assert.Equal(0, engine.Lights.Length);
        Assert.True(sync.IsWorkLightOn);
        Assert.Equal(WorkLightRig.LightCount, renderer.MemoryWorld.Lights.Length);

        // Off in the view settings: gone at once (no tick needed), and the project has an unsaved change.
        Assert.True(ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.Off));
        Assert.True(engine.IsModified);
        content.Sync();
        Assert.False(sync.IsWorkLightOn);
        Assert.Empty(renderer.MemoryWorld.Lights);

        // On, but a viewport shows the lux overlay: one light list serves every view, so the work light stays out.
        ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.On);
        content.LuxOverlayShown = true;
        content.Sync();
        Assert.False(sync.IsWorkLightOn);
        content.LuxOverlayShown = false;
        content.Sync();
        Assert.True(sync.IsWorkLightOn);

        // A final render gets no work light unless it asks for one, whatever the preview uses …
        using var job = new RenderJobRunner();
        var request = new RenderRequest(new RenderCamera(), new ViewOptions(), new PreviewSettings(), 32, 32, 1, 2, _dir.File("still.png"), null);
        Assert.True(job.Start(request, engine));
        Assert.Equal(WorkLightMode.Off, job.WorkLightInUse);
        job.Step(engine, renderer, 0);
        Assert.Equal(RenderJobState.Completed, job.State);
        Assert.NotEmpty(renderer.RenderedLights);
        Assert.All(renderer.RenderedLights, lights => Assert.Empty(lights));
        Assert.Null(job.WorkLightInUse);
        // … and the preview gets its own back on the next sync.
        content.WorkLightOverride = job.WorkLightInUse;
        content.Sync();
        Assert.True(sync.IsWorkLightOn);

        // Asked for: the render has the rig.
        renderer.RenderedLights.Clear();
        Assert.True(job.Start(request with { OutputPath = _dir.File("lit.png") } with { WorkLight = WorkLightMode.On }, engine));
        content.WorkLightOverride = job.WorkLightInUse;
        content.Sync();
        job.Step(engine, renderer, 0);
        Assert.All(renderer.RenderedLights, lights => Assert.Equal(WorkLightRig.LightCount, lights.Length));
    }

    [Fact]
    public void The_render_panel_asks_for_no_work_light_unless_chosen()
    {
        using var engine = StageTestSupport.CreateEngine(_dir);
        using var tools = StageTestSupport.CreateTools();
        var panel = new Candela.App.Panels.RenderPanel(StagePanelContext.ForEngine(engine, tools), _ => { });
        Assert.Equal(WorkLightMode.Off, panel.BuildRequest(tools.FinalRender.Live, null).WorkLight);
        panel.WorkLight = WorkLightMode.Auto;
        Assert.Equal(WorkLightMode.Auto, panel.BuildRequest(tools.FinalRender.Live, null).WorkLight);
        panel.WorkLight = (WorkLightMode)42;
        Assert.Equal(WorkLightMode.Off, panel.WorkLight);
    }

    [Fact]
    public async Task View_settings_changed_in_place_are_unsaved_changes_and_reach_the_autosave()
    {
        _settings.Settings.AutosaveMinutes = 5;
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir, autosave: true);
        var engine = session.Engine!;
        using var tools = StageTestSupport.CreateTools();
        session.Update(0.01);
        tools.BindProject(engine);
        Assert.False(engine.IsModified); // adopting the project's own values is not a change

        // Preview panel edit, a chosen preset, a legend's gradient, the render settings: each one is an unsaved change.
        long version = engine.SettingsVersion;
        tools.Preview.Live.Bloom = !tools.Preview.Live.Bloom;
        tools.Preview.MarkEdited();
        tools.BindProject(engine);
        Assert.True(engine.IsModified);
        Assert.True(engine.SettingsVersion > version);
        string project = _dir.File("show.candela");
        Assert.True(session.SaveAs(project).Succeeded);
        Assert.False(engine.IsModified);
        tools.BindProject(engine);
        Assert.False(engine.IsModified);

        tools.Lux.Live.RangeMax *= 2f;
        tools.Lux.MarkEdited();
        tools.BindProject(engine);
        Assert.True(engine.IsModified);
        Assert.True(session.Save().Succeeded);

        Assert.True(tools.Preview.Select(Candela.Core.Presets.PresetLibrary<PreviewSettings>.DefaultName));
        tools.BindProject(engine);
        Assert.True(engine.IsModified);
        Assert.True(session.Save().Succeeded);

        // The work light and the saved camera: a change marks, the same value again does not.
        Assert.True(ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.On));
        Assert.True(engine.IsModified);
        Assert.True(session.Save().Succeeded);
        version = engine.SettingsVersion;
        Assert.False(ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.On));
        var camera = new ViewportCamera { Target = new Vector3(1f, 2f, 1.5f), Distance = 12f };
        Assert.True(ViewSettingsEdits.StoreCamera(engine, camera));
        Assert.True(engine.IsModified);
        Assert.True(session.Save().Succeeded);
        Assert.False(ViewSettingsEdits.StoreCamera(engine, camera));
        Assert.Equal(version + 1, engine.SettingsVersion);
        Assert.False(engine.IsModified);

        // Reopened: the values come back and nothing counts as changed.
        Assert.True(session.Open(project).Succeeded);
        tools.BindProject(engine);
        Assert.False(engine.IsModified);
        Assert.Equal(WorkLightMode.On, engine.View.WorkLight);
        Assert.NotNull(engine.View.Camera);
        Assert.Equal(new Vector3(1f, 2f, 1.5f), engine.View.Camera!.Target);

        // The autosave stores a marked view change (and nothing before it).
        var autosave = engine.Autosave!;
        engine.ConfigureAutosave(true, TimeSpan.FromMilliseconds(10));
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(15);
            session.Update(0.015);
        }

        await autosave.FlushAsync();
        Assert.Null(autosave.LastSavedUtc);
        tools.Noise.Live.Logarithmic = !tools.Noise.Live.Logarithmic;
        tools.Noise.MarkEdited();
        tools.BindProject(engine);
        for (int i = 0; i < 200 && autosave.LastSavedUtc is null; i++)
        {
            Thread.Sleep(15);
            session.Update(0.015);
            await autosave.FlushAsync();
        }

        Assert.NotNull(autosave.LastSavedUtc);
    }

    [Fact]
    public void Autosave_setting_changes_reconfigure_the_running_session()
    {
        _settings.Settings.AutosaveMinutes = 5;
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir, autosave: true);
        var engine = session.Engine!;
        session.Update(0.01);
        var service = engine.Autosave!;
        string key = service.SessionKey;
        Assert.StartsWith(_dir.File("autosave"), service.Directory, StringComparison.Ordinal);
        Assert.True(engine.IsAutosaveEnabled);

        _settings.Settings.AutosaveMinutes = 0;
        session.Update(0.01);
        Assert.False(engine.IsAutosaveEnabled);
        Assert.Same(service, engine.Autosave);
        Assert.False(service.Enabled);

        _settings.Settings.AutosaveMinutes = 3;
        session.Update(0.01);
        Assert.Same(service, engine.Autosave);
        Assert.Equal(key, service.SessionKey);
        Assert.True(service.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(3), service.Interval);

        // A project opened later keeps the interval and folder.
        Assert.True(session.New().Succeeded);
        Assert.Equal(TimeSpan.FromMinutes(3), engine.AutosaveInterval);
        Assert.StartsWith(_dir.File("autosave"), engine.Autosave!.Directory, StringComparison.Ordinal);

        // Off from the start: nothing is set up until it is switched on, then in this session's folder.
        _settings.Settings.AutosaveMinutes = 0;
        using var second = new TempDir();
        using var off = EngineTestSupport.CreateSession(_log, _settings, second, autosave: true);
        off.Update(0.01);
        Assert.Null(off.Engine!.Autosave);
        Assert.Null(off.Engine.AutosaveInterval);
    }

    [Fact]
    public void A_saved_camera_round_trips_and_damaged_values_are_ignored()
    {
        var camera = new ViewportCamera { Target = new Vector3(2f, 3f, 1f), Distance = 14f, FovY = 0.7f, Ev100 = 9.5f };
        camera.Orient(0.6f, 0.35f);
        var saved = ViewSettingsEdits.ToSaved(camera);
        var back = new ViewportCamera();
        Assert.True(ViewSettingsEdits.Apply(saved, back));
        Assert.Equal(camera.Yaw, back.Yaw, 4);
        Assert.Equal(camera.Pitch, back.Pitch, 4);
        Assert.Equal(camera.Distance, back.Distance, 3);
        Assert.Equal(camera.FovY, back.FovY, 4);
        Assert.Equal(camera.Ev100, back.Ev100, 4);
        Assert.Equal(camera.Projection, back.Projection);
        Assert.True(Vector3.Distance(camera.Target, back.Target) < 1e-4f);

        // Orthographic: the stored eye sits at the orbit distance, not kilometres away.
        camera.SnapTo(ViewDirection.Top);
        saved = ViewSettingsEdits.ToSaved(camera);
        Assert.True(saved.Orthographic);
        Assert.True(Vector3.Distance(saved.Eye, saved.Target) < 100f);
        Assert.True(ViewSettingsEdits.Apply(saved, back));
        Assert.Equal(ProjectionKind.Orthographic, back.Projection);
        Assert.Null(back.SnappedView);
        Assert.Equal(camera.OrthoHeight, back.OrthoHeight, 3);

        var before = (back.Target, back.Yaw, back.Pitch, back.Distance);
        Assert.False(ViewSettingsEdits.Apply(new SavedCamera { Eye = new Vector3(float.NaN, 0f, 0f) }, back));
        Assert.False(ViewSettingsEdits.Apply(new SavedCamera { Eye = Vector3.One, Target = Vector3.One }, back));
        Assert.Equal(before, (back.Target, back.Yaw, back.Pitch, back.Distance));
        Assert.True(ViewSettingsEdits.Apply(new SavedCamera { FovYDegrees = float.PositiveInfinity, OrthoHeight = -3f, Ev100 = float.NaN }, back));
        Assert.True(float.IsFinite(back.FovY) && back.OrthoHeight > 0f && float.IsFinite(back.Ev100));
    }
}
