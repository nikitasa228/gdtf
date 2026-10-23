using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.App.Tests;

/// <summary>The editor session over the real stage engine: files, recent list, import/export, issues, recovery.</summary>
[Collection(UiStateCollection.Name)]
public sealed class EngineSessionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;
    private readonly EditorSession _session;

    public EngineSessionTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
        _session = EngineTestSupport.CreateSession(_log, _settings, _dir);
    }

    public void Dispose()
    {
        _session.Dispose();
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void A_new_session_is_an_empty_unmodified_scene_of_a_running_engine()
    {
        Assert.NotNull(_session.Engine);
        Assert.True(_session.EngineAvailable);
        Assert.Same(_session.Engine!.Scene, _session.Scene);
        Assert.Same(_session.Engine.Selection, _session.Selection);
        Assert.Same(_session.Engine.Undo, _session.Undo);
        Assert.Same(_session.Engine.Issues, _session.Issues);
        Assert.Equal(0, _session.Scene.Count);
        Assert.False(_session.IsModified);
        Assert.Equal("Untitled", _session.DisplayName);
        Assert.Null(_session.FilePath);
        Assert.Null(_session.Content);
        _session.Scene.Add(new Entity("x"));
        Assert.True(_session.IsModified);
    }

    [Fact]
    public void Opening_Fokina_loads_its_91_fixtures_and_save_as_reopens_unchanged()
    {
        var opened = _session.Open(EngineTestSupport.Fokina);
        Assert.True(opened.Succeeded, opened.Message);
        Assert.Contains("91", opened.Message);
        Assert.Equal(91, _session.FixtureCount());
        Assert.Null(_session.FilePath); // an MVR opens as a new, unsaved project
        Assert.Equal(Path.GetFullPath(EngineTestSupport.Fokina), _session.SourcePath);
        Assert.False(string.IsNullOrWhiteSpace(_session.DisplayName));

        var before = Snapshot(_session.Scene);
        string path = _dir.File("fokina");
        var saved = _session.SaveAs(path);
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(Path.GetFullPath(path + ".candela"), _session.FilePath);
        Assert.False(_session.IsModified);

        Assert.True(_session.New().Succeeded);
        Assert.Equal(0, _session.Scene.Count);
        var reopened = _session.Open(path + ".candela");
        Assert.True(reopened.Succeeded, reopened.Message);
        Assert.False(_session.IsModified);
        Assert.Equal(before, Snapshot(_session.Scene));
    }

    [Fact]
    public void Every_kind_of_file_is_imported_as_one_undo_step_and_the_scene_exports_as_MVR()
    {
        // A project to merge: one wall, saved and closed.
        var wall = new Entity("Back wall");
        wall.Add(new WallComponent { Kind = WallKind.Wall, Width = 4, Height = 3 });
        _session.Scene.Add(wall);
        string project = _dir.File("set.candela");
        Assert.True(_session.SaveAs(project).Succeeded);
        Assert.True(_session.New().Succeeded);
        Assert.Equal(_session.DisplayName, Loc.Get(Str.Untitled));

        var merged = _session.Import(project, ImportKind.Project);
        Assert.True(merged.Succeeded, merged.Message);
        Assert.NotEmpty(merged.Entities);
        Assert.Contains(_session.Scene.All(), e => e.Name == "Back wall" && e.Id != wall.Id);
        Assert.True(_session.IsModified);
        _session.Undo.Undo();
        Assert.Equal(0, _session.Scene.Count);
        _session.Undo.Redo();

        var mvr = _session.Import(EngineTestSupport.Fokina, ImportKind.Mvr);
        Assert.True(mvr.Succeeded, mvr.Message);
        Assert.Equal(91, _session.FixtureCount());

        string stl = _dir.Write("wedge.stl",
            "solid wedge\n facet normal 0 0 1\n  outer loop\n   vertex 0 0 0\n   vertex 1 0 0\n   vertex 0 1 0\n  endloop\n endfacet\n" +
            " facet normal 0 -1 0\n  outer loop\n   vertex 0 0 0\n   vertex 0 0 1\n   vertex 1 0 0\n  endloop\n endfacet\nendsolid wedge\n");
        var model = _session.Import(stl, ImportKind.Model, new Candela.Engine.EngineImportOptions { Placement = new Candela.Core.Math.Transform(new Vector3(1, 2, 0)) });
        Assert.True(model.Succeeded, model.Message);
        var modelEntity = _session.Scene.Find(Assert.Single(model.Entities))!;
        Assert.Equal(new Vector3(1, 2, 0), modelEntity.Local.Position);

        string png = Path.Combine(EngineTestSupport.Root, "docs", "images", "editor-view-cube.png");
        var image = _session.Import(png, ImportKind.Image, new Candela.Engine.EngineImportOptions { ImageMode = ImageMode.Poster });
        Assert.True(image.Succeeded, image.Message);
        Assert.Equal(ImageMode.Poster, _session.Scene.Find(Assert.Single(image.Entities))!.Get<ImageComponent>()!.Mode);

        // A GDTF the library already has is accepted without changing the (shared) library.
        int libraryCount = EngineTestSupport.Library.Count;
        var gdtf = _session.Import(EngineTestSupport.Library.Entries[0].FilePath, ImportKind.Gdtf);
        Assert.True(gdtf.Succeeded, gdtf.Message);
        Assert.Equal(libraryCount, EngineTestSupport.Library.Count);

        var export = _session.Export(_dir.File("show"), ExportKind.Mvr);
        Assert.True(export.Succeeded, export.Message);
        Assert.True(File.Exists(_dir.File("show.mvr")));
        Assert.True(_session.IsModified, "exporting is not saving the project");
        var reopened = _session.Open(_dir.File("show.mvr"));
        Assert.True(reopened.Succeeded, reopened.Message);
        Assert.Equal(91, _session.FixtureCount());
        // MVR has no walls: the wall travels as geometry under its name.
        Assert.Contains(_session.Scene.All(), e => e.Name == "Back wall");
    }

    [Fact]
    public void Missing_damaged_and_unsaved_files_fail_with_a_reason_and_keep_the_scene()
    {
        var keep = _session.Scene.Add(new Entity("keep"));
        var scene = _session.Scene;
        Assert.Equal(SessionOutcome.Failed, _session.Open(_dir.File("missing.mvr")).Outcome);
        Assert.Equal(SessionOutcome.Failed, _session.Open(_dir.Write("damaged.candela", "not a zip")).Outcome);
        Assert.Equal(SessionOutcome.Failed, _session.Open(_dir.Write("damaged.mvr", "not a zip")).Outcome);
        Assert.Equal(SessionOutcome.Failed, _session.Import(_dir.File("missing.glb"), ImportKind.Model).Outcome);
        Assert.Equal(SessionOutcome.Failed, _session.Import(_dir.Write("model.skp", "x"), ImportKind.Model).Outcome);
        var save = _session.Save();
        Assert.Equal(SessionOutcome.Failed, save.Outcome);
        Assert.Contains("Save As", save.Message);
        Assert.Same(scene, _session.Scene);
        Assert.Same(keep, _session.Scene.Find(keep.Id));
        Assert.Contains(_session.Issues.Current, i => i.Code == "engine.open-failed" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Russian_messages()
    {
        Loc.SetLanguage("ru");
        try
        {
            Assert.Contains("Новая сцена", _session.New().Message);
            Assert.Contains("не существует", _session.Open(_dir.File("missing.mvr")).Message);
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void Program_errors_go_to_the_issues_panel()
    {
        _session.ReportProgramError("app.frame-exception", "boom");
        var issue = Assert.Single(_session.Issues.Current, i => i.Code == "app.frame-exception");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(IssueCategory.Program, issue.Category);
    }

    [Fact]
    public void Deleted_entities_leave_the_selection_on_update()
    {
        var a = _session.Scene.Add(new Entity("a"));
        var b = _session.Scene.Add(new Entity("b"));
        _session.Selection.Set(new[] { a.Id, b.Id });
        _session.Scene.Remove(a);
        _session.Update(1 / 60.0);
        Assert.Equal(new[] { b.Id }, _session.Selection.Ids);
    }

    [Fact]
    public void New_resets_selection_and_history_and_raises_scene_replaced()
    {
        var a = _session.Scene.Add(new Entity("a"));
        _session.Selection.Set(a.Id);
        int replaced = 0;
        _session.SceneReplaced += () => replaced++;
        Assert.True(_session.New().Succeeded);
        Assert.True(_session.Selection.IsEmpty);
        Assert.False(_session.Undo.CanUndo);
        Assert.False(_session.IsModified);
        Assert.Equal(1, replaced);
    }

    [Fact]
    public void Recent_files_are_remembered_in_the_settings()
    {
        _session.RememberRecentFile(_dir.File("a.candela"));
        _session.RememberRecentFile(_dir.File("b.mvr"));
        Assert.Equal(_dir.File("b.mvr"), _settings.Settings.RecentFiles[0]);
        Assert.True(_settings.HasPendingSave);
        _session.ForgetRecentFile(_dir.File("b.mvr"));
        Assert.Equal(_dir.File("a.candela"), _settings.Settings.RecentFiles[0]);
    }

    [Fact]
    public void Disposed_sessions_refuse_updates()
    {
        _session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _session.Update(0.1));
    }

    [Fact]
    public void The_editor_simulates_smoke_in_real_time_except_while_a_render_job_holds_the_clock()
    {
        var simulation = _session.Engine!.Simulation;
        Assert.True(simulation.RealTime);
        Assert.False(simulation.IsSmokeBehindRealTime);
        _session.EngineClockHeld = true;
        Assert.False(simulation.RealTime);
        _session.EngineClockHeld = false;
        Assert.True(simulation.RealTime);
    }

    [Fact]
    public void Problems_the_smoke_solver_reports_from_its_own_thread_reach_the_issue_list()
    {
        // A haze volume and a wall far outside it: the solver, on its own thread, notes the obstacle it ignores — maybe
        // only after the analysis of the edit has run, which must then run again without another edit.
        var volume = new Entity("Haze volume") { Local = new Transform(new Vector3(0f, 0f, 1.5f)) };
        volume.Add(new AtmosphereVolumeComponent { Size = new Vector3(6f, 6f, 3f), CellSize = 0.5f });
        _session.Scene.Add(volume);
        var wall = new Entity("Far wall") { Local = new Transform(new Vector3(40f, 0f, 0f)) };
        wall.Add(new WallComponent { Kind = WallKind.Wall, Width = 2f, Height = 2f, Thickness = 0.2f });
        _session.Scene.Add(wall);

        _session.Update(1.0 / 60);
        Assert.NotNull(_session.Engine!.Simulation.Smoke);
        _session.Update(EditorSession.IssueRefreshDelay + 0.01);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!_session.Issues.Current.Any(i => i.Code == "simulation.obstacle-outside") && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            _session.Update(1.0 / 60);
        }

        Assert.Contains(_session.Issues.Current, i => i.Code == "simulation.obstacle-outside");
    }

    [Fact]
    public void Pausing_the_simulation_is_an_engine_setting()
    {
        Assert.False(_session.SimulationPaused);
        _session.SimulationPaused = true;
        Assert.True(_session.Engine!.Settings.SimulationPaused);
        _session.SimulationPaused = false;
        Assert.False(_session.Engine.Settings.SimulationPaused);
    }

    /// <summary>Everything that must survive a save and reopen: ids, names, parents, transforms, fixture data, walls.</summary>
    private static List<string> Snapshot(Scene scene)
    {
        var list = new List<string>();
        foreach (var e in scene.All())
        {
            var p = e.Local.Position;
            var r = e.Local.Rotation;
            string line = FormattableString.Invariant(
                $"{e.Id}|{e.Name}|{e.Parent?.Id}|{e.LayerId}|{e.Visible}|{e.Locked}|{p.X:F4},{p.Y:F4},{p.Z:F4}|{r.X:F4},{r.Y:F4},{r.Z:F4},{r.W:F4}");
            if (e.Get<FixtureComponent>() is { } f)
                line += FormattableString.Invariant($"|{f.GdtfSpec}|{f.GdtfMode}|{string.Join(";", f.Addresses)}|{f.FixtureId}");
            if (e.Get<WallComponent>() is { } w)
                line += FormattableString.Invariant($"|{w.Kind}|{w.Width:F3}|{w.Height:F3}|{w.MaterialId}");
            list.Add(line);
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

}
