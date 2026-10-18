using System.Text.RegularExpressions;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Rendering;

namespace Candela.App.Tests;

[Collection(UiStateCollection.Name)]
public sealed partial class LocalizationTests : IDisposable
{
    public void Dispose() => Loc.SetLanguage("en");

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string[] Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Every_key_has_exactly_one_entry_with_english_and_russian_text()
    {
        var keys = Enum.GetValues<Str>();
        var entries = Loc.Entries;
        Assert.Equal(keys.Length, entries.Count);
        Assert.Equal(keys.Length, entries.Select(e => e.Key).Distinct().Count());
        foreach (var (key, en, ru) in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} has no English text");
            Assert.False(string.IsNullOrWhiteSpace(ru), $"{key} has no Russian text");
        }
    }

    [Fact]
    public void Placeholders_match_between_languages_and_format()
    {
        foreach (var (key, en, ru) in Loc.Entries)
        {
            Assert.Equal(Placeholders(en), Placeholders(ru));
            var args = new object?[] { 1, 2, 3, 4 };
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, en, args);
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, ru, args);
            _ = key;
        }
    }

    [Theory]
    [InlineData(nameof(Str.MenuFile))]
    [InlineData(nameof(Str.MenuEdit))]
    [InlineData(nameof(Str.MenuView))]
    [InlineData(nameof(Str.MenuTools))]
    [InlineData(nameof(Str.MenuWindow))]
    [InlineData(nameof(Str.MenuHelp))]
    [InlineData(nameof(Str.FileNew))]
    [InlineData(nameof(Str.FileOpen))]
    [InlineData(nameof(Str.FileSaveAs))]
    [InlineData(nameof(Str.FileImport))]
    [InlineData(nameof(Str.FileSettings))]
    [InlineData(nameof(Str.EditUndo))]
    [InlineData(nameof(Str.ViewShading))]
    [InlineData(nameof(Str.WindowLayouts))]
    [InlineData(nameof(Str.HelpAbout))]
    [InlineData(nameof(Str.PanelViewport))]
    [InlineData(nameof(Str.PanelLibrary))]
    [InlineData(nameof(Str.PanelProperties))]
    [InlineData(nameof(Str.PanelIssues))]
    [InlineData(nameof(Str.PanelConsole))]
    [InlineData(nameof(Str.PanelRender))]
    [InlineData(nameof(Str.PanelPreview))]
    [InlineData(nameof(Str.PanelSettings))]
    [InlineData(nameof(Str.MsgNotAvailableTitle))]
    public void Menus_and_panel_titles_are_russian(string name)
    {
        var key = Enum.Parse<Str>(name);
        Loc.SetLanguage("ru");
        string text = Loc.Get(key);
        Assert.Contains(text, c => c is >= 'А' and <= 'я' or 'ё' or 'Ё');
        Assert.NotEqual(Loc.English_(key), text);
    }

    [Fact]
    public void Switching_language_raises_the_event_and_unknown_codes_mean_english()
    {
        int raised = 0;
        void Handler() => raised++;
        Loc.LanguageChanged += Handler;
        try
        {
            Loc.SetLanguage("RU");
            Assert.Equal("ru", Loc.Language);
            Assert.Equal("Файл", Loc.Get(Str.MenuFile));
            Loc.SetLanguage("ru"); // no change, no event
            Loc.SetLanguage("fr");
            Assert.Equal("en", Loc.Language);
            Assert.Equal("File", Loc.Get(Str.MenuFile));
            Assert.Equal(2, raised);
            Assert.Contains(Loc.Languages, l => l.Code == "ru");
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
        }
    }

    [Fact]
    public void Format_uses_invariant_numbers()
    {
        Assert.Equal("60 fps", Loc.Format(Str.ViewportFps, 59.7));
        Loc.SetLanguage("ru");
        Assert.Equal("Выделено: 3", Loc.Format(Str.StatusSelected, 3));
    }
}

public sealed class MenuScopeTests
{
    [Theory]
    [InlineData("MVR Scene…", "mvr scene")]
    [InlineData("Save As...", "save as")]
    [InlineData("  Open##file", "open")]
    [InlineData("  Four Viewports  ", "four viewports")]
    public void Normalize(string label, string expected) => Assert.Equal(expected, MenuScope.Normalize(label));

    [Fact]
    public void Scripts_match_by_label_key_or_english_text()
    {
        Assert.True(MenuScope.Matches("MVR Scene", "  Сцена MVR…", "FileImportMvr", "MVR Scene…"));
        Assert.True(MenuScope.Matches("сцена mvr", "  Сцена MVR…", "FileImportMvr", "MVR Scene…"));
        Assert.True(MenuScope.Matches("FileImportMvr", "  Сцена MVR…", "FileImportMvr", "MVR Scene…"));
        Assert.False(MenuScope.Matches("Model", "  Сцена MVR…", "FileImportMvr", "MVR Scene…"));
        Assert.True(MenuScope.Matches("Layout 1", "Layout 1", null, null));
    }
}

