using System.Numerics;
using Candela.App.Input;
using Candela.App.Ui;
using Candela.Core.Presets;
using Candela.Core.Settings;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

public sealed class KeyBindingMapTests
{
    [Fact]
    public void Default_bindings_are_valid_and_free_of_conflicts()
    {
        var set = KeyBindingsService.CreateDefault();
        var map = new KeyBindingMap(set);
        Assert.Equal(0, map.InvalidCount);
        Assert.Empty(map.Conflicts());
        // Core's defaults and the editor's own actions are both there.
        Assert.Equal(EditorActions.Undo, map.Resolve(new KeyChord("Z", KeyModifiers.Ctrl)));
        Assert.Equal(AppActions.Cut, map.Resolve(new KeyChord("X", KeyModifiers.Ctrl)));
        Assert.Equal(EditorActions.Redo, map.Resolve(new KeyChord("Z", KeyModifiers.Ctrl | KeyModifiers.Shift)));
        Assert.Equal(EditorActions.ViewTop, map.Resolve(new KeyChord("Numpad7")));
    }

    [Fact]
    public void Every_default_chord_names_a_bindable_key()
    {
        foreach (var (action, chords) in KeyBindingsService.CreateDefault().Bindings)
        {
            foreach (var text in chords)
                Assert.True(KeyNames.ToImGui(KeyChord.Parse(text).Key) != ImGuiKey.None, $"{action}: {text}");
        }
    }

    [Fact]
    public void Key_names_are_case_insensitive_and_canonical()
    {
        var set = new KeyBindingSet();
        set.Bindings["a"] = new List<string> { "ctrl+k" };
        var map = new KeyBindingMap(set);
        Assert.Equal("a", map.Resolve(new KeyChord("K", KeyModifiers.Ctrl)));
        Assert.Equal("a", map.Resolve(new KeyChord("k", KeyModifiers.Ctrl)));
        Assert.Null(map.Resolve(new KeyChord("K")));
        Assert.Null(map.Resolve(new KeyChord("K", KeyModifiers.Ctrl | KeyModifiers.Shift)));
    }

    [Fact]
    public void Conflicts_are_reported_and_the_first_action_wins()
    {
        var set = new KeyBindingSet();
        set.Set("first", new KeyChord("G"));
        set.Set("second", new KeyChord("G"), new KeyChord("H"));
        var map = new KeyBindingMap(set);
        var conflict = Assert.Single(map.Conflicts());
        Assert.Equal(new KeyChord("G"), conflict.Chord);
        Assert.Equal(new[] { "first", "second" }, conflict.Actions);
        Assert.Equal("first", map.Resolve(new KeyChord("G")));
        Assert.Equal(new[] { "second" }, map.ConflictsOf("first", new KeyChord("G")));
        Assert.Empty(map.ConflictsOf("second", new KeyChord("H")));
    }

    [Fact]
    public void Malformed_chords_are_skipped_and_counted()
    {
        var set = new KeyBindingSet();
        set.Bindings["broken"] = new List<string> { "Ctrl+", "Ctrl+NoSuchKey", "F5" };
        var map = new KeyBindingMap(set);
        Assert.Equal(2, map.InvalidCount);
        Assert.Equal("broken", map.Resolve(new KeyChord("F5")));
    }

    [Fact]
    public void Key_name_mapping_round_trips()
    {
        foreach (var key in KeyNames.BindableKeys)
        {
            var name = KeyNames.FromImGui(key);
            Assert.NotNull(name);
            Assert.Equal(key, KeyNames.ToImGui(name));
            Assert.False(KeyNames.IsModifier(key));
        }

        Assert.Equal(ImGuiKey.None, KeyNames.ToImGui("NoSuchKey"));
        Assert.Equal(ImGuiKey.None, KeyNames.ToImGui(null));
        Assert.Equal(ImGuiKey.S | ImGuiKey.ModCtrl | ImGuiKey.ModShift,
            KeyNames.ToImGuiChord(new KeyChord("S", KeyModifiers.Ctrl | KeyModifiers.Shift)));
    }
}

