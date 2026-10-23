using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Settings;

namespace Candela.App.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(TestLog.Create(), dir.File("settings.json"));
        var s = store.Load();
        Assert.Equal(1f, s.UiScale);
        Assert.Equal("en", s.Language);
        Assert.Equal("Candela", s.NavigationStyle);
        Assert.False(store.HasPendingSave);
    }

    [Fact]
    public void Save_and_load_round_trip_atomically()
    {
        using var dir = new TempDir();
        string path = dir.File("cfg/settings.json");
        var store = new SettingsStore(TestLog.Create(), path);
        store.Load();
        store.Settings.UiScale = 1.75f;
        store.Settings.Language = "ru";
        store.Settings.ArtNetUniverseOffset = 0;
        store.Settings.LibraryFolders.Add("/fixtures");
        Assert.True(store.SaveNow());
        Assert.Empty(Directory.GetFiles(dir.File("cfg"), "*.tmp"));

        var again = new SettingsStore(TestLog.Create(), path).Load();
        Assert.Equal(1.75f, again.UiScale);
        Assert.Equal("ru", again.Language);
        Assert.Equal(0, again.ArtNetUniverseOffset);
        Assert.Equal(new[] { "/fixtures" }, again.LibraryFolders);
    }

    [Fact]
    public void A_damaged_file_is_kept_aside_and_defaults_are_used()
    {
        using var dir = new TempDir();
        string path = dir.Write("settings.json", "{ \"UiScale\": 2, oops");
        var log = TestLog.Create();
        var s = new SettingsStore(log, path).Load();
        Assert.Equal(1f, s.UiScale);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.damaged-*"));
        Assert.Contains(TestLog.Entries(log), e => e.Source == "Settings" && e.Message.Contains("could not be read"));
    }

    [Fact]
    public void Saves_are_debounced()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var store = new SettingsStore(TestLog.Create(), path);
        store.Load();
        store.RequestSave(10.0);
        Assert.True(store.HasPendingSave);
        store.Update(10.5);
        Assert.False(File.Exists(path));
        store.RequestSave(10.6); // a later request postpones the save
        store.Update(11.0);
        Assert.False(File.Exists(path));
        store.Update(11.4);
        Assert.True(File.Exists(path));
        Assert.False(store.HasPendingSave);
    }

    [Fact]
    public void An_unwritable_location_is_reported_not_thrown()
    {
        using var dir = new TempDir();
        string blocker = dir.Write("file-not-folder", "x");
        var log = TestLog.Create();
        var store = new SettingsStore(log, Path.Combine(blocker, "settings.json"));
        store.Load();
        Assert.False(store.SaveNow());
        Assert.Contains(TestLog.Entries(log), e => e.Level == Diagnostics.LogLevel.Warning);
    }

    [Fact]
    public void Command_line_overrides_apply_to_one_run_only()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var first = new SettingsStore(TestLog.Create(), path);
        first.Load();
        first.Settings.UiScale = 1.25f;
        first.Settings.Language = "en";
        first.SaveNow();

        var run = new SettingsStore(TestLog.Create(), path);
        run.Load();
        run.ApplyRunOverrides(2f, "RU", "llvmpipe");
        Assert.Equal((2f, "ru", "llvmpipe"), (run.Settings.UiScale, run.Settings.Language, run.Settings.PreferredGpu));
        run.Settings.ZoomSpeed = 2f; // an unrelated change made during the run
        run.SaveNow();
        var stored = new SettingsStore(TestLog.Create(), path).Load();
        Assert.Equal((1.25f, "en", ""), (stored.UiScale, stored.Language, stored.PreferredGpu));
        Assert.Equal(2f, stored.ZoomSpeed);

        // A setting the user changes during the run is saved.
        run.Settings.UiScale = 1.5f;
        run.SaveNow();
        Assert.Equal(1.5f, new SettingsStore(TestLog.Create(), path).Load().UiScale);
    }

    [Fact]
    public void Read_only_stores_never_write()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var store = new SettingsStore(TestLog.Create(), path) { ReadOnly = true };
        store.Load();
        store.Settings.UiScale = 3f;
        Assert.True(store.SaveNow());
        store.RequestSave(0);
        store.Update(10);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Sanitize_clamps_and_repairs_every_value()
    {
        var s = new AppSettings
        {
            UiScale = 99f,
            FontSize = 2f,
            Theme = " ",
            Language = "RU ",
            LibraryFolders = new List<string> { "/a", "", "/a", " " },
            KeyBindingPreset = "",
            NetworkInterface = null!,
            ArtNetUniverseOffset = 7,
            AutosaveMinutes = -3,
            MaxRecentFiles = 2,
            RecentFiles = new List<string> { "x", "x", "y", "z" },
            NavigationStyle = "maya",
            ZoomSpeed = float.NaN,
            PreferredGpu = null!,
        };
        SettingsStore.Sanitize(s);
        Assert.Equal(UiScale.Max, s.UiScale);
        Assert.Equal(UiScale.MinFontSize, s.FontSize);
        Assert.Equal("Dark", s.Theme);
        Assert.Equal("ru", s.Language);
        Assert.Equal(new[] { "/a" }, s.LibraryFolders);
        Assert.Equal("Default", s.KeyBindingPreset);
        Assert.Equal("", s.NetworkInterface);
        Assert.Equal(1, s.ArtNetUniverseOffset);
        Assert.Equal(0, s.AutosaveMinutes);
        Assert.Equal(new[] { "x", "y" }, s.RecentFiles);
        Assert.Equal("Maya", s.NavigationStyle);
        Assert.Equal(1f, s.ZoomSpeed);
        Assert.Equal("", s.PreferredGpu);

        s.Language = "de";
        s.UiScale = float.NaN;
        SettingsStore.Sanitize(s);
        Assert.Equal("en", s.Language);
        Assert.Equal(UiScale.Min, s.UiScale);
    }
}

public sealed class UiScaleTests
{
    [Theory]
    [InlineData(1f, 1f, 1f)]
    [InlineData(1.5f, 2f, 3f)]
    [InlineData(0.2f, 1f, 1f)]
    [InlineData(10f, 1f, 3f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(1f, float.NaN, 1f)]
    [InlineData(2f, 8f, 8f)]
    public void Effective_scale_combines_user_scale_and_monitor_dpi(float user, float dpi, float expected) =>
        Assert.Equal(expected, UiScale.Effective(user, dpi), 4);

    [Fact]
    public void Presets_and_percent()
    {
        Assert.All(UiScale.Presets, p => Assert.InRange(p, UiScale.Min, UiScale.Max));
        Assert.Equal("150 %", UiScale.Percent(1.5f));
        Assert.Equal(UiScale.MaxFontSize, UiScale.ClampFontSize(100f));
        Assert.Equal(15f, UiScale.ClampFontSize(float.NaN));
    }
}
