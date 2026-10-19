using Candela.App.Diagnostics;
using Candela.App.Editing;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.App.Windows;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Project;
using Hexa.NET.ImGui;
using System.Numerics;

namespace Candela.App.Tests;

/// <summary>The session dialogs and the start-up recovery of a crashed session.</summary>
[Collection(UiStateCollection.Name)]
public sealed class SessionFlowTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public SessionFlowTests()
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
    public void The_unsaved_changes_question_is_drawn_and_answered_once()
    {
        using var ui = new HeadlessImGui(new Vector2(1280, 800));
        var dialogs = new SessionDialogs();
        var answers = new List<UnsavedChoice>();
        dialogs.AskUnsaved("Show", answers.Add);
        Assert.True(dialogs.IsOpen);
        Assert.Equal(SessionDialogKind.Unsaved, dialogs.Kind);
        ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
        ui.Frame(() =>
        {
            dialogs.Draw(1f, 1f / 60f);
            Assert.True(ImGui.IsPopupOpen("###session-dialog"));
        });
        dialogs.Answer(UnsavedChoice.Discard);
        dialogs.Answer(UnsavedChoice.Save); // closed: ignored
        Assert.Equal(new[] { UnsavedChoice.Discard }, answers);
        Assert.False(dialogs.IsOpen);
        ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
    }

    [Fact]
    public void The_busy_overlay_shows_and_closes()
    {
        using var ui = new HeadlessImGui(new Vector2(1280, 800));
        var dialogs = new SessionDialogs();
        dialogs.ShowBusy("Opening Фокина.mvr…");
        Assert.Equal("Opening Фокина.mvr…", dialogs.BusyText);
        ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
        ui.Frame(() =>
        {
            dialogs.Draw(1f, 1f / 60f);
            Assert.True(ImGui.IsPopupOpen("###session-busy"));
        });
        dialogs.HideBusy();
        Assert.True(dialogs.IsOpen); // until the popup was closed in a frame
        ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
        Assert.False(dialogs.IsOpen);
        ui.Frame(() => Assert.False(ImGui.IsPopupOpen("###session-busy")));
    }

    [Fact]
    public void The_open_or_import_question()
    {
        using var ui = new HeadlessImGui(new Vector2(1280, 800));
        var dialogs = new SessionDialogs();
        DropChoice? choice = null;
        dialogs.AskOpenOrImport("show.mvr", c => choice = c);
        ui.Frame(() => dialogs.Draw(1.5f, 1f / 60f));
        dialogs.Answer(DropChoice.Import);
        Assert.Equal(DropChoice.Import, choice);
    }

    [Fact]
    public void A_crashed_session_is_offered_at_start_and_recovered()
    {
        string autosave = _dir.File("autosave");
        var crashed = new StageEngine(new StageEngineOptions { Library = EngineTestSupport.Library, CacheDirectory = _dir.File("cache-a") });
        crashed.New("Rehearsal");
        crashed.EnableAutosave(new AutosaveOptions { Directory = autosave, Interval = TimeSpan.FromHours(1) });
        crashed.Scene.Add(new Entity("work in progress"));
        crashed.Autosave!.SaveNowAsync().Wait(TimeSpan.FromSeconds(30));
        crashed.Dispose(); // no clean exit: the recovery files stay

        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        var sessions = session.FindRecoverable();
        var found = Assert.Single(sessions);
        Assert.Equal("Rehearsal", found.SceneName);

        var dialogs = new SessionDialogs();
        SessionResult? recovered = null;
        dialogs.OfferRecovery(sessions, s => recovered = session.Recover(s), s => session.Discard(s));
        Assert.Equal(SessionDialogKind.Recovery, dialogs.Kind);
        using (var ui = new HeadlessImGui(new Vector2(1280, 800)))
            ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
        dialogs.Recover(0);
        Assert.True(recovered?.Succeeded, recovered?.Message);
        Assert.Contains(session.Scene.All(), e => e.Name == "work in progress");
        Assert.True(session.IsModified);
        Assert.Equal("Rehearsal", session.DisplayName);

        // Saved, the session is not offered again.
        Assert.True(session.SaveAs(_dir.File("rehearsal.candela")).Succeeded);
        Assert.Empty(session.FindRecoverable());
    }

    [Fact]
    public void A_discarded_crash_is_deleted()
    {
        string autosave = _dir.File("autosave");
        var crashed = new StageEngine(new StageEngineOptions { Library = EngineTestSupport.Library, CacheDirectory = _dir.File("cache-b") });
        crashed.EnableAutosave(new AutosaveOptions { Directory = autosave, Interval = TimeSpan.FromHours(1) });
        crashed.Scene.Add(new Entity("lost"));
        crashed.Autosave!.SaveNowAsync().Wait(TimeSpan.FromSeconds(30));
        crashed.Dispose();
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        var dialogs = new SessionDialogs();
        int deleted = 0;
        dialogs.OfferRecovery(session.FindRecoverable(), _ => { }, s =>
        {
            Assert.True(session.Discard(s).Succeeded);
            deleted++;
        });
        dialogs.Delete(0);
        Assert.Equal(1, deleted);
        Assert.False(dialogs.IsOpen);
        Assert.Empty(session.FindRecoverable());
    }

    [Fact]
    public void Dropped_and_imported_files_are_classified_by_extension()
    {
        Assert.True(Workspace.IsScene("a/show.MVR"));
        Assert.True(Workspace.IsScene("show.candela"));
        Assert.False(Workspace.IsScene("chair.obj"));
        Assert.True(Workspace.CanImport("chair.obj"));
        Assert.True(Workspace.CanImport("plan.jpg"));
        Assert.True(Workspace.CanImport("Robe@Robin.gdtf"));
        Assert.False(Workspace.CanImport("notes.txt"));
        Assert.Equal(ImportKind.Gdtf, Workspace.KindOf("x.gdtf"));
        Assert.Equal(ImportKind.Image, Workspace.KindOf("x.PNG"));
        var hit = SurfaceHit.Ground(new Vector3(2, 3, 0));
        Assert.Equal(new Vector3(2, 3, 0), Workspace.PlacementFor(ImportKind.Model, hit).Position);
        Assert.Equal(Core.Math.Transform.Identity, Workspace.PlacementFor(ImportKind.Mvr, hit));
        Assert.Equal(Core.Math.Transform.Identity, Workspace.PlacementFor(ImportKind.Model, null));
    }

    [Fact]
    public void Duplicating_a_truss_with_its_fixture_keeps_the_fixture_on_the_copy()
    {
        var scene = new Scene();
        var selection = new Selection();
        var undo = new UndoStack();
        var truss = scene.Add(new Entity("truss"));
        truss.Add(new TrussComponent());
        var fixture = scene.Add(new Entity("spot"), truss);
        fixture.Add(new FixtureComponent { MountedOn = truss.Id });
        var loose = scene.Add(new Entity("loose spot"));
        loose.Add(new FixtureComponent { MountedOn = truss.Id });

        selection.Set(new[] { truss.Id, loose.Id });
        var copies = EditOperations.Duplicate(scene, selection, undo);
        Assert.Equal(2, copies.Count);
        var trussCopy = scene.Find(copies[0])!;
        var fixtureCopy = trussCopy.Children.Single();
        Assert.Equal(trussCopy.Id, fixtureCopy.Get<FixtureComponent>()!.MountedOn); // remapped to the copy
        Assert.Equal(trussCopy.Id, scene.Find(copies[1])!.Get<FixtureComponent>()!.MountedOn); // copied together: the copy
        Assert.Equal("Duplicate 2 objects", undo.UndoDescription);
        selection.Set(loose.Id);
        var alone = Assert.Single(EditOperations.Duplicate(scene, selection, undo));
        Assert.Equal(truss.Id, scene.Find(alone)!.Get<FixtureComponent>()!.MountedOn); // outside reference kept

        // Copied, then pasted into another scene: the mount on a truss that scene does not have is cleared.
        var ops = new EditOperations();
        selection.Set(loose.Id);
        ops.Copy(scene, selection);
        var other = new Scene();
        var pasted = ops.Paste(other, new Selection(), new UndoStack());
        Assert.Null(other.Find(Assert.Single(pasted))!.Get<FixtureComponent>()!.MountedOn);
        // …and kept when pasted back into this scene.
        var back = ops.Paste(scene, selection, undo);
        Assert.Equal(truss.Id, scene.Find(Assert.Single(back))!.Get<FixtureComponent>()!.MountedOn);
    }
}
