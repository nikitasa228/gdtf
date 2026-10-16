using Candela.Core.Presets;
using Candela.Core.Settings;

namespace Candela.Core.Tests;

public class PresetAndSettingsTests
{
    private sealed class Gradient
    {
        public float Min { get; set; }
        public float Max { get; set; } = 1000f;
    }

    [Fact]
    public void Editing_switches_to_custom_and_save_as_names_it()
    {
        var lib = new PresetLibrary<Gradient>("lux-gradient", () => new Gradient(), new MemoryPresetStore());
        Assert.Equal("Default", lib.CurrentName);
        Assert.Equal(new[] { "Default" }, lib.Names);

        lib.Current.Max = 500;
        lib.MarkModified();
        Assert.True(lib.IsCustom);
        Assert.Equal(new[] { "Default", "Custom" }, lib.Names);

        lib.SaveAs("Theatre");
        Assert.Equal("Theatre", lib.CurrentName);
        Assert.Equal(new[] { "Default", "Theatre" }, lib.Names);

        lib.Select("Default");
        Assert.Equal(1000f, lib.Current.Max);
        lib.Select("Theatre");
        Assert.Equal(500f, lib.Current.Max);
        Assert.Throws<ArgumentException>(() => lib.SaveAs("Default"));
    }

    [Fact]
    public void File_store_round_trips_names_with_special_characters()
    {
        var dir = Path.Combine(Path.GetTempPath(), "candela-presets-" + Guid.NewGuid());
        try
        {
            var store = new FilePresetStore(dir);
            var lib = new PresetLibrary<Gradient>("noise", () => new Gradient(), store);
            lib.Current.Min = 3;
            lib.SaveAs("Клуб: 50/100%");
            var lib2 = new PresetLibrary<Gradient>("noise", () => new Gradient(), new FilePresetStore(dir));
            Assert.Contains("Клуб: 50/100%", lib2.Names);
            lib2.Select("Клуб: 50/100%");
            Assert.Equal(3f, lib2.Current.Min);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Key_chords_parse_and_default_bindings_have_no_conflicts()
    {
        var chord = KeyChord.Parse("Ctrl+Shift+S");
        Assert.Equal("S", chord.Key);
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift, chord.Modifiers);
        Assert.Equal("Ctrl+Shift+S", chord.ToString());

        var set = KeyBindingSet.CreateDefault();
        var all = set.Bindings.SelectMany(kv => kv.Value.Select(KeyChord.Parse)).ToList();
        var duplicates = all.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
        Assert.Empty(duplicates);
        Assert.Contains(EditorActions.Undo, set.ActionsFor(KeyChord.Parse("Ctrl+Z")));
    }

    [Fact]
    public void Settings_survive_a_damaged_file()
    {
        var f = Path.Combine(Path.GetTempPath(), "candela-settings-" + Guid.NewGuid() + ".json");
        File.WriteAllText(f, "{ not json");
        try
        {
            var s = AppSettings.Load(f);
            Assert.Equal(1f, s.UiScale);
            s.UiScale = 1.5f;
            s.AddRecentFile("/a.candela");
            s.Save(f);
            Assert.Equal(1.5f, AppSettings.Load(f).UiScale);
        }
        finally
        {
            File.Delete(f);
        }
    }
}