public sealed class KeyDisplayTests
{
    [Theory]
    [InlineData("Ctrl+Comma", "Ctrl+,")]
    [InlineData("Ctrl+Shift+S", "Ctrl+Shift+S")]
    [InlineData("Numpad7", "Num 7")]
    [InlineData("Ctrl+Numpad1", "Ctrl+Num 1")]
    [InlineData("Alt+0", "Alt+0")]
    [InlineData("F12", "F12")]
    [InlineData("Delete", "Delete")]
    [InlineData("Ctrl+NumpadAdd", "Ctrl+Num +")]
    [InlineData("Shift+Slash", "Shift+/")]
    [InlineData("PageDown", "Page Down")]
    [InlineData("Ctrl+Backslash", "Ctrl+\\")]
    public void Chords_show_symbols_and_short_keypad_names(string chord, string expected) =>
        Assert.Equal(expected, KeyNames.DisplayChord(chord));

    [Fact]
    public void Every_default_chord_has_a_readable_display_form()
    {
        foreach (var (_, chords) in KeyBindingsService.CreateDefault().Bindings)
        {
            foreach (var chord in chords)
            {
                string display = KeyNames.DisplayChord(chord);
                Assert.DoesNotContain("Comma", display);
                Assert.DoesNotContain("Numpad", display);
                Assert.Same(display, KeyNames.DisplayChord(chord)); // cached: menus ask every frame
            }
        }
    }
}

public sealed class KeyBindingsServiceTests
{
    private static KeyBindingsService Create(AppSettings settings, IPresetStore store, string customFile) =>
        new(settings, store, customFile, TestLog.Create());

    [Fact]
    public void Editing_a_chord_switches_to_custom_and_survives_a_restart()
    {
        using var dir = new TempDir();
        var settings = new AppSettings();
        var store = new FilePresetStore(dir.File("presets"));
        string custom = dir.File("keybindings.custom.json");
        var service = Create(settings, store, custom);
        Assert.Equal(PresetLibrary<KeyBindingSet>.DefaultName, service.Library.CurrentName);
        int changes = 0;
        service.Changed += () => changes++;

        service.SetChord(EditorActions.Undo, 0, new KeyChord("U", KeyModifiers.Ctrl));
        Assert.True(service.Library.IsCustom);
        Assert.Equal("Custom", settings.KeyBindingPreset);
        Assert.Equal(1, changes);
        Assert.True(File.Exists(custom));

        var restarted = Create(settings, store, custom);
        Assert.True(restarted.Library.IsCustom);
        Assert.Equal(new[] { "Ctrl+U" }, restarted.Current.Bindings[EditorActions.Undo]);
    }

    [Fact]
    public void Named_presets_are_saved_selected_and_deleted()
    {
        using var dir = new TempDir();
        var settings = new AppSettings();
        var store = new FilePresetStore(dir.File("presets"));
        var service = Create(settings, store, dir.File("custom.json"));
        service.SetChord("my.action", 0, new KeyChord("F7"));
        service.SaveAs("Studio");
        Assert.Equal("Studio", service.Library.CurrentName);
        Assert.Equal("Studio", settings.KeyBindingPreset);
        Assert.Contains("Studio", service.Library.Names);
        Assert.True(Directory.EnumerateFiles(dir.File("presets"), "*", SearchOption.AllDirectories).Any());

        service.ResetToDefault();
        Assert.False(service.Current.Bindings.ContainsKey("my.action"));
        service.Select("Studio");
        Assert.Equal(new[] { "F7" }, service.Current.Bindings["my.action"]);

        // A restart picks the named preset again.
        var restarted = Create(settings, store, dir.File("custom.json"));
        Assert.Equal("Studio", restarted.Library.CurrentName);
        Assert.True(restarted.Current.Bindings.ContainsKey("my.action"));

        service.Delete("Studio");
        Assert.DoesNotContain("Studio", service.Library.Names);
    }

    [Fact]
    public void Reserved_names_are_rejected()
    {
        var service = Create(new AppSettings(), new MemoryPresetStore(), Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => service.SaveAs("Default"));
        Assert.Throws<ArgumentException>(() => service.SaveAs("  "));
    }