[Collection(UiStateCollection.Name)]
public sealed class EditorSessionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;
    private readonly EditorSession _session;

    public EditorSessionTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
        _session = new EditorSession(_log, _settings);
    }

    public void Dispose()
    {
        _session.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void A_new_session_is_an_empty_unmodified_scene()
    {
        Assert.Equal(0, _session.Scene.Count);
        Assert.False(_session.IsModified);
        Assert.False(_session.EngineAvailable);
        Assert.Equal("Untitled", _session.DisplayName);
        Assert.Null(_session.Content);
        _session.Scene.Add(new Entity("x"));
        Assert.True(_session.IsModified);
    }

    [Fact]
    public void Engine_operations_say_clearly_that_they_are_not_available_and_change_nothing()
    {
        string existing = _dir.Write("show.mvr", "x");
        var scene = _session.Scene;
        long version = scene.Version;
        var results = new[]
        {
            _session.New(),
            _session.Open(existing),
            _session.SaveAs(_dir.File("out.candela")),
            _session.Import(existing, ImportKind.Mvr),
            _session.Import(existing, ImportKind.Model),
            _session.Export(_dir.File("out.mvr"), ExportKind.Mvr),
            _session.StartFinalRender(new FinalRenderSettings()),
        };
        foreach (var r in results)
        {
            Assert.Equal(SessionOutcome.NotAvailable, r.Outcome);
            Assert.False(r.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
        }

        Assert.Contains("needs the stage engine", results[0].Message);
        Assert.StartsWith("New ", results[0].Message);
        Assert.Contains("MVR Scene", results[3].Message);
        Assert.Same(scene, _session.Scene);
        Assert.Equal(version, scene.Version);
        Assert.False(File.Exists(_dir.File("out.candela")));
        Assert.Contains(_session.Issues.Current, i => i.Code == "app.engine-not-available" && i.Severity == IssueSeverity.Note);
    }

    [Fact]
    public void Missing_files_and_unsaved_scenes_fail_with_a_reason()
    {
        Assert.Equal(SessionOutcome.Failed, _session.Open(_dir.File("missing.mvr")).Outcome);
        Assert.Equal(SessionOutcome.Failed, _session.Import(_dir.File("missing.glb"), ImportKind.Model).Outcome);
        var save = _session.Save();
        Assert.Equal(SessionOutcome.Failed, save.Outcome);
        Assert.Contains("Save As", save.Message);
        _session.MarkSaved(_dir.File("show.candela"));
        Assert.Equal(SessionOutcome.NotAvailable, _session.Save().Outcome);
        Assert.Equal("show", _session.DisplayName);
    }

    [Fact]
    public void Russian_messages()
    {
        Loc.SetLanguage("ru");
        try
        {
            var r = _session.New();
            Assert.Contains("движок", r.Message);
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
        var issue = Assert.Single(_session.Issues.Current);
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
    public void Replacing_the_scene_resets_selection_and_history()
    {
        var a = _session.Scene.Add(new Entity("a"));
        _session.Selection.Set(a.Id);
        int replaced = 0;
        _session.SceneReplaced += () => replaced++;
        var scene = new Scene("other");
        _session.ReplaceScene(scene, _dir.File("other.candela"));
        Assert.Same(scene, _session.Scene);
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
    }

    [Fact]
    public void Disposed_sessions_refuse_updates()
    {
        _session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _session.Update(0.1));
    }

    [Fact]
    public void Preview_stage_entities_are_named_and_unique()
    {
        var entities = PreviewStage.CreateEntities();
        Assert.Equal(8, entities.Count);
        Assert.Equal(entities.Count, entities.Select(e => e.Id).Distinct().Count());
        Assert.Equal(entities.Count, entities.Select(e => e.Name).Distinct().Count());
        Assert.Contains(entities, e => e.Name == "Gold sphere");
        Assert.All(entities, e => Assert.NotNull(e.Get<Candela.Core.SceneGraph.Components.MeshComponent>()));
    }
}

public sealed class FrameTimerAndLogTests
{
    [Fact]
    public void Frame_timer_clamps_and_averages()
    {
        var timer = new FrameTimer();
        for (int i = 0; i < 100; i++)
            timer.Record(1.0 / 50.0);
        Assert.Equal(50.0, timer.FramesPerSecond, 3);
        Assert.Equal(20.0, timer.FrameMilliseconds, 3);
        timer.Record(double.NaN);
        timer.Record(-1.0);
        Assert.Equal(50.0, timer.FramesPerSecond, 3);
        timer.Tick();
        timer.Tick();
        Assert.Equal(2, timer.FrameCount);
        Assert.InRange(timer.DeltaTime, 1e-4, FrameTimer.MaxDeltaTime); // stalls never give a huge simulation step
    }

    [Fact]
    public void Log_is_a_bounded_ring_and_mirrors_to_a_file()
    {
        using var dir = new TempDir();
        using var log = new AppLog(capacity: 16) { ConsoleLevel = LogLevel.Error + 1 };
        log.OpenFile(dir.File("logs/app.log"));
        for (int i = 0; i < 30; i++)
            log.Info("Test", "message " + i);
        log.Error("Test", "failed", new InvalidOperationException("inner detail"));
        var entries = TestLog.Entries(log);
        Assert.Equal(16, log.Count);
        Assert.Equal("message 15", entries[0].Message);
        Assert.Equal(LogLevel.Error, entries[^1].Level);
        log.Dispose();
        string text = File.ReadAllText(dir.File("logs/app.log"));
        Assert.Contains("message 0", text);
        Assert.Contains("inner detail", text);
    }
}
