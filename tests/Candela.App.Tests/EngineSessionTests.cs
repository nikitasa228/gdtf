using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
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