    [Fact]
    public void A_missing_or_damaged_preset_falls_back_to_the_defaults()
    {
        using var dir = new TempDir();
        var missing = Create(new AppSettings { KeyBindingPreset = "Gone" }, new MemoryPresetStore(), dir.File("custom.json"));
        Assert.Equal(PresetLibrary<KeyBindingSet>.DefaultName, missing.Library.CurrentName);

        System.IO.File.WriteAllText(dir.File("custom.json"), "{ this is not json");
        var damaged = Create(new AppSettings { KeyBindingPreset = "Custom" }, new MemoryPresetStore(), dir.File("custom.json"));
        Assert.Equal(PresetLibrary<KeyBindingSet>.DefaultName, damaged.Library.CurrentName);
        Assert.Equal(0, new KeyBindingMap(damaged.Current).InvalidCount);
    }

    [Fact]
    public void Removing_and_appending_chords()
    {
        using var dir = new TempDir();
        var service = Create(new AppSettings(), new MemoryPresetStore(), dir.File("custom.json"));
        service.SetChord(EditorActions.Redo, 5, new KeyChord("R", KeyModifiers.Ctrl)); // index past the end appends
        Assert.Equal(3, service.Current.Bindings[EditorActions.Redo].Count);
        service.RemoveChord(EditorActions.Redo, 0);
        Assert.Equal(new[] { "Ctrl+Shift+Z", "Ctrl+R" }, service.Current.Bindings[EditorActions.Redo]);
        service.RemoveChord(EditorActions.Redo, 17); // ignored
        Assert.Equal(2, service.Current.Bindings[EditorActions.Redo].Count);
    }
}

[Collection(UiStateCollection.Name)]
public sealed class KeyBindingDispatchTests
{
    private static (CommandRegistry Commands, List<string> Ran) Registry(params string[] ids)
    {
        var commands = new CommandRegistry();
        var ran = new List<string>();
        foreach (var id in ids)
            commands.Register(new EditorCommand(id, Str.EditUndo, "", () => ran.Add(id)));
        return (commands, ran);
    }

