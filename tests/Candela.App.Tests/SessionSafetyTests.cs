using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.SceneGraph;

namespace Candela.App.Tests;

/// <summary>
/// Data safety of the editor session (review of phase B1): autosave files survive what is not a clean exit and never
/// outlive a clean one, the scene is not marked modified by the engine's own frames, and a failed save changes nothing.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class SessionSafetyTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public SessionSafetyTests()
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
    public void The_engine_frames_of_an_opened_rig_do_not_mark_it_modified()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);
        for (int i = 0; i < 40; i++)
            session.Update(1.0 / 30.0);
        Assert.False(session.IsModified);
    }

    [Fact]
    public async Task Changing_the_autosave_interval_leaves_no_recovery_files_after_a_clean_exit()
    {
        _settings.Settings.AutosaveMinutes = 5;
        using (var session = EngineTestSupport.CreateSession(_log, _settings, _dir, autosave: true))
        {
            session.Update(0.01);
            Assert.NotNull(session.Engine!.Autosave);
            session.Scene.Add(new Entity("work"));
            await session.Engine.Autosave!.SaveNowAsync().WaitAsync(TimeSpan.FromSeconds(30));

            // The user changes the interval, then turns autosave off and on again during the run.
            _settings.Settings.AutosaveMinutes = 10;
            session.Update(0.01);
            _settings.Settings.AutosaveMinutes = 0;
            session.Update(0.01);
            _settings.Settings.AutosaveMinutes = 2;
            session.Update(0.01);
            Assert.Equal(TimeSpan.FromMinutes(2), session.Engine.Autosave!.Interval);
            Assert.True(session.Engine.Autosave.Enabled);
            session.Scene.Add(new Entity("more work"));
            await session.Engine.Autosave.SaveNowAsync().WaitAsync(TimeSpan.FromSeconds(30));
            session.EndCleanly();
        }

        using var next = EngineTestSupport.CreateSession(_log, _settings, _dir);
        Assert.Empty(next.FindRecoverable());
    }

    [Fact]
    public async Task Turning_autosave_off_stops_autosaving_and_a_save_still_removes_the_recovery_files()
    {
        _settings.Settings.AutosaveMinutes = 5;
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir, autosave: true);
        session.Update(0.01);
        session.Scene.Add(new Entity("work"));
        await session.Engine!.Autosave!.SaveNowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        _settings.Settings.AutosaveMinutes = 0;
        session.Update(0.01);
        Assert.False(session.Engine.Autosave?.Enabled ?? false);

        // The crash data of the running session stays (another instance must not offer it: it is live).
        Assert.True(session.SaveAs(_dir.File("show.candela")).Succeeded);
        await (session.Engine.Autosave?.FlushAsync() ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));
        session.EndCleanly();
        session.Dispose();
        using var next = EngineTestSupport.CreateSession(_log, _settings, _dir);
        Assert.Empty(next.FindRecoverable());
    }

    [Fact]
    public async Task A_session_that_does_not_end_cleanly_keeps_its_recovery_files()
    {
        _settings.Settings.AutosaveMinutes = 5;
        var session = EngineTestSupport.CreateSession(_log, _settings, _dir, autosave: true);
        session.Update(0.01);
        session.Scene.Add(new Entity("unsaved work"));
        await session.Engine!.Autosave!.SaveNowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        // The frame loop gave up (or an exception ended the program): disposed without EndCleanly.
        session.Dispose();

        using var next = EngineTestSupport.CreateSession(_log, _settings, _dir);
        var found = Assert.Single(next.FindRecoverable());
        Assert.True(next.Recover(found).Succeeded);
        Assert.Contains(next.Scene.All(), e => e.Name == "unsaved work");
    }

    [Fact]
    public void Steady_frames_of_an_opened_rig_with_autosave_on_do_not_allocate()
    {
        _settings.Settings.AutosaveMinutes = 5;
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir, autosave: true);
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);
        session.Selection.Set(session.Scene.Roots[0].Id);
        for (int i = 0; i < 60; i++)
            session.Update(1.0 / 30.0);
        _ = session.DisplayName;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++)
        {
            session.Update(1.0 / 30.0);
            _ = session.DisplayName;
            _ = session.IsModified;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024, $"{allocated} bytes in 60 frames");
    }

    [Fact]
    public void A_failed_save_as_keeps_the_scene_name()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        session.Scene.Add(new Entity("x"));
        string name = session.Scene.Name;
        // A folder of that name is in the way: the project cannot be written.
        Directory.CreateDirectory(_dir.File("blocked.candela"));
        var result = session.SaveAs(_dir.File("blocked.candela"));
        Assert.False(result.Succeeded);
        Assert.Equal(name, session.Scene.Name);
        Assert.Null(session.FilePath);
        Assert.True(session.IsModified);
    }
}

/// <summary>
/// The session questions never take the key press that opened them as their answer: Enter that confirmed a file in the
/// file browser (File → Open with unsaved changes) must not also answer "Save" in the same frame.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class SessionDialogKeyTests
{
    [Theory]
    [InlineData(Hexa.NET.ImGui.ImGuiKey.Enter)]
    [InlineData(Hexa.NET.ImGui.ImGuiKey.Escape)]
    public void A_key_pressed_in_the_frame_a_question_opens_does_not_answer_it(Hexa.NET.ImGui.ImGuiKey key)
    {
        Loc.SetLanguage("en");
        using var ui = new HeadlessImGui(new System.Numerics.Vector2(1280, 800));
        var dialogs = new Candela.App.Windows.SessionDialogs();
        var answers = new List<Candela.App.Windows.UnsavedChoice>();
        var io = Hexa.NET.ImGui.ImGui.GetIO();

        // The key goes down in the frame in which the question is asked (by another dialog's handler).
        io.AddKeyEvent(key, true);
        ui.Frame(() =>
        {
            dialogs.AskUnsaved("Show", answers.Add);
            dialogs.Draw(1f, 1f / 60f);
        });
        io.AddKeyEvent(key, false);
        ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
        Assert.Empty(answers);
        Assert.True(dialogs.IsOpen);

        // A new press while the question is showing answers it.
        io.AddKeyEvent(key, true);
        ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
        io.AddKeyEvent(key, false);
        ui.Frame(() => dialogs.Draw(1f, 1f / 60f));
        var expected = key == Hexa.NET.ImGui.ImGuiKey.Enter ? Candela.App.Windows.UnsavedChoice.Save : Candela.App.Windows.UnsavedChoice.Cancel;
        Assert.Equal(new[] { expected }, answers);
    }
}
