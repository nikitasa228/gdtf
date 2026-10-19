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
    public void Icon_labels_are_built_once_per_language()
    {
        string first = Labels.With(Icons.Search, Str.Search, "##search");
        Assert.Same(first, Labels.With(Icons.Search, Str.Search, "##search"));
        Assert.EndsWith("##search", first);
        Assert.StartsWith(Icons.Search, first);
        Assert.Equal(Loc.Get(Str.MenuFile), Labels.With("", Str.MenuFile));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            _ = Labels.With(Icons.Search, Str.Search, "##search");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Loc.SetLanguage("ru");
        Assert.Contains("Поиск", Labels.With(Icons.Search, Str.Search, "##search"));
    }

    [Fact]
    public void Every_core_issue_category_is_translated()
    {
        var categories = typeof(IssueCategory).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(categories);
        foreach (var category in categories)
            Assert.Equal(category, Candela.App.Panels.IssuesPanel.CategoryLabel(category));
        Loc.SetLanguage("ru");
        foreach (var category in categories)
            Assert.NotEqual(category, Candela.App.Panels.IssuesPanel.CategoryLabel(category));
        Assert.Equal("Программа", Candela.App.Panels.IssuesPanel.CategoryLabel(IssueCategory.Program));
        Assert.Equal("Custom", Candela.App.Panels.IssuesPanel.CategoryLabel("Custom")); // unknown categories as they are
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