    private static void Press(HeadlessImGui ui, KeyBindingDispatcher dispatcher, ImGuiKey key, KeyModifiers mods = KeyModifiers.None,
        Action? draw = null)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, (mods & KeyModifiers.Ctrl) != 0);
        io.AddKeyEvent(ImGuiKey.ModShift, (mods & KeyModifiers.Shift) != 0);
        io.AddKeyEvent(ImGuiKey.ModAlt, (mods & KeyModifiers.Alt) != 0);
        io.AddKeyEvent(key, true);
        ui.Frame(() =>
        {
            draw?.Invoke();
            dispatcher.Dispatch();
        });
        io.AddKeyEvent(key, false);
        io.AddKeyEvent(ImGuiKey.ModCtrl, false);
        io.AddKeyEvent(ImGuiKey.ModShift, false);
        io.AddKeyEvent(ImGuiKey.ModAlt, false);
        ui.Frame(() =>
        {
            draw?.Invoke();
            dispatcher.Dispatch();
        });
    }

    [Fact]
    public void Pressed_chords_run_their_commands_once()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        ui.Frame(() => { });
        var (commands, ran) = Registry(EditorActions.Undo, EditorActions.Redo, EditorActions.FrameSelection);
        var dispatcher = new KeyBindingDispatcher(commands, KeyBindingsService.CreateDefault());

        Press(ui, dispatcher, ImGuiKey.Z, KeyModifiers.Ctrl);
        Assert.Equal(new[] { EditorActions.Undo }, ran);
        Assert.Equal(EditorActions.Undo, dispatcher.LastAction);

        Press(ui, dispatcher, ImGuiKey.Z, KeyModifiers.Ctrl | KeyModifiers.Shift);
        Press(ui, dispatcher, ImGuiKey.F);
        Press(ui, dispatcher, ImGuiKey.Z); // unbound without Ctrl
        Assert.Equal(new[] { EditorActions.Undo, EditorActions.Redo, EditorActions.FrameSelection }, ran);
    }

    [Fact]
    public void Suspended_dispatcher_and_disabled_commands_do_nothing()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        ui.Frame(() => { });
        var commands = new CommandRegistry();
        int runs = 0;
        commands.Register(new EditorCommand(EditorActions.Undo, Str.EditUndo, "", () => runs++, IsEnabled: () => false));
        commands.Register(new EditorCommand(EditorActions.FrameSelection, Str.ViewFrameSelection, "", () => runs++));
        var dispatcher = new KeyBindingDispatcher(commands, KeyBindingsService.CreateDefault());
        Press(ui, dispatcher, ImGuiKey.Z, KeyModifiers.Ctrl);
        Assert.Equal(0, runs);
        dispatcher.Suspended = true;
        Press(ui, dispatcher, ImGuiKey.F);
        Assert.Equal(0, runs);
        dispatcher.Suspended = false;
        Press(ui, dispatcher, ImGuiKey.F);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Plain_keys_go_to_a_focused_text_field_but_ctrl_chords_still_work()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        var (commands, ran) = Registry(EditorActions.FrameSelection, EditorActions.Undo);
        var dispatcher = new KeyBindingDispatcher(commands, KeyBindingsService.CreateDefault());
        string text = "";
        bool focus = true;
        void Field()
        {
            ImGui.Begin("field");
            if (focus)
            {
                ImGui.SetKeyboardFocusHere();
                focus = false;
            }

            ImGui.InputText("##name", ref text, 64);
            ImGui.End();
        }

        for (int i = 0; i < 4; i++)
            ui.Frame(Field); // focus request → active field → io.WantTextInput on the following frame
        Assert.True(ImGui.GetIO().WantTextInput);
        Press(ui, dispatcher, ImGuiKey.F, draw: Field);
        Assert.Empty(ran);
        Press(ui, dispatcher, ImGuiKey.Z, KeyModifiers.Ctrl, Field);
        Assert.Equal(new[] { EditorActions.Undo }, ran);
    }

    [Fact]
    public void Shortcuts_do_not_reach_the_scene_while_a_popup_or_dialog_is_open()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        ui.Frame(() => { });
        var (commands, ran) = Registry(EditorActions.Delete, EditorActions.DeselectAll, EditorActions.Undo);
        var dispatcher = new KeyBindingDispatcher(commands, KeyBindingsService.CreateDefault());
        bool open = true;
        bool openRequested = true;
        void Modal()
        {
            if (openRequested)
            {
                ImGui.OpenPopup("dialog");
                openRequested = false;
            }

            if (ImGui.BeginPopupModal("dialog"))
            {
                ImGui.TextUnformatted("A dialog");
                if (!open)
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        ui.Frame(Modal);
        Press(ui, dispatcher, ImGuiKey.Delete, draw: Modal);
        Press(ui, dispatcher, ImGuiKey.Z, KeyModifiers.Ctrl, Modal);
        Assert.Empty(ran);

        // Escape belongs to the dialog (its Cancel), not to "Deselect".
        Press(ui, dispatcher, ImGuiKey.Escape, draw: Modal);
        Assert.Empty(ran);

        open = false;
        ui.Frame(Modal);
        ui.Frame(Modal);
        Press(ui, dispatcher, ImGuiKey.Delete, draw: Modal);
        Assert.Equal(new[] { EditorActions.Delete }, ran);
    }

    [Fact]
    public void Escape_that_closes_a_menu_or_a_dialog_does_not_also_deselect()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        ui.Frame(() => { });
        var (commands, ran) = Registry(EditorActions.DeselectAll, EditorActions.Delete);
        var dispatcher = new KeyBindingDispatcher(commands, KeyBindingsService.CreateDefault());

        // A menu (non-modal popup): Dear ImGui closes it on Escape at the start of the frame, before the dispatcher runs.
        bool openMenu = true;
        bool menuShown = false;
        void Menu()
        {
            ImGui.Begin("host");
            if (openMenu)
            {
                ImGui.OpenPopup("menu");
                openMenu = false;
            }

            menuShown = ImGui.BeginPopup("menu");
            if (menuShown)
            {
                ImGui.MenuItem("Item");
                ImGui.EndPopup();
            }

            ImGui.End();
        }

        ui.Frame(() => { Menu(); dispatcher.Dispatch(); });
        ui.Frame(() => { Menu(); dispatcher.Dispatch(); });
        Assert.True(menuShown);
        Press(ui, dispatcher, ImGuiKey.Escape, draw: Menu);
        Assert.False(menuShown, "Escape closes the menu");
        Assert.Empty(ran);

        // A dialog that closes itself on Escape while it is drawn (message boxes, the file browser).
        bool openDialog = true;
        bool dialogShown = false;
        void Dialog()
        {
            dialogShown = false;
            if (openDialog)
            {
                ImGui.OpenPopup("dialog");
                openDialog = false;
            }

            if (ImGui.BeginPopupModal("dialog"))
            {
                dialogShown = true;
                ImGui.TextUnformatted("A dialog");
                if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        ui.Frame(() => { Dialog(); dispatcher.Dispatch(); });
        ui.Frame(() => { Dialog(); dispatcher.Dispatch(); });
        Assert.True(dialogShown);
        Press(ui, dispatcher, ImGuiKey.Escape, draw: Dialog);
        Assert.False(dialogShown, "Escape closes the dialog");
        Assert.Empty(ran);

        // With nothing open, Escape deselects again.
        Press(ui, dispatcher, ImGuiKey.Escape);
        Assert.Equal(new[] { EditorActions.DeselectAll }, ran);
    }

    [Fact]
    public void Escape_closes_a_focused_window_instead_of_deselecting()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        var (commands, ran) = Registry(EditorActions.DeselectAll);
        var dispatcher = new KeyBindingDispatcher(commands, KeyBindingsService.CreateDefault());
        bool windowOpen = true;
        string text = "";
        bool editText = false;
        void Window()
        {
            if (!windowOpen)
                return;
            ImGui.SetNextWindowFocus();
            ImGui.Begin("Settings");
            if (editText)
            {
                ImGui.SetKeyboardFocusHere();
                editText = false;
            }

            ImGui.InputText("##filter", ref text, 32);
            if (Candela.App.Windows.WindowKeys.CloseRequested(dispatcher))
                windowOpen = false;
            ImGui.End();
        }

        for (int i = 0; i < 3; i++)
            ui.Frame(() => { Window(); dispatcher.Dispatch(); });

        // While its text field is edited, Escape belongs to the field: the window stays and nothing is deselected.
        editText = true;
        for (int i = 0; i < 3; i++)
            ui.Frame(() => { Window(); dispatcher.Dispatch(); });
        Assert.True(ImGui.GetIO().WantTextInput);
        Press(ui, dispatcher, ImGuiKey.Escape, draw: Window);
        Assert.True(windowOpen);
        Assert.Empty(ran);

        // Then Escape closes the window, and only the window.
        for (int i = 0; i < 2; i++)
            ui.Frame(() => { Window(); dispatcher.Dispatch(); });
        Press(ui, dispatcher, ImGuiKey.Escape, draw: Window);
        Assert.False(windowOpen);
        Assert.Empty(ran);

        // With no window left, Escape deselects.
        Press(ui, dispatcher, ImGuiKey.Escape, draw: Window);
        Assert.Equal(new[] { EditorActions.DeselectAll }, ran);
    }

    [Fact]
    public void Rebuild_applies_new_bindings()
    {
        using var ui = new HeadlessImGui(new Vector2(800, 600));
        ui.Frame(() => { });
        var (commands, ran) = Registry(EditorActions.FrameSelection);
        var dispatcher = new KeyBindingDispatcher(commands, KeyBindingsService.CreateDefault());
        var custom = KeyBindingsService.CreateDefault();
        custom.Set(EditorActions.FrameSelection, new KeyChord("Period"));
        dispatcher.Rebuild(custom);
        Press(ui, dispatcher, ImGuiKey.F);
        Assert.Empty(ran);
        Press(ui, dispatcher, ImGuiKey.Period);
        Assert.Equal(new[] { EditorActions.FrameSelection }, ran);
    }
}
